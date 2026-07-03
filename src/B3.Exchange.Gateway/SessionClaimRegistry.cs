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

    /// <summary>
    /// Issue #405: boot-time seed for the highest-seen
    /// <c>sessionVerID</c> per session, recovered from a persisted
    /// <c>FixpSessionStateSnapshot</c>. Called once during host
    /// startup, before the FIXP listener accepts connections.
    /// Idempotent across calls — the registry monotonically keeps the
    /// maximum value seen. Calling this with <paramref name="sessionVerId"/>
    /// = 0 is a no-op (matches the "never negotiated" sentinel).
    /// </summary>
    /// <remarks>
    /// This is the mechanism that preserves the SBE 5.2 §4.5.2
    /// intraweek monotonicity rule across process restart: without
    /// it, a fresh-process registry would accept the peer's old
    /// <c>sessionVerID</c> and the spec-mandated "preventing affects
    /// on subsequent retransmission" guarantee would silently break.
    /// </remarks>
    public void SeedLastVersion(uint sessionId, ulong sessionVerId)
    {
        if (sessionVerId == 0UL) return;
        lock (_lock)
        {
            if (_lastSessionVerId.TryGetValue(sessionId, out var existing)
                && existing >= sessionVerId)
                return;
            _lastSessionVerId[sessionId] = sessionVerId;
        }
    }

    /// <summary>
    /// Issue #405 (review finding): re-claim a session whose
    /// <see cref="SeedLastVersion"/> entry survived a host crash, using
    /// the SAME <paramref name="sessionVerId"/> as the persisted snapshot.
    /// This is the spec §1.5 RECOVERABLE serverFlow resume path —
    /// the peer reconnects with Establish reusing its original
    /// SessionVerId, and the server is contractually obliged to
    /// resume that session rather than treat the verId as stale.
    /// Returns <see cref="ClaimResult.Accepted"/> only if no live
    /// claim is currently held AND the seeded verId matches exactly;
    /// any mismatch falls through to the normal monotonicity rules
    /// (caller should use <see cref="TryClaim"/> for the Negotiate
    /// path with strictly-greater verId).
    /// </summary>
    public ClaimResult TryReclaim(uint sessionId, ulong sessionVerId, object claimToken)
    {
        ArgumentNullException.ThrowIfNull(claimToken);
        if (sessionVerId == 0UL) return ClaimResult.ZeroVersion;

        lock (_lock)
        {
            if (_activeClaims.ContainsKey(sessionId))
                return ClaimResult.DuplicateConnection;
            if (!_lastSessionVerId.TryGetValue(sessionId, out var last) || sessionVerId != last)
                return ClaimResult.StaleVersion;

            _activeClaims[sessionId] = claimToken;
            return ClaimResult.Accepted;
        }
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
    /// Issue #488: atomically evict an existing claim and register a new one
    /// when the incoming <paramref name="sessionVerId"/> is strictly greater
    /// than the last recorded version for <paramref name="sessionId"/>. This
    /// enables the FIXP session-takeover path: a peer that crashed and
    /// restarted fast (before the server's idle-timeout fired) can reclaim
    /// its session without being blocked by <see cref="TryClaim"/> returning
    /// <see cref="ClaimResult.DuplicateConnection"/>.
    ///
    /// <para>All state mutations occur under <see cref="_lock"/>, so no
    /// intermediate state is visible to concurrent callers. The evicted token
    /// is returned via <paramref name="evictedToken"/> so the caller can close
    /// the old session AFTER the atomic swap; the old session's own
    /// <see cref="Release"/> call will then be a no-op (the dictionary value
    /// no longer matches its token).</para>
    ///
    /// <para>Returns <see cref="ClaimResult.Accepted"/> (with a non-null
    /// <paramref name="evictedToken"/>) when the takeover succeeds, or
    /// <see cref="ClaimResult.StaleVersion"/> when the version is not
    /// strictly greater (reject: this is not a legitimate reconnect).</para>
    /// </summary>
    public ClaimResult TryForceTakeOver(uint sessionId, ulong sessionVerId,
        object newToken, out object? evictedToken)
    {
        ArgumentNullException.ThrowIfNull(newToken);
        evictedToken = null;
        if (sessionVerId == 0UL) return ClaimResult.ZeroVersion;

        lock (_lock)
        {
            // New verId must be strictly greater than the last recorded
            // version — the FIXP monotonicity rule still applies.
            if (_lastSessionVerId.TryGetValue(sessionId, out var last) && sessionVerId <= last)
                return ClaimResult.StaleVersion;

            // Evict old claim atomically (may or may not exist).
            _activeClaims.TryGetValue(sessionId, out evictedToken);
            _activeClaims[sessionId] = newToken;
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
    /// reset the recorded last-seen sessionVerID unless
    /// <paramref name="forgetLastVersion"/> is true. Normal reconnects
    /// need monotonicity to survive for the process lifetime; daily
    /// trading-day resets explicitly clear it so SessionVerID starts
    /// fresh.
    /// </summary>
    public void Release(uint sessionId, object claimToken, bool forgetLastVersion = false)
    {
        ArgumentNullException.ThrowIfNull(claimToken);
        lock (_lock)
        {
            if (_activeClaims.TryGetValue(sessionId, out var owner) &&
                ReferenceEquals(owner, claimToken))
            {
                _activeClaims.Remove(sessionId);
            }
            if (forgetLastVersion)
            {
                _lastSessionVerId.Remove(sessionId);
            }
        }
    }

    /// <summary>
    /// Rolls back a failed <see cref="TryForceTakeOver"/> by atomically
    /// restoring the evicted session's claim and reverting
    /// <c>_lastSessionVerId</c> to the pre-takeover value. Only acts
    /// when <paramref name="newToken"/> still holds the active claim
    /// (guards against a concurrent takeover winning the registry
    /// between the failed persist and this restore call). Safe to call
    /// multiple times; idempotent when the claim has already moved on.
    /// Returns <c>true</c> when the claim was actually restored to
    /// <paramref name="oldToken"/>, so the caller can keep the
    /// <see cref="SessionRegistry"/> entry in lock-step; returns
    /// <c>false</c> when a concurrent takeover already moved the claim on
    /// (in which case the caller must not roll the registry back either).
    /// </summary>
    public bool TryRestoreTakeOver(uint sessionId, object newToken,
        object oldToken, ulong oldVerId)
    {
        ArgumentNullException.ThrowIfNull(newToken);
        ArgumentNullException.ThrowIfNull(oldToken);
        lock (_lock)
        {
            if (_activeClaims.TryGetValue(sessionId, out var current) &&
                ReferenceEquals(current, newToken))
            {
                _activeClaims[sessionId] = oldToken;
                _lastSessionVerId[sessionId] = oldVerId;
                return true;
            }
        }
        return false;
    }
}
