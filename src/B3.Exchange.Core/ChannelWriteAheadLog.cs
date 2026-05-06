using B3.Exchange.Contracts;
using B3.Exchange.Matching;

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
public interface IChannelWriteAheadLog
{
    /// <summary>
    /// Appends a record to the WAL. Implementations choose whether to
    /// fsync per-write (zero RPO, lower throughput) or batch (higher
    /// throughput, RPO bounded by batch interval).
    /// </summary>
    void Append(WalRecord record);

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
}
