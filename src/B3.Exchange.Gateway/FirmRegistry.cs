namespace B3.Exchange.Gateway;

/// <summary>
/// Immutable, startup-loaded registry of <see cref="Firm"/> records and
/// their associated <see cref="SessionCredential"/>s. Authoritative for
/// "is this firm/session known and what is its policy?"
///
/// <para>See <c>docs/B3-ENTRYPOINT-ARCHITECTURE.md</c> §4.6 and §8 for the
/// configuration schema. Validation is performed at construction time;
/// startup fails fast on any inconsistency (unknown <c>firmId</c>,
/// duplicate <c>sessionId</c>, etc.).</para>
///
/// <para>Thread-safety: read-only after construction; safe for concurrent
/// lookups from any thread.</para>
/// </summary>
public sealed class FirmRegistry
{
    private readonly IReadOnlyDictionary<string, Firm> _firms;
    private readonly IReadOnlyDictionary<string, SessionCredential> _credentials;
    private readonly IReadOnlyDictionary<uint, SessionCredential> _credentialsByWire;

    /// <summary>All firms keyed by <see cref="Firm.Id"/>.</summary>
    public IReadOnlyDictionary<string, Firm> Firms => _firms;

    /// <summary>All session credentials keyed by <see cref="SessionCredential.SessionId"/>.</summary>
    public IReadOnlyDictionary<string, SessionCredential> Credentials => _credentials;

    public FirmRegistry(IEnumerable<Firm> firms, IEnumerable<SessionCredential> sessions)
    {
        ArgumentNullException.ThrowIfNull(firms);
        ArgumentNullException.ThrowIfNull(sessions);

        var firmMap = new Dictionary<string, Firm>(StringComparer.Ordinal);
        foreach (var f in firms)
        {
            ArgumentNullException.ThrowIfNull(f);
            if (string.IsNullOrWhiteSpace(f.Id))
                throw new InvalidOperationException("Firm.Id must be non-empty");
            if (!firmMap.TryAdd(f.Id, f))
                throw new InvalidOperationException($"duplicate firm id: '{f.Id}'");
        }

        var credMap = new Dictionary<string, SessionCredential>(StringComparer.Ordinal);
        var wireMap = new Dictionary<uint, SessionCredential>();
        foreach (var s in sessions)
        {
            ArgumentNullException.ThrowIfNull(s);
            if (string.IsNullOrWhiteSpace(s.SessionId))
                throw new InvalidOperationException("SessionCredential.SessionId must be non-empty");
            // FIXP wire SessionID is uint32; require config sessionId to
            // parse as a decimal uint32 so credential lookup at Negotiate
            // time is unambiguous (see B3-ENTRYPOINT-ARCHITECTURE.md §4.2).
            if (!uint.TryParse(s.SessionId, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var wireId))
                throw new InvalidOperationException(
                    $"SessionCredential.SessionId '{s.SessionId}' must parse as a decimal uint32 " +
                    "(FIXP wire SessionID is uint32 per spec §4.5.2)");
            if (wireId == 0)
                throw new InvalidOperationException(
                    $"SessionCredential.SessionId '{s.SessionId}' must be > 0 (zero is the FIXP null value)");
            // Reject leading zeros so the (string sessionId) → (uint32) map is
            // truly bijective. NumberStyles.None blocks signs/whitespace but
            // still accepts "001" → 1, which would let two distinct
            // SessionCredential.SessionId values collide in wireMap.Add below.
            if (s.SessionId.Length > 1 && s.SessionId[0] == '0')
                throw new InvalidOperationException(
                    $"SessionCredential.SessionId '{s.SessionId}' must not have leading zeros " +
                    "(canonical decimal required so the wire-uint32 mapping is bijective)");
            if (!firmMap.ContainsKey(s.FirmId))
                throw new InvalidOperationException(
                    $"session '{s.SessionId}' references unknown firm '{s.FirmId}' " +
                    $"(known firms: {string.Join(", ", firmMap.Keys)})");
            s.Policy.Validate();
            if (!credMap.TryAdd(s.SessionId, s))
                throw new InvalidOperationException($"duplicate session id: '{s.SessionId}'");
            // wire index uniqueness is implied by string uniqueness + the
            // canonical-decimal rule above (no leading zeros / signs allowed
            // by NumberStyles.None, so the parse is bijective).
            wireMap.Add(wireId, s);
        }

        _firms = firmMap;
        _credentials = credMap;
        _credentialsByWire = wireMap;
    }

    /// <summary>
    /// Resolves a <see cref="SessionCredential"/> by FIXP sessionID. Returns
    /// <c>null</c> when unknown — callers should respond with
    /// <c>NegotiateReject(Credentials)</c> in that case.
    /// </summary>
    public SessionCredential? FindSession(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return _credentials.TryGetValue(sessionId, out var c) ? c : null;
    }

    /// <summary>
    /// Resolves a <see cref="SessionCredential"/> by the wire-format
    /// uint32 SessionID (the encoding used by FIXP <c>Negotiate</c>).
    /// Returns <c>null</c> when unknown.
    /// </summary>
    public SessionCredential? FindSessionByWire(uint sessionId)
        => _credentialsByWire.TryGetValue(sessionId, out var c) ? c : null;

    /// <summary>
    /// Resolves a <see cref="Firm"/> by FirmId. Returns <c>null</c> when
    /// unknown.
    /// </summary>
    public Firm? FindFirm(string firmId)
    {
        ArgumentNullException.ThrowIfNull(firmId);
        return _firms.TryGetValue(firmId, out var f) ? f : null;
    }

    /// <summary>
    /// Convenience: returns the <see cref="Firm"/> that owns the given
    /// session, or <c>null</c> if either lookup fails. Used by FixpSession
    /// after Negotiate to stamp outbound ER frames with the right
    /// <c>EnteringFirmCode</c>.
    /// </summary>
    public Firm? FirmOf(string sessionId)
    {
        var c = FindSession(sessionId);
        return c is null ? null : FindFirm(c.FirmId);
    }
}
