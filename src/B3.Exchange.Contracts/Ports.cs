using B3.Exchange.Matching;
using MatchingRejectEvent = B3.Exchange.Matching.RejectEvent;
using MatchingSide = B3.Exchange.Matching.Side;

namespace B3.Exchange.Contracts;

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
/// <para>This interface intentionally takes <c>B3.Exchange.Matching</c>
/// engine event types as its payload. ADR 0011 (issue #379) adopts
/// Matching's events as the canonical Core→Gateway boundary model and
/// retires the never-implemented neutral <c>ExecutionEvent</c> family
/// that previously lived in this assembly.</para>
/// </summary>
public interface ICoreOutbound
{
    bool WriteExecutionReportNew(SessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e,
        ulong receivedTimeNanos = ulong.MaxValue,
        DurabilityHandle durability = default);

    bool WriteExecutionReportTrade(SessionId session, in TradeEvent e, bool isAggressor,
        long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty,
        DurabilityHandle durability = default);

    /// <summary>
    /// Routes an <c>ExecutionReport_Trade</c> for the resting (passive) side
    /// of <paramref name="e"/>. The Core resolves the resting order's owning
    /// session locally on the dispatch thread (via its per-channel
    /// <c>OrderRegistry</c>) and passes the resolved
    /// <paramref name="ownerSession"/> + <paramref name="ownerClOrdId"/>
    /// here; the Gateway forwards the wire-encoded ER. When the resting
    /// order's owner has gone away the report is dropped silently (same
    /// behaviour as the active path).
    /// </summary>
    bool WriteExecutionReportPassiveTrade(SessionId ownerSession, ulong ownerClOrdId, long restingOrderId,
        in TradeEvent e, long leavesQty, long cumQty,
        DurabilityHandle durability = default);

    /// <summary>
    /// Routes an <c>ExecutionReport_Cancel</c> for the owner of
    /// <paramref name="orderId"/> (whether the cancel was self-initiated, a
    /// mass-cancel, or any other engine-driven cancel). The Core has already
    /// resolved <paramref name="ownerSession"/> + <paramref name="ownerClOrdId"/>
    /// on the dispatch thread; the Gateway sends the ER passing
    /// <paramref name="requesterClOrdIdOrZero"/> as the new <c>ClOrdId</c>
    /// on the wire (with the owner's original ClOrdId becoming
    /// <c>OrigClOrdID</c>); pass zero when the cancel was not initiated by
    /// a request from a live session (e.g. engine-internal cancel).
    /// </summary>
    bool WriteExecutionReportPassiveCancel(SessionId ownerSession, ulong ownerClOrdId, long orderId,
        in OrderCanceledEvent e, ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue,
        DurabilityHandle durability = default);

    bool WriteExecutionReportModify(SessionId session, long securityId, long orderId,
        ulong clOrdIdValue, ulong origClOrdIdValue,
        MatchingSide side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq,
        ulong receivedTimeNanos = ulong.MaxValue,
        DurabilityHandle durability = default,
        InvestorId? investorId = null);

    bool WriteExecutionReportReject(SessionId session, in MatchingRejectEvent e, ulong clOrdIdValue,
        DurabilityHandle durability = default);

    /// <summary>
    /// GAP-26 / issue #498: routes a private daily Good-Till restatement
    /// <c>ExecutionReport_Modify</c> (OrdStatus=RESTATED,
    /// ExecRestatementReason=GT_RESTATEMENT) for the owner of the surviving
    /// GTC / unexpired-GTD order to <paramref name="ownerSession"/>. The Core
    /// resolves the owner on the dispatch thread (read-only — the order stays
    /// on the book) and passes <paramref name="ownerClOrdId"/> as the wire
    /// ClOrdID. No UMDF frame, RptSeq advance, or registry mutation
    /// accompanies a restatement.
    ///
    /// <para>Default no-op (returns <c>false</c>) so legacy fakes compile
    /// unchanged; the Gateway overrides it.</para>
    /// </summary>
    bool WriteExecutionReportRestate(SessionId ownerSession, ulong ownerClOrdId,
        in OrderRestatedEvent e, DurabilityHandle durability = default) => false;
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
    /// <summary>
    /// Enqueue a new-order command for dispatch on the per-channel matching
    /// thread. Returns <c>false</c> when the dispatcher's bounded inbound
    /// queue is full (i.e. backpressure). Callers (the Gateway) MUST react
    /// to <c>false</c> by emitting a client-visible reject (e.g.
    /// <c>BusinessMessageReject(SystemBusy)</c>) so the offending session
    /// learns the venue is overloaded instead of silently losing its
    /// command. Returning <c>true</c> means the work item was accepted onto
    /// the queue; it does NOT imply the matching engine has processed it
    /// yet.
    /// </summary>
    bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue);

    /// <summary>
    /// Enqueue a cancel command. Same backpressure semantics as
    /// <see cref="EnqueueNewOrder"/>: <c>false</c> means the dispatcher
    /// queue is full and the gateway MUST reject the command.
    /// </summary>
    bool EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue);

    /// <summary>
    /// Enqueue a replace command. Same backpressure semantics as
    /// <see cref="EnqueueNewOrder"/>.
    /// </summary>
    bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue);

    /// <summary>
    /// Enqueues a NewOrderCross (template 106). Both legs MUST be processed
    /// atomically on the dispatch thread: a half-applied cross would expose
    /// one leg as live liquidity while the second is dropped or interleaved
    /// with other producers. Implementations queue ONE work item carrying
    /// both legs; <see cref="ChannelDispatcher"/> submits buy then sell in
    /// a single dispatch turn under one packet flush.
    ///
    /// <para>Same backpressure semantics as <see cref="EnqueueNewOrder"/>:
    /// returns <c>false</c> when the dispatcher queue is full and the
    /// gateway MUST reject the cross to the originating session.</para>
    /// </summary>
    bool EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm);

    /// <summary>
    /// Enqueues a mass-cancel command (OrderMassActionRequest template 701,
    /// spec §4.8 / #GAP-19). The dispatcher walks its
    /// <c>OrderOwnership</c> map for every resting order owned by the
    /// originating <paramref name="session"/> + <paramref name="enteringFirm"/>
    /// that matches the optional Side / SecurityId filters and cancels
    /// each (one <c>ExecutionReport_Cancel</c> per order back to the
    /// originating session). The caller (gateway) is responsible for
    /// emitting the matching <c>OrderMassActionReport</c> (template 702)
    /// acknowledgement on the same wire.
    ///
    /// <para>A <c>SecurityId == 0</c> on the command means "any
    /// instrument"; the host router fans the command out to every
    /// dispatcher in that case.</para>
    ///
    /// <para>Returns <c>false</c> when ANY targeted channel's queue is
    /// full (partial accept is still possible — what gets enqueued stays
    /// enqueued); the gateway MUST then emit
    /// <c>OrderMassActionReport(REJECTED)</c> with reason "system busy"
    /// instead of the ACCEPTED ack so the peer learns the request was
    /// dropped under load.</para>
    /// </summary>
    bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm);

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
