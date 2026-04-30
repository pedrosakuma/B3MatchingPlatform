using B3.Exchange.Matching;

namespace B3.Exchange.EntryPoint;

/// <summary>
/// Per-connection writer of outbound ExecutionReports. The matching engine's
/// integration layer is expected to call these from the engine dispatch thread;
/// implementations (e.g. <see cref="EntryPointSession"/>) MUST be thread-safe
/// for the write side and serialise sends through a dedicated send loop.
///
/// All <c>Write*</c> methods return <c>true</c> if the report was queued for
/// sending or <c>false</c> if the channel is closed (peer disconnected) or
/// backpressure forced a drop. A returned <c>false</c> is informational; the
/// caller should not retry on the same channel.
/// </summary>
public interface IEntryPointResponseChannel
{
    /// <summary>Stable per-connection identifier (for diagnostics + ownership maps).</summary>
    long ConnectionId { get; }

    /// <summary>Configured EnteringFirm for this connection (assigned at accept time).</summary>
    uint EnteringFirm { get; }

    /// <summary>True until the underlying transport is closed.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// New order accepted onto the book.
    /// </summary>
    bool WriteExecutionReportNew(in OrderAcceptedEvent e);

    /// <summary>One trade leg (1 aggressor, 1 maker). The integration layer
    /// determines whether this connection is the aggressor or the maker.</summary>
    bool WriteExecutionReportTrade(in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty);

    /// <summary>Order canceled (client cancel, IOC remainder, replace-lost-priority).</summary>
    bool WriteExecutionReportCancel(in OrderCanceledEvent e, ulong clOrdIdValue, ulong origClOrdIdValue);

    /// <summary>Order modified (in-place priority-preserving replace).</summary>
    bool WriteExecutionReportModify(long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue,
        Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq);

    /// <summary>Inbound message rejected synchronously (validation, unknown order, etc.).</summary>
    bool WriteExecutionReportReject(in RejectEvent e, ulong clOrdIdValue);

    /// <summary>
    /// Send a session-level reject (B3 <c>Terminate</c> with the given
    /// <paramref name="terminationCode"/>) and gracefully close the
    /// connection after the frame is flushed to the wire. Used when the
    /// inbound stream is no longer recoverable (bad header, unknown
    /// templateId, schema-version mismatch, body length mismatch, ...).
    /// Returns <c>false</c> if the channel was already closed.
    /// </summary>
    bool WriteSessionReject(byte terminationCode);

    /// <summary>
    /// Send a business-level reject (B3 <c>BusinessMessageReject</c>) for a
    /// well-framed inbound message that failed business validation. The
    /// session is kept open; the client may retry with a corrected
    /// message. <paramref name="refSeqNum"/> echoes the offending message's
    /// inbound MsgSeqNum so the client can correlate.
    /// </summary>
    bool WriteBusinessMessageReject(byte refMsgType, uint refSeqNum, ulong businessRejectRefId,
        uint businessRejectReason, string? text = null);
}

/// <summary>
/// Sink for inbound commands decoded from a TCP connection. The
/// <see cref="EntryPointSession"/> calls these on the receive loop thread.
///
/// IMPORTANT: implementations MUST hand off to a per-channel dispatch queue
/// (the matching engine is single-threaded per channel — invoking it inline
/// from multiple sessions corrupts internal state). The integration layer
/// (next milestone) owns this hand-off.
/// </summary>
public interface IEntryPointEngineSink
{
    void EnqueueNewOrder(in NewOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue);
    void EnqueueCancel(in CancelOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue, ulong origClOrdIdValue);
    void EnqueueReplace(in ReplaceOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue, ulong origClOrdIdValue);

    /// <summary>Called when a frame fails decoding (unsupported template,
    /// invalid block length, malformed body). The integration layer may close
    /// the connection or emit a generic reject.</summary>
    void OnDecodeError(IEntryPointResponseChannel reply, string error);
}
