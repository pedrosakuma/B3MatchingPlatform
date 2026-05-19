using B3.Exchange.Matching;

namespace B3.Exchange.PostTrade;

/// <summary>
/// Self-contained, per-trade audit record. One <see cref="PostTradeRecord"/>
/// is emitted on the dispatch thread for every <see cref="TradeEvent"/> the
/// engine produces, immediately after the matching ER_Trade / UMDF
/// <c>Trade_53</c> wire frames are appended to the outbound packet — so
/// the audit-log ordering is identical to the published trade order
/// (issue #329, ADR 0001 §2).
///
/// Field semantics:
/// - <see cref="TradeId"/>: engine-monotonic per channel (from <see cref="TradeEvent.TradeId"/>).
/// - <see cref="TransactTimeNanos"/>: nanosecond UTC of the match. The on-disk
///   schema in subsequent PRs may quantize to microseconds per the ADR; the
///   in-memory record carries the full nanosecond precision the engine
///   already has so downstream consumers can choose.
/// - <see cref="BuyClOrdId"/> / <see cref="SellClOrdId"/>: 0 when the
///   corresponding side has no resolvable owner (e.g. cross/internal print
///   where one leg pre-dates the registry or has already deregistered).
///   The on-disk schema treats 0 as "unknown" — never as a valid order id.
/// - <see cref="BuyFirm"/> / <see cref="SellFirm"/>: always present
///   (sourced from the engine's <see cref="TradeEvent"/>).
/// </summary>
public readonly record struct PostTradeRecord(
    uint TradeId,
    ulong TransactTimeNanos,
    long SecurityId,
    Side AggressorSide,
    long Quantity,
    long PriceMantissa,
    ulong BuyClOrdId,
    ulong SellClOrdId,
    uint BuyFirm,
    uint SellFirm,
    long BuyOrderId,
    long SellOrderId);

/// <summary>
/// Per-channel post-trade audit log sink. Implementations MUST be safe to
/// invoke on the <c>ChannelDispatcher</c>'s single dispatch thread and
/// MUST NOT block on I/O in <see cref="OnTrade"/> long enough to push the
/// dispatcher off its latency budget — the durable-flush should happen on
/// a background writer (see <see cref="IAuditDurabilityTracker"/>, added
/// in the watermark PR).
///
/// Skeleton contract (#329 PR-1). Subsequent PRs add: append-only file
/// writer with CRC32 (IEEE 802.3) + daily rollover (PR-2), firm sparse index (PR-3),
/// durability watermark gating WAL truncation (PR-4), restart
/// replay-into-audit-only mode (PR-5), and operator config / RUNBOOK
/// (PR-6).
/// </summary>
public interface IPostTradeSink
{
    void OnTrade(in PostTradeRecord record);

    /// <summary>
    /// Tags every <see cref="OnTrade"/> emitted since the previous boundary
    /// (or since construction) as belonging to <paramref name="commandSeq"/>.
    /// Called by <c>ChannelDispatcher.OnAfterCommandFlushed</c> on the
    /// dispatch thread once the engine has finished processing a command and
    /// the corresponding UMDF packet has been published.
    ///
    /// <para>Implementations MUST be non-blocking — this runs on the engine
    /// hot path. The <see cref="Checkpoint"/> method is the slow fsync hook.</para>
    /// </summary>
    void OnCommandBoundary(long commandSeq);

    /// <summary>
    /// Flushes any in-progress index block, calls <c>fsync</c> on the
    /// underlying files, and advances <see cref="DurableThroughCommandSeq"/>
    /// to the most recent boundary observed via <see cref="OnCommandBoundary"/>.
    /// On exception the watermark is NOT advanced — callers (the WAL
    /// truncation gate) must treat that as "audit not durable; defer".
    ///
    /// <para>Implementations MUST be safe to call from a thread other than
    /// the dispatch thread (the async snapshot writer invokes it from its
    /// dedicated writer thread). The expected concurrency cost is a brief
    /// lock acquisition; the fsync itself runs under the lock so the
    /// dispatch hot path stalls only for the fsync duration when both
    /// happen to overlap.</para>
    /// </summary>
    void Checkpoint();

    /// <summary>
    /// Highest <c>commandSeq</c> for which every <see cref="OnTrade"/>
    /// record produced by that command is guaranteed fsync'd to disk.
    /// Read by the WAL truncation gate (<c>ChannelDispatcher.Wal.cs</c>):
    /// truncation may only drop WAL records with seq &lt;= this value.
    ///
    /// <para>For the no-op sink this is <see cref="long.MaxValue"/> so a
    /// dispatcher with audit disabled (the default) is never gated — the
    /// pre-#329 truncate-everything behaviour is preserved exactly.</para>
    /// </summary>
    long DurableThroughCommandSeq { get; }

    /// <summary>
    /// Appends an operator-issued trade-bust audit record to the audit log
    /// for <paramref name="tradeDate"/> (ADR 0008 §1, §3). Unlike
    /// <see cref="OnTrade"/>, busts are not tied to the current dispatch
    /// day — they reference a historical fill — so implementations MUST
    /// be capable of writing to a day other than the currently-open log
    /// (cross-day write). Called on the <c>ChannelDispatcher</c> dispatch
    /// thread after the operator-bust validator has accepted the request.
    /// </summary>
    void OnBust(in BustRecord record, DateOnly tradeDate);

    /// <summary>
    /// Appends a reject-attempt audit record to TODAY's audit log
    /// (ADR 0008 §2.5). Records every accepted-but-rejected operator
    /// bust attempt so the audit trail explains 4xx HTTP responses.
    /// Called on the dispatch thread.
    /// </summary>
    void OnRejectAttempt(in RejectAttemptRecord record);
}

/// <summary>
/// No-op sink installed by default when <see cref="ChannelDispatcher"/> is
/// constructed without an explicit <see cref="IPostTradeSink"/>. Keeps the
/// dispatcher's OnTrade hot-path branch-free of null-checks and lets the
/// existing 1300+ tests run unchanged while the feature is in flight.
/// </summary>
public sealed class NullPostTradeSink : IPostTradeSink
{
    public static readonly NullPostTradeSink Instance = new();
    private NullPostTradeSink() { }
    public void OnTrade(in PostTradeRecord record) { }
    public void OnCommandBoundary(long commandSeq) { }
    public void Checkpoint() { }
    public void OnBust(in BustRecord record, DateOnly tradeDate) { }
    public void OnRejectAttempt(in RejectAttemptRecord record) { }
    public long DurableThroughCommandSeq => long.MaxValue;
}
