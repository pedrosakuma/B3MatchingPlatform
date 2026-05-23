namespace B3.Exchange.Gateway.Persistence;

/// <summary>
/// Issue #405: per-FIXP-session append-only journal of outbound
/// business frames, the durable source-of-truth for replay across
/// arbitrary disconnect durations and across matching-platform
/// process restarts.
///
/// <para><b>Why a journal, not a bounded ring snapshot</b>: the
/// existing <see cref="RetransmitBuffer"/> evicts oldest entries when
/// full. The previous on-disk persister (<c>FileFixpRetransmitPersister</c>,
/// #289) only mirrored the bounded in-memory window, so a peer that
/// disconnected long enough for the ring to roll over lost all
/// evicted ExecutionReports forever — even with persistence enabled.
/// The FIXP V6 server-flow is declared <c>recoverable</c> in
/// EstablishmentAck (B3 Binary EntryPoint SBE 5.2 §1.5, line 578 of
/// the PDF), which contractually obliges the gateway to replay any
/// missed business frame on demand. A bounded ring violates that
/// contract for any disconnect longer than the ring's lifetime.</para>
///
/// <para><b>Hot path</b>: <see cref="Append"/> is invoked under the
/// <see cref="FixpSession"/> outbound write lock for every business
/// frame the encoder emits. Implementations must keep the call cheap
/// (single fwrite + optional fsync window) to avoid back-pressuring
/// the encoder.</para>
///
/// <para><b>Replay path</b>: <see cref="ReadRange"/> is invoked by
/// the dispatch thread when handling a <c>RetransmitRequest</c> whose
/// <c>fromSeqNo</c> falls below the in-memory ring's lowest retained
/// seq. Returns frames in seq order; gaps inside the journal indicate
/// either corruption (logged at Critical) or a request that crosses
/// a prune-watermark.</para>
///
/// <para><b>Acked-watermark pruning</b>: <see cref="PruneUpTo"/> is
/// invoked when the peer signals (via <c>RetransmitRequest.fromSeqNo</c>
/// or by sending a new business message at high seq) that it has
/// successfully applied messages up to a known watermark. Implementations
/// should be conservative — never prune above the caller-supplied
/// watermark — and may defer the physical delete to a segment
/// boundary for I/O efficiency.</para>
///
/// <para><b>Terminal removal</b>: <see cref="Remove"/> is called by
/// <c>FixpSession.CloseLocked</c> only on terminal close events
/// (peer-initiated <c>Terminate(Finished)</c>, suspended-timeout
/// reaper). Graceful host shutdown does NOT remove journals — they
/// must survive process restart so a reconnecting peer can recover
/// every event produced while the matching-platform was down.</para>
///
/// <para><b>Boot rehydration</b>: <see cref="ListSessions"/> enumerates
/// every session whose journal survived the last process exit. The
/// host calls this once at boot, then for each session derives the
/// outbound seq counter via <see cref="MaxSeq"/> so a fresh
/// <see cref="FixpSession"/> built post-restart resumes producing
/// frames at the correct seq (no collision with the persisted
/// journal).</para>
/// </summary>
public interface IFixpOutboundJournal : IDisposable
{
    /// <summary>
    /// Persists a single appended frame for <paramref name="sessionId"/>.
    /// Called on the FixpSession outbound write lock, so this method
    /// MUST NOT block on any cross-session resource. The frame buffer
    /// may be retained by the implementation; callers must not mutate
    /// it after the call.
    /// </summary>
    /// <param name="sessionId">FIXP wire SessionID (uint32) of the
    /// owning session.</param>
    /// <param name="seq">FIXP outbound <c>MsgSeqNum</c> assigned to
    /// this frame. Must be strictly greater than the previous
    /// <c>Append</c> for the same <paramref name="sessionId"/> — gaps
    /// or out-of-order writes indicate a producer bug and may be
    /// rejected by the implementation.</param>
    /// <param name="timestampNanos">Nanoseconds since Unix epoch at
    /// append time; informational, used for diagnostic logging and
    /// time-based retention policies.</param>
    /// <param name="frame">The wire frame bytes
    /// (SOFH + SBE header + body) exactly as enqueued on the
    /// transport. Must not be empty.</param>
    void Append(uint sessionId, uint seq, long timestampNanos, ReadOnlySpan<byte> frame);

    /// <summary>
    /// Returns up to <paramref name="count"/> consecutive entries
    /// from <paramref name="sessionId"/>'s journal starting at
    /// <paramref name="fromSeq"/> (inclusive). Returns an empty list
    /// when no matching entry exists. Implementations must return
    /// entries in strictly increasing <c>Seq</c> order with no gaps
    /// inside the returned slice; if a gap is detected (e.g. the
    /// journal was pruned in the middle of the requested window) the
    /// implementation must stop at the gap and return only the
    /// contiguous prefix starting at <paramref name="fromSeq"/>.
    /// </summary>
    IReadOnlyList<OutboundJournalEntry> ReadRange(uint sessionId, uint fromSeq, int count);

    /// <summary>
    /// Discards every persisted entry for <paramref name="sessionId"/>
    /// with <c>Seq &lt;= uptoSeq</c>. Conservative semantics: the
    /// implementation may retain entries above the watermark even
    /// when it would be cheaper to delete the entire segment they
    /// live in. Idempotent; safe to call with watermarks below the
    /// current pruned floor.
    /// </summary>
    void PruneUpTo(uint sessionId, uint uptoSeq);

    /// <summary>
    /// Returns the highest <c>Seq</c> currently persisted for
    /// <paramref name="sessionId"/>, or 0 when the session has no
    /// journal (either never appended, or fully pruned, or removed).
    /// Used at boot to seed <c>FixpSession._msgSeqNum</c> so the
    /// next outbound frame allocates <c>MaxSeq + 1</c> and does not
    /// collide with the persisted ring.
    /// </summary>
    uint MaxSeq(uint sessionId);

    /// <summary>
    /// Returns the number of entries currently persisted for
    /// <paramref name="sessionId"/>. Diagnostic / health-check use
    /// only; not on any hot path.
    /// </summary>
    long EntryCount(uint sessionId);

    /// <summary>
    /// Terminal removal: deletes every artifact for
    /// <paramref name="sessionId"/>. Idempotent — safe to call when
    /// nothing exists for the session. Called only on peer-initiated
    /// <c>Terminate(Finished)</c> and on suspended-timeout reaping.
    /// Graceful host shutdown must NOT call this.
    /// </summary>
    void Remove(uint sessionId);

    /// <summary>
    /// Enumerates the SessionIDs whose journals are recoverable from
    /// the data directory. Called once at host boot, before the FIXP
    /// listener accepts connections. Returns an empty collection
    /// when the directory does not exist or contains no session
    /// journals.
    /// </summary>
    IReadOnlyCollection<uint> ListSessions();
}
