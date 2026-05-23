namespace B3.Exchange.Gateway.Persistence;

/// <summary>
/// Issue #405: durable home for <see cref="FixpSessionStateSnapshot"/>
/// — the per-session FIXP envelope metadata (SessionVerID, seq
/// counters, EnteringFirm) that must survive process restart so a
/// reconnecting peer's next Establish lands on a server with the
/// correct monotonicity invariants.
///
/// <para><b>Write path</b>: <see cref="Save"/> is called by
/// <see cref="FixpSession"/> on every meaningful state transition
/// (Establish success, business-frame dispatch, EstablishmentAck
/// emission, terminate without remove). Implementations must commit
/// atomically — a partial write must never be observed at boot.</para>
///
/// <para><b>Read path</b>: <see cref="LoadAll"/> is called once at
/// host boot before the FIXP listener accepts connections.
/// <see cref="Load"/> may be used for targeted lookups (e.g.,
/// claim-registry seeding) on the same data set.</para>
///
/// <para><b>Removal</b>: <see cref="Remove"/> is called only on
/// terminal close events (peer-initiated <c>Terminate(Finished)</c>,
/// suspended-timeout reaper). Graceful host shutdown must NOT call
/// this — the snapshot must survive process exit so a peer
/// reconnecting after the host comes back up sees a session-version
/// strictly greater than any it has seen.</para>
/// </summary>
public interface IFixpSessionStatePersister : IDisposable
{
    /// <summary>
    /// Atomically writes <paramref name="snapshot"/>. Subsequent
    /// loads for the same <c>SessionId</c> return the value just
    /// written; a crash mid-write must leave either the previous
    /// snapshot or the new one — never a torn record.
    /// </summary>
    void Save(in FixpSessionStateSnapshot snapshot);

    /// <summary>
    /// Returns the most recently saved snapshot for
    /// <paramref name="sessionId"/>, or <c>null</c> when nothing has
    /// ever been persisted for it (or it has been removed).
    /// </summary>
    FixpSessionStateSnapshot? Load(uint sessionId);

    /// <summary>
    /// Enumerates every persisted snapshot. Called once at boot to
    /// rehydrate the claim registry and the suspended-session set.
    /// </summary>
    IReadOnlyCollection<FixpSessionStateSnapshot> LoadAll();

    /// <summary>
    /// Terminal removal. Idempotent — safe to call when nothing
    /// exists for the session. Graceful host shutdown must NOT call
    /// this; see interface remarks.
    /// </summary>
    void Remove(uint sessionId);
}
