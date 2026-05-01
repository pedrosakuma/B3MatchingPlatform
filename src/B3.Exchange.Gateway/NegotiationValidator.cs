using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.Gateway;

/// <summary>
/// Decoded fixed-block fields of a FIXP <c>Negotiate</c> message (V6
/// schema, MESSAGE_ID = 1, BLOCK_LENGTH = 28). The credentials varData
/// segment is parsed separately into <see cref="NegotiateCredentials"/>.
/// </summary>
public readonly record struct NegotiateRequest(
    uint SessionId,
    ulong SessionVerId,
    ulong TimestampNanos,
    uint EnteringFirm,
    uint? OnBehalfFirm);

/// <summary>
/// Result of <see cref="NegotiationValidator.Validate"/>. Either the
/// peer was accepted (carrying the resolved <see cref="Firm"/> and
/// <see cref="SessionCredential"/> for the dispatch loop to install on
/// the <see cref="FixpSession"/>) or it was rejected with one of the
/// FIXP <c>NegotiationRejectCode</c> values.
/// </summary>
public readonly struct NegotiationOutcome
{
    public bool IsAccepted { get; }
    public FixpSbe.NegotiationRejectCode RejectCode { get; }
    public string? RejectReason { get; }
    /// <summary>For <c>ALREADY_NEGOTIATED</c>: the current session
    /// version that must be echoed in the reject frame per §4.5.3.1.
    /// Zero when not applicable.</summary>
    public ulong CurrentSessionVerId { get; }
    public SessionCredential? Credential { get; }
    public Firm? Firm { get; }

    private NegotiationOutcome(bool ok, FixpSbe.NegotiationRejectCode code, string? reason,
        ulong currentSessionVerId, SessionCredential? cred, Firm? firm)
    {
        IsAccepted = ok;
        RejectCode = code;
        RejectReason = reason;
        CurrentSessionVerId = currentSessionVerId;
        Credential = cred;
        Firm = firm;
    }

    public static NegotiationOutcome Accept(SessionCredential credential, Firm firm)
        => new(true, FixpSbe.NegotiationRejectCode.UNSPECIFIED, null, 0UL, credential, firm);

    public static NegotiationOutcome Reject(FixpSbe.NegotiationRejectCode code, string reason,
        ulong currentSessionVerId = 0UL)
        => new(false, code, reason, currentSessionVerId, null, null);
}

/// <summary>
/// Pure (no IO, no socket) validator that maps an inbound
/// <see cref="NegotiateRequest"/> + parsed <see cref="NegotiateCredentials"/>
/// to a <see cref="NegotiationOutcome"/>. Encapsulates every spec rule
/// from §4.5.2/§4.5.3:
/// <list type="bullet">
///   <item>Auth type <c>basic</c> only.</item>
///   <item><c>username</c> must equal the wire <c>sessionID</c> (decimal
///   string).</item>
///   <item>Session must exist in <see cref="FirmRegistry"/>.</item>
///   <item><c>enteringFirm</c> must match the credential's owning
///   firm's <c>EnteringFirmCode</c>.</item>
///   <item><c>access_key</c> must match (when the credential declares
///   one) — bypassed only when <c>devMode</c> is true.</item>
///   <item><c>sessionVerID</c> must be non-zero and strictly greater
///   than the last seen value (claim registry).</item>
///   <item>No other transport may currently hold a claim on the same
///   <c>sessionID</c> (DUPLICATE_SESSION_CONNECTION).</item>
///   <item>Caller passes <c>currentSessionState</c> = <see cref="FixpState.Negotiated"/>
///   or <see cref="FixpState.Established"/> if the same TCP connection
///   already negotiated — surfaces as ALREADY_NEGOTIATED.</item>
/// </list>
///
/// <para>The validator does <em>not</em> mutate the claim registry; the
/// caller's dispatch loop calls <see cref="SessionClaimRegistry.TryClaim"/>
/// after deciding to apply the lifecycle transition. This keeps the
/// validator pure and testable.</para>
/// </summary>
public sealed class NegotiationValidator
{
    private readonly FirmRegistry _firms;
    private readonly SessionClaimRegistry _claims;
    private readonly bool _devMode;
    private readonly Func<ulong> _nowNanos;
    /// <summary>Maximum tolerated absolute clock skew (nanoseconds)
    /// between server and Negotiate <c>timestamp</c>. Zero disables the
    /// check (used by tests). Default 5 minutes.</summary>
    public ulong TimestampSkewToleranceNs { get; }

    public NegotiationValidator(FirmRegistry firms, SessionClaimRegistry claims,
        bool devMode, Func<ulong>? nowNanos = null,
        ulong timestampSkewToleranceNs = 5UL * 60UL * 1_000_000_000UL)
    {
        _firms = firms ?? throw new ArgumentNullException(nameof(firms));
        _claims = claims ?? throw new ArgumentNullException(nameof(claims));
        _devMode = devMode;
        _nowNanos = nowNanos ?? (() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
        TimestampSkewToleranceNs = timestampSkewToleranceNs;
    }

    /// <param name="currentSessionState">The session's lifecycle state
    /// at the moment Negotiate arrives. <see cref="FixpState.Idle"/> for
    /// a fresh handshake; anything else triggers
    /// <c>ALREADY_NEGOTIATED</c> with the recorded session version.</param>
    public NegotiationOutcome Validate(in NegotiateRequest req,
        in NegotiateCredentials credentials, FixpState currentSessionState)
    {
        if (currentSessionState != FixpState.Idle)
        {
            // §4.5.3.1: the reject MUST echo the currently-active
            // sessionVerID so the client can recover its session state.
            return NegotiationOutcome.Reject(
                FixpSbe.NegotiationRejectCode.ALREADY_NEGOTIATED,
                $"already in state {currentSessionState}",
                _claims.CurrentSessionVerId(req.SessionId));
        }

        // Cheap structural checks first.
        if (req.SessionId == 0)
            return NegotiationOutcome.Reject(
                FixpSbe.NegotiationRejectCode.INVALID_SESSIONID, "sessionID is zero (null value)");

        if (TimestampSkewToleranceNs > 0 && req.TimestampNanos != 0)
        {
            var now = _nowNanos();
            var diff = req.TimestampNanos > now ? req.TimestampNanos - now : now - req.TimestampNanos;
            if (diff > TimestampSkewToleranceNs)
                return NegotiationOutcome.Reject(
                    FixpSbe.NegotiationRejectCode.INVALID_TIMESTAMP,
                    $"timestamp skew {diff}ns exceeds {TimestampSkewToleranceNs}ns");
        }

        if (!string.Equals(credentials.AuthType, "basic", StringComparison.Ordinal))
            return NegotiationOutcome.Reject(
                FixpSbe.NegotiationRejectCode.CREDENTIALS,
                $"auth_type='{credentials.AuthType}' not supported (only 'basic')");

        // §4.5.2: username MUST equal the wire SessionID expressed as a
        // decimal string. We canonicalise on the server side so the
        // comparison is unambiguous regardless of zero-padding the
        // client may apply.
        var sessionIdString = req.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(credentials.Username, sessionIdString, StringComparison.Ordinal))
            return NegotiationOutcome.Reject(
                FixpSbe.NegotiationRejectCode.CREDENTIALS,
                $"username '{credentials.Username}' does not match sessionID {req.SessionId}");

        var cred = _firms.FindSessionByWire(req.SessionId);
        if (cred is null)
            return NegotiationOutcome.Reject(
                FixpSbe.NegotiationRejectCode.CREDENTIALS,
                $"unknown sessionID {req.SessionId}");

        var firm = _firms.FindFirm(cred.FirmId)
            ?? throw new InvalidOperationException(
                $"BUG: session '{cred.SessionId}' references firm '{cred.FirmId}' missing from registry");

        if (req.EnteringFirm != firm.EnteringFirmCode)
            return NegotiationOutcome.Reject(
                FixpSbe.NegotiationRejectCode.INVALID_FIRM,
                $"enteringFirm {req.EnteringFirm} does not match registered firm code {firm.EnteringFirmCode}");

        if (!_devMode && !string.IsNullOrEmpty(cred.AccessKey) &&
            !string.Equals(credentials.AccessKey, cred.AccessKey, StringComparison.Ordinal))
        {
            return NegotiationOutcome.Reject(
                FixpSbe.NegotiationRejectCode.CREDENTIALS, "access_key mismatch");
        }

        // sessionVerID monotonicity is enforced by the claim registry at
        // commit time (TryClaim). We surface a specific reject here when
        // we can detect the failure cheaply, so callers don't have to
        // race-then-retry.
        if (req.SessionVerId == 0UL)
            return NegotiationOutcome.Reject(
                FixpSbe.NegotiationRejectCode.INVALID_SESSIONVERID,
                "sessionVerID is zero (null value)");
        var lastSeen = _claims.CurrentSessionVerId(req.SessionId);
        if (lastSeen != 0UL && req.SessionVerId <= lastSeen)
            return NegotiationOutcome.Reject(
                FixpSbe.NegotiationRejectCode.INVALID_SESSIONVERID,
                $"sessionVerID {req.SessionVerId} is not strictly greater than last-seen {lastSeen}");

        return NegotiationOutcome.Accept(cred, firm);
    }
}
