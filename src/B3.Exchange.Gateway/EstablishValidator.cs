using B3.EntryPoint.Wire;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.Gateway;

/// <summary>
/// Outcome of validating an inbound FIXP <c>Establish</c> request
/// against the session's current state and identity. On <see cref="IsAccepted"/>
/// the dispatch loop should commit the negotiated keepAlive interval and
/// transition to <see cref="FixpState.Established"/>. On reject the
/// caller emits an <c>EstablishReject(<see cref="RejectCode"/>)</c>
/// followed by a <c>Terminate</c>.
/// </summary>
public readonly struct EstablishOutcome
{
    public bool IsAccepted { get; }
    public FixpSbe.EstablishRejectCode RejectCode { get; }
    public string? RejectReason { get; }
    /// <summary>For <c>INVALID_NEXTSEQNO</c>: the server's last accepted
    /// inbound application <c>MsgSeqNum</c> (per spec §4.5.3.1, the
    /// peer should resume from <c>lastIncomingSeqNo + 1</c>).</summary>
    public uint? LastIncomingSeqNo { get; }

    private EstablishOutcome(bool ok, FixpSbe.EstablishRejectCode code, string? reason, uint? lastSeq)
    {
        IsAccepted = ok;
        RejectCode = code;
        RejectReason = reason;
        LastIncomingSeqNo = lastSeq;
    }

    public static EstablishOutcome Accept()
        => new(true, FixpSbe.EstablishRejectCode.UNSPECIFIED, null, null);

    public static EstablishOutcome Reject(FixpSbe.EstablishRejectCode code, string reason,
        uint? lastIncomingSeqNo = null)
        => new(false, code, reason, lastIncomingSeqNo);
}

/// <summary>
/// Pure (no IO, no socket) validator for the FIXP <c>Establish</c>
/// handshake. Encapsulates every spec rule from §4.5.3 so the
/// <see cref="FixpSession"/> dispatch loop can stay a thin orchestration
/// layer.
///
/// <para>Rule order is significant — the implementation walks rules
/// cheapest-first so a malformed request is rejected with the most
/// informative reject code. The <em>state</em> rule comes first because
/// it must be honored even if other fields are corrupt
/// (e.g. <c>UNNEGOTIATED</c> wins over <c>INVALID_KEEPALIVE_INTERVAL</c>
/// before the peer has any business sending Establish).</para>
/// </summary>
public sealed class EstablishValidator
{
    private readonly Func<ulong> _nowNanos;
    /// <summary>Maximum tolerated absolute clock skew (nanoseconds)
    /// between server and Establish <c>timestamp</c>. Zero disables the
    /// check (used by tests). Default 5 minutes.</summary>
    public ulong TimestampSkewToleranceNs { get; }

    /// <summary>Inclusive lower bound for <c>keepAliveInterval</c> (ms).
    /// Issue #43 acceptance criterion specifies <c>[1, 60000]</c>; the
    /// SBE schema field description says <c>1000–60000</c>. We follow
    /// the issue.</summary>
    public const ulong MinKeepAliveIntervalMs = 1;
    /// <summary>Inclusive upper bound for <c>keepAliveInterval</c> (ms).</summary>
    public const ulong MaxKeepAliveIntervalMs = 60_000;

    public EstablishValidator(Func<ulong>? nowNanos = null,
        ulong timestampSkewToleranceNs = 5UL * 60UL * 1_000_000_000UL)
    {
        _nowNanos = nowNanos ?? (() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
        TimestampSkewToleranceNs = timestampSkewToleranceNs;
    }

    /// <param name="negotiatedSessionId">The wire-format SessionID
    /// claimed during the preceding <c>Negotiate</c>. The Establish
    /// frame must carry the same id, otherwise <c>INVALID_SESSIONID</c>.</param>
    /// <param name="negotiatedSessionVerId">The <c>sessionVerID</c>
    /// recorded at Negotiate time; mismatch ⇒ <c>INVALID_SESSIONVERID</c>.</param>
    /// <param name="lastIncomingSeqNo">Highest inbound application
    /// <c>MsgSeqNum</c> previously accepted on this session
    /// (0 for a fresh session). Used by <c>INVALID_NEXTSEQNO</c>.</param>
    public EstablishOutcome Validate(in EstablishRequest req,
        FixpState currentSessionState,
        uint negotiatedSessionId, ulong negotiatedSessionVerId,
        uint lastIncomingSeqNo)
    {
        // §4.5.3 strict-gating: order matters. State first because the
        // peer is forbidden from sending Establish before Negotiate.
        if (currentSessionState == FixpState.Idle)
            return EstablishOutcome.Reject(
                FixpSbe.EstablishRejectCode.UNNEGOTIATED,
                "Establish received before Negotiate");
        if (currentSessionState == FixpState.Established)
            return EstablishOutcome.Reject(
                FixpSbe.EstablishRejectCode.ALREADY_ESTABLISHED,
                "Establish received after session already established");

        // Identity must match Negotiate. Spec keeps these as separate
        // codes so the client can correlate which field disagrees.
        if (req.SessionId != negotiatedSessionId)
            return EstablishOutcome.Reject(
                FixpSbe.EstablishRejectCode.INVALID_SESSIONID,
                $"sessionID {req.SessionId} does not match negotiated {negotiatedSessionId}");
        if (req.SessionVerId != negotiatedSessionVerId)
            return EstablishOutcome.Reject(
                FixpSbe.EstablishRejectCode.INVALID_SESSIONVERID,
                $"sessionVerID {req.SessionVerId} does not match negotiated {negotiatedSessionVerId}");

        if (TimestampSkewToleranceNs > 0 && req.TimestampNanos != 0)
        {
            var now = _nowNanos();
            var diff = req.TimestampNanos > now ? req.TimestampNanos - now : now - req.TimestampNanos;
            if (diff > TimestampSkewToleranceNs)
                return EstablishOutcome.Reject(
                    FixpSbe.EstablishRejectCode.INVALID_TIMESTAMP,
                    $"timestamp skew {diff}ns exceeds {TimestampSkewToleranceNs}ns");
        }

        if (req.KeepAliveIntervalMillis < MinKeepAliveIntervalMs ||
            req.KeepAliveIntervalMillis > MaxKeepAliveIntervalMs)
        {
            return EstablishOutcome.Reject(
                FixpSbe.EstablishRejectCode.INVALID_KEEPALIVE_INTERVAL,
                $"keepAliveInterval {req.KeepAliveIntervalMillis}ms is outside " +
                $"[{MinKeepAliveIntervalMs},{MaxKeepAliveIntervalMs}]");
        }

        // We do not yet enforce strict in-order MsgSeqNum on inbound
        // application messages — that is #GAP-07. For now reject only
        // the obviously-invalid sentinel value of 0 so we don't claim
        // semantics we don't implement.
        if (req.NextSeqNo == 0)
            return EstablishOutcome.Reject(
                FixpSbe.EstablishRejectCode.INVALID_NEXTSEQNO,
                "nextSeqNo is zero (must be >= 1)",
                lastIncomingSeqNo: lastIncomingSeqNo);

        return EstablishOutcome.Accept();
    }
}
