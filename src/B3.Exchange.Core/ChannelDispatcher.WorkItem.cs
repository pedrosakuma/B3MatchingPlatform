using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using B3.Exchange.PostTrade;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// Inbox <c>WorkItem</c> definitions and high-frequency LoggerMessage
/// declarations used by <see cref="ChannelDispatcher"/> (issue #168 split).
/// </summary>
public sealed partial class ChannelDispatcher
{
    internal enum WorkKind : byte { New, Cancel, Replace, Cross, MassCancel, DecodeError, SnapshotRotation, PriceBandPublish, OperatorSnapshotNow, OperatorBumpVersion, OperatorTradeBust, OperatorSetTradingPhase, OperatorPersistSnapshot, OperatorUncrossAuction, OperatorHaltInstrument, OperatorResumeInstrument, OperatorBustV2, AuditCheckpoint, ShutdownBarrier, OperatorExpireSecurity, OperatorExpireGtd, OperatorRestateGt }

    /// <summary>
    /// Pre-allocated string names for <see cref="WorkKind"/> used as
    /// Activity tag values on the dispatch hot path. Avoids the
    /// per-command <c>Enum.ToString()</c> allocation (round-2 perf #12)
    /// when telemetry listeners are attached.
    /// Indexed by <c>(byte)WorkKind</c>; keep in sync with the enum.
    /// </summary>
    private static readonly string[] WorkKindNames =
    {
        "New",
        "Cancel",
        "Replace",
        "Cross",
        "MassCancel",
        "DecodeError",
        "SnapshotRotation",
        "PriceBandPublish",
        "OperatorSnapshotNow",
        "OperatorBumpVersion",
        "OperatorTradeBust",
        "OperatorSetTradingPhase",
        "OperatorPersistSnapshot",
        "OperatorUncrossAuction",
        "OperatorHaltInstrument",
        "OperatorResumeInstrument",
        "OperatorBustV2",
        "AuditCheckpoint",
        "ShutdownBarrier",
        "OperatorExpireSecurity",
        "OperatorExpireGtd",
        "OperatorRestateGt",
    };

    private static string WorkKindName(WorkKind kind)
    {
        int idx = (int)kind;
        return (uint)idx < (uint)WorkKindNames.Length ? WorkKindNames[idx] : kind.ToString();
    }

    internal sealed record WorkItem(
        WorkKind Kind,
        SessionId Session,
        uint Firm,
        bool HasSession,
        ulong ClOrdId,
        ulong OrigClOrdId,
        NewOrderCommand? NewOrder,
        CancelOrderCommand? Cancel,
        ReplaceOrderCommand? Replace,
        CrossOrderCommand? Cross,
        ResolvedMassCancel? MassCancel = null,
        OperatorTradeBust? TradeBust = null,
        OperatorTradingPhase? TradingPhase = null,
        OperatorUncrossAuction? UncrossAuction = null,
        TaskCompletionSource<PhaseChangeOutcome>? PhaseCompletion = null,
        OperatorHalt? Halt = null,
        OperatorResume? Resume = null,
        TaskCompletionSource<HaltOutcome>? HaltCompletion = null,
        OperatorBustV2? BustV2 = null,
        TaskCompletionSource<OperatorBustV2Outcome>? BustCompletion = null,
        AuditCheckpointRequest? AuditCheckpoint = null,
        TaskCompletionSource<bool>? ShutdownBarrier = null,
        OperatorExpireSecurity? ExpireSecurity = null,
        TaskCompletionSource<ExpireSecurityOutcome>? ExpireCompletion = null,
        OperatorExpireGtd? ExpireGtd = null,
        TaskCompletionSource<ExpireGtdOutcome>? ExpireGtdCompletion = null,
        OperatorRestateGt? RestateGt = null,
        TaskCompletionSource<RestateGtOutcome>? RestateGtCompletion = null,
        long EnqueueTicks = 0,
        System.Diagnostics.ActivityContext ParentContext = default);

    /// <summary>
    /// Per-channel mass-cancel payload after gateway-side resolution: a
    /// flat list of engine-assigned <c>OrderID</c>s plus the original
    /// inbound timestamp.
    /// </summary>
    internal sealed record ResolvedMassCancel(IReadOnlyList<long> OrderIds, MassCancelCommand Command);

    /// <summary>
    /// Operator-triggered trade-bust payload (issue #15): identifies a
    /// previously-published trade by (SecurityId, TradeId) and carries the
    /// echo fields (price/size/date) the consumer audits.
    /// </summary>
    internal sealed record OperatorTradeBust(
        long SecurityId,
        long PriceMantissa,
        long Size,
        uint TradeId,
        ushort TradeDate);

    /// <summary>
    /// Operator-triggered trading-phase change payload (gap-functional §5
    /// / issue #201): identifies the affected instrument and the target
    /// phase. The transition is applied on the dispatch thread so the
    /// engine remains single-threaded.
    /// </summary>
    internal sealed record OperatorTradingPhase(long SecurityId, B3.Exchange.Matching.TradingPhase Phase);

    /// <summary>
    /// Issue #321: operator-triggered auction uncross payload.
    /// Drives <see cref="MatchingEngine.UncrossAuction"/> with a Reserved→Open
    /// (opening call) or FinalClosingCall→Close (closing call) target phase.
    /// </summary>
    internal sealed record OperatorUncrossAuction(long SecurityId, B3.Exchange.Matching.TradingPhase TargetPhase);

    /// <summary>
    /// Issue #322: operator-triggered halt payload — carries the
    /// security to halt, the categorical reason, and an optional
    /// free-form note for downstream observability.
    /// </summary>
    internal sealed record OperatorHalt(long SecurityId, B3.Exchange.Matching.HaltReason Reason, string? Note);

    /// <summary>
    /// Issue #322: operator-triggered resume payload.
    /// </summary>
    internal sealed record OperatorResume(long SecurityId);

    /// <summary>
    /// OPT-03 (ADR 0014): end-of-trading-day expiry payload. Carries the
    /// security id whose option series has reached its terminal trading
    /// day. The dispatcher cancels every resting order on that security
    /// (per-order <c>ER_Cancel</c> + UMDF <c>OrderDelete</c>) and
    /// transitions the trading phase to <c>Close</c>
    /// (UMDF <c>SecurityStatus_3</c> CLOSE) in one dispatch-thread
    /// step so consumers observe the cancellations and the terminal
    /// status under one packet.
    /// </summary>
    internal sealed record OperatorExpireSecurity(long SecurityId);

    /// <summary>
    /// OPT-03 (ADR 0014): outcome of an expire-security command. Reports
    /// how many resting orders were cancelled and whether the trading
    /// phase actually changed (false if it was already <c>Close</c>).
    /// </summary>
    public readonly record struct ExpireSecurityOutcome(int CancelledOrderCount, bool PhaseChanged);

    /// <summary>
    /// GAP-23 / issue #499: end-of-trading-day Good-Till-Date expiry sweep
    /// payload. Carries the trading day being closed as a B3
    /// <c>LocalMktDate</c> (days since Unix epoch). The dispatcher cancels
    /// every resting GTD order across the channel's books whose
    /// <c>ExpireDate</c> is on or before this date (per-order
    /// <c>ER_Cancel</c> + UMDF <c>OrderDelete</c>), in one dispatch-thread
    /// step. Unlike <see cref="OperatorExpireSecurity"/> it never changes a
    /// trading phase.
    /// </summary>
    internal sealed record OperatorExpireGtd(ushort CurrentDate);

    /// <summary>
    /// GAP-23 / issue #499: outcome of a GTD expiry sweep — how many
    /// resting GTD orders were cancelled (0 when none had reached their
    /// ExpireDate).
    /// </summary>
    public readonly record struct ExpireGtdOutcome(int CancelledOrderCount);

    /// <summary>
    /// GAP-26 / issue #498: payload for the daily Good-Till restatement sweep.
    /// On the dispatch thread the engine emits a private restatement ER for
    /// every surviving GTC order and every GTD order whose <c>ExpireDate</c>
    /// is strictly after <see cref="CurrentDate"/> (a B3 <c>LocalMktDate</c>).
    /// The book is not mutated; no trading phase changes.
    /// </summary>
    internal sealed record OperatorRestateGt(ushort CurrentDate);

    /// <summary>
    /// GAP-26 / issue #498: outcome of a Good-Till restatement sweep — how
    /// many surviving GTC / unexpired-GTD orders were restated.
    /// </summary>
    public readonly record struct RestateGtOutcome(int RestatedOrderCount);

    /// <summary>
    /// ADR 0008 PR-2: operator bust request payload (post-trade audit
    /// path). Distinct from <see cref="OperatorTradeBust"/> (which is the
    /// legacy fire-and-forget replay path that does no validation and no
    /// audit-log write): this one runs through the dedup/validator before
    /// optionally emitting a TradeBust_57 frame and writing a bust or
    /// reject-attempt record to the post-trade audit log.
    /// </summary>
    internal sealed record OperatorBustV2(
        uint TradeId,
        DateOnly TradeDate,
        ulong CorrelationId,
        ushort ReasonCode,
        uint BusterFirm,
        long? SecurityIdEcho,
        ulong AttemptTransactTimeNanos);

    /// <summary>ADR 0008 PR-2: surfaced back to the HTTP handler so the
    /// 200/4xx/5xx mapping can happen at the edge. The validator-kind
    /// field carries the validator's verdict 1:1; <see cref="ExistingCorrelationId"/>
    /// is populated only for <see cref="BustValidationKind.AlreadyBustedDifferentCorrelation"/>.</summary>
    public readonly record struct OperatorBustV2Outcome(
        B3.Exchange.PostTrade.BustValidationKind Kind,
        ulong ExistingCorrelationId);

    /// <summary>
    /// Cross-thread handshake payload for issue #396: snapshot writer requests
    /// a dispatch-thread audit checkpoint prepare, then performs the returned
    /// durable flush off the dispatch loop.
    /// </summary>
    internal sealed record AuditCheckpointRequest(
        long SnapshotLastAppliedSeq,
        TaskCompletionSource<IAuditCheckpointOperation> Prepared);

    // ====== high-frequency log messages (LoggerMessage source-gen) ======

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "channel {ChannelNumber} processed {WorkKind} clOrdId={ClOrdId}")]
    private partial void LogCommandProcessed(byte channelNumber, WorkKind workKind, ulong clOrdId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Trace,
        Message = "channel {ChannelNumber} flushed UMDF packet seq={Sequence} bytes={Bytes}")]
    private partial void LogPacketFlushed(byte channelNumber, uint sequence, int bytes);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "channel {ChannelNumber} inbound queue full; dropped {WorkKind} (slow consumer)")]
    private partial void LogQueueFull(byte channelNumber, WorkKind workKind);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
        Message = "channel {ChannelNumber} dispatcher work-item crash workKind={WorkKind} session={Session} firm={Firm} clOrdId={ClOrdId}")]
    private partial void LogDispatcherCrash(Exception ex, byte channelNumber, WorkKind workKind, string session, uint firm, ulong clOrdId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "channel {ChannelNumber} rejecting {WorkKind} — channel WAL-halted (issue #286); restart host after resolving storage fault")]
    private partial void LogWalHalted(byte channelNumber, WorkKind workKind);
}
