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
}
