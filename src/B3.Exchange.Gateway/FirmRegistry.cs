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
        foreach (var s in sessions)
        {
            ArgumentNullException.ThrowIfNull(s);
            if (string.IsNullOrWhiteSpace(s.SessionId))
                throw new InvalidOperationException("SessionCredential.SessionId must be non-empty");
            if (!firmMap.ContainsKey(s.FirmId))
                throw new InvalidOperationException(
                    $"session '{s.SessionId}' references unknown firm '{s.FirmId}' " +
                    $"(known firms: {string.Join(", ", firmMap.Keys)})");
            s.Policy.Validate();
            if (!credMap.TryAdd(s.SessionId, s))
                throw new InvalidOperationException($"duplicate session id: '{s.SessionId}'");
        }

        _firms = firmMap;
        _credentials = credMap;
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
