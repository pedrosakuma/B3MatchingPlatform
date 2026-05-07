namespace B3.Exchange.Gateway.Persistence;

/// <summary>
/// Issue #289: per-FIXP-session durable mirror of the in-memory
/// <see cref="RetransmitBuffer"/>. Closes the gap where a host crash
/// while a session is <c>Suspended</c> would erase every buffered
/// passive ExecutionReport, leaving a reattaching peer with a
/// retransmit window that no longer covers its <c>NextSeqNo</c>.
///
/// <para>Append is hot-path: invoked under the FixpSession outbound
/// write lock for every business frame the encoder emits.
/// Implementations must keep the call cheap (single fwrite + optional
/// fsync window) to avoid back-pressuring the encoder.</para>
///
/// <para>Compaction is needed because the underlying ring evicts on
/// overflow but a naive append-only file would grow without bound.
/// <see cref="Compact"/> is invoked periodically (e.g. when on-disk
/// record count exceeds <c>2 × Capacity</c>) with the current live
/// ring contents; the implementation atomically replaces the file.
/// </para>
///
/// <para><see cref="Remove"/> is called by
/// <c>FixpSession.CloseLocked</c> on <em>terminal</em> close
/// (Terminate frame, peer Disconnect, watchdog timeout) — never on
/// Suspend, because Suspend is exactly the state we want to recover
/// across host restarts.</para>
///
/// <para><see cref="LoadAll"/> is called once at host boot, before
/// the FIXP listener accepts connections, and returns every
/// recoverable per-session ring keyed by
/// <c>SessionId</c>. Corrupt records (Crc32C mismatch, torn tail)
/// are dropped per-record with a warning logged by the
/// implementation; an entirely unreadable file is logged at
/// <c>Critical</c> and the session is treated as if it had no
/// persisted state (the peer's Establish will then fall through to
/// the spec §4.5.6 not-applied-range path).</para>
/// </summary>
public interface IFixpRetransmitPersister : IDisposable
{
    /// <summary>
    /// Persists a single appended frame for <paramref name="sessionId"/>.
    /// Called on the FixpSession outbound write lock, so this method
    /// MUST NOT block on any cross-session resource. The frame buffer
    /// may be retained by the implementation; callers must not mutate
    /// it after the call.
    /// </summary>
    void Append(uint sessionId, uint seq, ReadOnlySpan<byte> frame);

    /// <summary>
    /// Atomically replaces the per-session file with the supplied
    /// <paramref name="liveFrames"/> (the current in-memory ring
    /// contents, lowest-seq first). Implementations should write to a
    /// temporary file and rename so a crash mid-compaction cannot
    /// leave a partially-rewritten file.
    /// </summary>
    void Compact(uint sessionId, IReadOnlyList<RetransmitRingEntry> liveFrames);

    /// <summary>
    /// Removes the per-session file. Idempotent — safe to call when no
    /// file exists.
    /// </summary>
    void Remove(uint sessionId);

    /// <summary>
    /// Loads every recoverable per-session ring from the data
    /// directory. Returns an empty dictionary if the directory does
    /// not exist or contains no session files. Called once at boot.
    /// </summary>
    IReadOnlyDictionary<uint, IReadOnlyList<RetransmitRingEntry>> LoadAll();
}

/// <summary>
/// One entry in a persisted retransmit ring. Carries the FIXP
/// outbound <c>MsgSeqNum</c> and the full wire frame
/// (SOFH+SBE+body) exactly as it was written to the transport.
/// </summary>
public readonly record struct RetransmitRingEntry(uint Seq, byte[] Frame);
