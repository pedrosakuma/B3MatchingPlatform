using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using RejectEvent = B3.Exchange.Matching.RejectEvent;
using Side = B3.Exchange.Matching.Side;

namespace B3.Exchange.Core;

/// <summary>
/// Outbound surface that <see cref="ChannelDispatcher"/> calls to deliver
/// per-order ExecutionReports back to the originating session.
///
/// Core never holds a reference to a transport, socket, or
/// <c>FixpSession</c> instance: every method takes a
/// <see cref="SessionId"/> value and the implementation (lives in the
/// Gateway) is responsible for resolving it to the live session — or for
/// dropping the event when the session is no longer connected (this PR's
/// behaviour) or buffering it into a Suspended-session retx ring (a Phase 3
/// concern).
///
/// Implementations MUST be safe to call from the Core dispatch thread and
/// MUST NOT block on I/O — outbound encoding plus enqueueing onto the
/// per-session send loop is expected to be O(1).
///
/// <para>This interface intentionally still uses <c>B3.Exchange.Matching</c>
/// engine event types as its payload: translating engine events into the
/// neutral <see cref="ExecutionEvent"/> family in <c>B3.Exchange.Contracts</c>
/// is deferred to a follow-up PR, because the Contracts records currently
/// lack the fields (<c>Side</c>, <c>SecurityId</c>, <c>RptSeq</c>,
/// <c>InsertTimestampNanos</c>, …) needed to reconstruct the
/// <c>ExecutionReport</c> wire form. Removing the transport reference from
/// Core (the acceptance criterion for #66) is the higher-priority half of
/// the migration.</para>
/// </summary>
public interface ICoreOutbound
{
    bool WriteExecutionReportNew(SessionId session, ulong clOrdIdValue, in OrderAcceptedEvent e,
        ulong receivedTimeNanos = ulong.MaxValue);

    bool WriteExecutionReportTrade(SessionId session, in TradeEvent e, bool isAggressor,
        long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty);

    bool WriteExecutionReportCancel(SessionId session, in OrderCanceledEvent e,
        ulong clOrdIdValue, ulong origClOrdIdValue,
        ulong receivedTimeNanos = ulong.MaxValue);

    bool WriteExecutionReportModify(SessionId session, long securityId, long orderId,
        ulong clOrdIdValue, ulong origClOrdIdValue,
        Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq,
        ulong receivedTimeNanos = ulong.MaxValue);

    bool WriteExecutionReportReject(SessionId session, in RejectEvent e, ulong clOrdIdValue);
}

/// <summary>
/// Sink for inbound commands decoded by the Gateway. Core holds a reference
/// to one of these per process (typically a <c>HostRouter</c> that fans
/// commands out to a <see cref="ChannelDispatcher"/> per
/// <c>SecurityId</c>).
///
/// <para>The Gateway calls these methods on the receive-loop thread of the
/// originating <c>FixpSession</c>; implementations MUST hand off to a
/// per-channel dispatch queue (the matching engine is single-threaded per
/// channel — invoking it inline from multiple sessions corrupts internal
/// state).</para>
///
/// <para>No transport / session / SBE-EntryPoint type appears in this
/// surface: the Gateway flattens its identity into the value pair
/// <c>(<see cref="SessionId"/>, <c>EnteringFirm</c>)</c> at the point where
/// the inbound message is decoded.</para>
/// </summary>
public interface IInboundCommandSink
{
    void EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue);

    void EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue);

    void EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue);

    /// <summary>
    /// Enqueues a NewOrderCross (template 106). Both legs MUST be processed
    /// atomically on the dispatch thread: a half-applied cross would expose
    /// one leg as live liquidity while the second is dropped or interleaved
    /// with other producers. Implementations queue ONE work item carrying
    /// both legs; <see cref="ChannelDispatcher"/> submits buy then sell in
    /// a single dispatch turn under one packet flush.
    /// </summary>
    void EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm);

    /// <summary>Called when a frame fails decoding. Logging hook only — the
    /// Gateway-side <c>FixpSession</c> is responsible for the actual
    /// SessionReject (Terminate) / BusinessMessageReject + close-of-stream
    /// behaviour.</summary>
    void OnDecodeError(SessionId session, string error);

    /// <summary>
    /// Called once when a session's transport closes. The router MUST drop
    /// any cached references for <paramref name="session"/> so the
    /// <c>FixpSession</c> can be garbage-collected and any orderId →
    /// session map entry releases the session id (the orders themselves
    /// stay on the book — they ARE the book — but passive fills against
    /// them go to "no live session" until / unless a fresh session takes
    /// over).
    /// </summary>
    void OnSessionClosed(SessionId session);
}
