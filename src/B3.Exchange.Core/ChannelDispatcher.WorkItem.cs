using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// Inbox <c>WorkItem</c> definitions and high-frequency LoggerMessage
/// declarations used by <see cref="ChannelDispatcher"/> (issue #168 split).
/// </summary>
public sealed partial class ChannelDispatcher
{
    internal enum WorkKind : byte { New, Cancel, Replace, Cross, MassCancel, DecodeError, SnapshotRotation, OperatorSnapshotNow, OperatorBumpVersion, OperatorTradeBust }

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
        long EnqueueTicks = 0,
        System.Diagnostics.ActivityContext ParentContext = default);

    /// <summary>
    /// Per-channel mass-cancel payload after gateway-side resolution: a
    /// flat list of engine-assigned <c>OrderID</c>s plus the original
    /// inbound timestamp.
    /// </summary>
    internal sealed record ResolvedMassCancel(IReadOnlyList<long> OrderIds, ulong EnteredAtNanos);

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
}
