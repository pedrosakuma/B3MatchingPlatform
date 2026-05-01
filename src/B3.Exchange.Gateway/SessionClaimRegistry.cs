namespace B3.Exchange.Gateway;

/// <summary>
/// Process-wide ledger of <em>active</em> FIXP session claims plus the
/// last-seen <c>sessionVerID</c> for each known session. Backs the
/// <c>DuplicateSessionConnection</c> and <c>InvalidSessionVerID</c>
/// reject paths in <see cref="NegotiationValidator"/>.
///
/// <para>Distinct from <see cref="SessionRegistry"/> (which maps the
/// transport-neutral <c>Contracts.SessionId</c> to a live
/// <see cref="FixpSession"/>): this registry deals in FIXP wire
/// <c>uint32</c> session IDs and is concerned with handshake-time
/// invariants only.</para>
///
/// <para>Thread-safety: backed by a single lock; contention is bounded
/// by Negotiate frequency (handshake-only, very low rate).</para>
/// </summary>
public sealed class SessionClaimRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<uint, ulong> _lastSessionVerId = new();
    private readonly Dictionary<uint, object> _activeClaims = new();

    /// <summary>Number of currently-claimed sessionIDs.</summary>
    public int ActiveCount
    {
        get { lock (_lock) return _activeClaims.Count; }
    }

    /// <summary>
    /// The highest <c>sessionVerID</c> ever seen for the given session,
    /// or 0 if the session has never been negotiated. Exposed for the
    /// <c>NegotiateReject(AlreadyNegotiated)</c> path which must echo
    /// the current session version per spec §4.5.3.1.
    /// </summary>
    public ulong CurrentSessionVerId(uint sessionId)
    {
        lock (_lock) return _lastSessionVerId.TryGetValue(sessionId, out var v) ? v : 0UL;
    }

    /// <summary>Outcome of <see cref="TryClaim"/>.</summary>
    public enum ClaimResult
    {
        /// <summary>Claim accepted; sessionVerID recorded as latest.</summary>
        Accepted,
        /// <summary>Another live transport already owns this session; the
        /// new request must be rejected with
        /// <c>DUPLICATE_SESSION_CONNECTION</c>.</summary>
        DuplicateConnection,
        /// <summary>The proposed sessionVerID is &lt;= the highest seen
        /// for this session (spec §4.5.2 monotonicity rule). Reject with
        /// <c>INVALID_SESSIONVERID</c>.</summary>
        StaleVersion,
        /// <summary>sessionVerID is zero (FIXP null value). Reject with
        /// <c>INVALID_SESSIONVERID</c>.</summary>
        ZeroVersion,
    }

    /// <summary>
    /// Atomically validate <paramref name="sessionVerId"/> against the
    /// monotonic-per-process rule and (on success) register
    /// <paramref name="claimToken"/> as the live owner of
    /// <paramref name="sessionId"/>.
    /// </summary>
    /// <param name="claimToken">An opaque identity (typically the
    /// <see cref="FixpSession"/>) used to scope <see cref="Release"/>
    /// so a stale teardown of an already-replaced claim is a no-op.</param>
    public ClaimResult TryClaim(uint sessionId, ulong sessionVerId, object claimToken)
    {
        ArgumentNullException.ThrowIfNull(claimToken);
        if (sessionVerId == 0UL) return ClaimResult.ZeroVersion;

        lock (_lock)
        {
            if (_activeClaims.ContainsKey(sessionId))
                return ClaimResult.DuplicateConnection;
            if (_lastSessionVerId.TryGetValue(sessionId, out var last) && sessionVerId <= last)
                return ClaimResult.StaleVersion;

            _activeClaims[sessionId] = claimToken;
            _lastSessionVerId[sessionId] = sessionVerId;
            return ClaimResult.Accepted;
        }
    }

    /// <summary>
    /// Returns the live owner of <paramref name="sessionId"/>, if any,
    /// along with the recorded last-seen sessionVerID. Used by the
    /// rebind path (#69b-2): if the new transport's <c>Establish</c>
    /// targets a sessionId currently claimed by a <see cref="FixpSession"/>
    /// in <see cref="FixpState.Suspended"/> with matching sessionVerID,
    /// the listener routes the new transport to that existing session
    /// instead of constructing a fresh one.
    ///
    /// <para>Returns <c>false</c> if no claim is currently held. Returns
    /// <c>true</c> with <paramref name="holder"/> set to the live owner
    /// otherwise; the caller is responsible for downcasting to the
    /// concrete session type.</para>
    /// </summary>
    public bool TryGetActiveClaim(uint sessionId, out object holder, out ulong sessionVerId)
    {
        lock (_lock)
        {
            if (_activeClaims.TryGetValue(sessionId, out var owner))
            {
                holder = owner;
                sessionVerId = _lastSessionVerId.TryGetValue(sessionId, out var v) ? v : 0UL;
                return true;
            }
        }
        holder = null!;
        sessionVerId = 0UL;
        return false;
    }

    /// <summary>
    /// Releases the claim for <paramref name="sessionId"/> if (and only
    /// if) it is currently held by <paramref name="claimToken"/>. Safe to
    /// call from any thread; safe to call multiple times. Does not
    /// reset the recorded last-seen sessionVerID — that persists for
    /// the life of the process so reconnects can be checked for
    /// monotonicity.
    /// </summary>
    public void Release(uint sessionId, object claimToken)
    {
        ArgumentNullException.ThrowIfNull(claimToken);
        lock (_lock)
        {
            if (_activeClaims.TryGetValue(sessionId, out var owner) &&
                ReferenceEquals(owner, claimToken))
            {
                _activeClaims.Remove(sessionId);
            }
        }
    }
}
