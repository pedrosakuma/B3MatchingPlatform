using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using System.Collections.Generic;

namespace B3.Exchange.Core;

/// <summary>
/// Write-Ahead Log entry persisted by
/// <see cref="IChannelWriteAheadLog"/> (issue #269) before the
/// dispatcher applies a command to the engine. On crash recovery the
/// dispatcher loads the latest snapshot and replays every record with
/// <see cref="Seq"/> greater than
/// <see cref="ChannelStateSnapshot.LastAppliedSeq"/>, restoring the
/// channel to the most-recently-acknowledged command.
///
/// <para>Only the state-mutating high-frequency command kinds are
/// covered (<see cref="WalRecordKind.NewOrder"/>,
/// <see cref="WalRecordKind.Cancel"/>,
/// <see cref="WalRecordKind.Replace"/>). Operator commands always
/// force-snapshot post-flush so their effect survives a crash via the
/// snapshot itself; <c>Cross</c> and <c>MassCancel</c> are deliberately
/// out of scope for the v1 WAL — operators that need them on the
/// recovery path must keep the synchronous snapshot persister enabled.</para>
/// </summary>
public sealed record WalRecord(
    long Seq,
    WalRecordKind Kind,
    string SessionValue,
    uint Firm,
    ulong ClOrdId,
    ulong OrigClOrdId,
    NewOrderCommand? NewOrder,
    CancelOrderCommand? Cancel,
    ReplaceOrderCommand? Replace);

/// <summary>
/// Discriminator for <see cref="WalRecord"/>. Stable string-encoded
/// names ensure forward compatibility (a future WAL writer can add
/// new kinds without invalidating older records).
/// </summary>
public enum WalRecordKind
{
    NewOrder,
    Cancel,
    Replace,
}

/// <summary>
/// Pluggable per-channel append-only Write-Ahead Log (issue #269).
/// The dispatcher appends one <see cref="WalRecord"/> for each
/// state-mutating command before invoking the engine. After a
/// successful snapshot persist the dispatcher calls
/// <see cref="Truncate"/> so the WAL only contains records that are
/// not yet reflected in the snapshot.
///
/// <para>All methods are invoked on the dispatch loop thread (single
/// writer). Implementations MUST be safe under abrupt termination:
/// <see cref="Append"/> must produce records that are either fully
/// visible to <see cref="ReadAll"/> or absent — partial / torn writes
/// would invalidate replay.</para>
/// </summary>
public interface IChannelWriteAheadLog : IDurabilityBarrier
{
    /// <summary>
    /// Appends a record to the WAL. Implementations choose whether to
    /// fsync per-write (zero RPO, lower throughput) or batch (higher
    /// throughput, RPO bounded by batch interval). Returns the number
    /// of bytes the record consumed on the underlying medium so the
    /// dispatcher can update <c>exch_wal_append_bytes_total</c> without
    /// re-serializing the record itself.
    /// </summary>
    int Append(WalRecord record);

    /// <summary>
    /// Returns every record currently in the log in append order.
    /// Invoked on cold start before the dispatcher begins consuming the
    /// inbound queue. Records that fail to deserialize are skipped and
    /// the implementation logs the corruption — replay continues with
    /// the surviving records.
    /// </summary>
    IReadOnlyList<WalRecord> ReadAll();

    /// <summary>
    /// Discards every record currently in the log. Called by the
    /// dispatcher after a successful snapshot persist so the WAL only
    /// contains records that are not yet reflected in the snapshot.
    /// </summary>
    void Truncate();

    /// <summary>
    /// Issue #348: drops every record with <c>Seq &lt;= throughSeq</c>
    /// and KEEPS every record with <c>Seq &gt; throughSeq</c>. Used by
    /// the async-snapshot path where the dispatch thread may have
    /// appended records past the snapshot's <c>LastAppliedSeq</c>
    /// between <see cref="BackgroundSnapshotWriter"/>'s <c>Submit</c>
    /// and <c>onSaved</c> callback firing — a full <see cref="Truncate"/>
    /// in that window would silently drop the tail.
    ///
    /// <para><b>No default is provided on purpose.</b> Any naive
    /// <c>ReadAll → Truncate → re-Append</c> default would be unsafe
    /// for production implementations whose <see cref="Append"/> runs
    /// on a different thread (the dispatch thread) than this call (the
    /// async-snapshot writer thread): a record appended between
    /// <see cref="ReadAll"/> and <see cref="Truncate"/> would be lost,
    /// recreating the very race this method was introduced to close.
    /// Implementations MUST serialize the rewrite atomically with
    /// concurrent <see cref="Append"/> calls (the file implementation
    /// uses its internal write lock and an atomic file rename).</para>
    /// </summary>
    void TruncateThrough(long throughSeq);

    /// <summary>
    /// Issue #271: removes the underlying WAL artifact entirely (file,
    /// table, etc.) so a subsequent boot has nothing to replay. Used
    /// by the admin "snapshot/reset" endpoint. Default no-op so
    /// in-memory fakes used by tests don't need to implement it.
    /// </summary>
    void Reset() { }

    /// <summary>
    /// Issue #285: number of records the most recent
    /// <see cref="ReadAll"/> dropped because their stored Crc32C did
    /// not match the record bytes. Default 0 for in-memory fakes.
    /// </summary>
    int LastReadCorruptCount => 0;

    /// <summary>
    /// Issue #285: number of records the most recent
    /// <see cref="ReadAll"/> accepted that had no Crc32C suffix
    /// (pre-#285 files). Default 0 for in-memory fakes.
    /// </summary>
    int LastReadLegacyCount => 0;

    /// <summary>
    /// Issue #291: current on-disk size in bytes (or in-memory
    /// equivalent for non-file implementations). Surfaced via the
    /// <c>exch_wal_size_bytes</c> gauge so operators can alert
    /// before the configured cap is hit. Defaults to 0 for
    /// in-memory fakes.
    /// </summary>
    long CurrentSizeBytes => 0;

    /// <summary>
    /// Issue #291: cumulative count of <see cref="Append"/> calls
    /// that the WAL silently dropped because the configured
    /// <c>maxBytes</c> cap was already reached and the resolved
    /// <see cref="WalSizeCapPolicy"/> is
    /// <see cref="WalSizeCapPolicy.Drop"/>. Surfaced via
    /// <c>exch_wal_drops_on_full_total</c>. Defaults to 0.
    /// </summary>
    long DropsOnFullCount => 0;

    /// <summary>
    /// Issue #312 (Tier-2 perf): the <see cref="WalRecord.Seq"/>
    /// of the most-recently <see cref="Append"/>ed record (i.e.
    /// the highest seq the WAL has ACCEPTED — but not necessarily
    /// fsynced). Equal to <see cref="DurableSeqOrZero"/> in
    /// <see cref="WalFsyncMode.PerWrite"/> mode and in in-memory
    /// fakes. <c>0</c> means nothing has been appended.
    /// </summary>
    long PendingDurableSeqOrZero => 0;

    /// <summary>
    /// Issue #312: the highest <see cref="WalRecord.Seq"/> that
    /// has been fsynced to durable storage. In
    /// <see cref="WalFsyncMode.PerWrite"/> mode this advances
    /// inside every <see cref="Append"/>; in
    /// <see cref="WalFsyncMode.GroupCommit"/> mode it advances on
    /// each background fsync. <c>0</c> means nothing is durable
    /// yet. Default implementation returns
    /// <see cref="PendingDurableSeqOrZero"/> so in-memory fakes
    /// trivially satisfy <see cref="WaitForDurable"/>.
    /// </summary>
    long DurableSeqOrZero => PendingDurableSeqOrZero;
}
