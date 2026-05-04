namespace B3.Exchange.Matching;

/// <summary>
/// Fired when an order successfully rests on the book OR when it is accepted as
/// a transient aggressor whose first action is a cross. The integration layer
/// translates this to <c>Order_MBO_50</c> NEW iff the order rested. To detect
/// resting, observe whether <see cref="RemainingQuantity"/> is &gt; 0 AT THE TIME
/// of the event (always true here since the engine fires Accepted only for
/// orders that actually entered the book — pure aggressors that never rest do
/// NOT receive an Accepted event; instead they get only <see cref="TradeEvent"/>
/// and optionally a synthetic Reject if no liquidity).
/// </summary>
public readonly record struct OrderAcceptedEvent(
    long SecurityId,
    long OrderId,
    string ClOrdId,
    Side Side,
    long PriceMantissa,
    long RemainingQuantity,
    uint EnteringFirm,
    ulong InsertTimestampNanos,
    uint RptSeq);

/// <summary>
/// Fired when a resting order's remaining quantity is reduced as a passive maker
/// in a trade, but it is not yet fully filled. The integration layer translates
/// to <c>Order_MBO_50</c> UPDATE.
/// </summary>
public readonly record struct OrderQuantityReducedEvent(
    long SecurityId,
    long OrderId,
    Side Side,
    long PriceMantissa,
    long NewRemainingQuantity,
    ulong InsertTimestampNanos,
    ulong TransactTimeNanos,
    uint RptSeq);

/// <summary>
/// Fired when a resting order is removed from the book — either because it was
/// fully filled or because it was canceled/replaced. The integration layer
/// translates this to <c>DeleteOrder_MBO_51</c>.
/// </summary>
public readonly record struct OrderCanceledEvent(
    long SecurityId,
    long OrderId,
    Side Side,
    long PriceMantissa,
    long RemainingQuantityAtCancel,
    ulong TransactTimeNanos,
    CancelReason Reason,
    uint RptSeq);

/// <summary>
/// Fired when a resting order is fully consumed by trades. The integration layer
/// translates this to <c>DeleteOrder_MBO_51</c>. Distinct from
/// <see cref="OrderCanceledEvent"/> because downstream execution-report logic
/// distinguishes "filled" from "canceled".
/// </summary>
public readonly record struct OrderFilledEvent(
    long SecurityId,
    long OrderId,
    Side Side,
    long PriceMantissa,
    long FinalFilledQuantity,
    ulong TransactTimeNanos,
    uint RptSeq);

/// <summary>
/// One trade between exactly one aggressor and one resting maker. An aggressor
/// that consumes N resting orders generates N trade events.
/// </summary>
public readonly record struct TradeEvent(
    long SecurityId,
    uint TradeId,
    long PriceMantissa,
    long Quantity,
    Side AggressorSide,
    long AggressorOrderId,
    string AggressorClOrdId,
    uint AggressorFirm,
    long RestingOrderId,
    uint RestingFirm,
    ulong TransactTimeNanos,
    uint RptSeq);

public readonly record struct RejectEvent(
    string ClOrdId,
    long SecurityId,
    long OrderIdOrZero,
    RejectReason Reason,
    ulong TransactTimeNanos);

/// <summary>
/// Fired ONCE per (SecurityID, Side) pair touched by a single
/// <see cref="MatchingEngine.MassCancel"/> invocation, BEFORE the per-order
/// <see cref="OrderCanceledEvent"/>s for the same group. The integration
/// layer translates this to a <c>MassDeleteOrders_MBO_52</c> UMDF frame so
/// consumers that recognise mass-delete semantics can apply them as an
/// atomic boundary; consumers that only follow per-order
/// <c>DeleteOrder_MBO_51</c>s remain correct because the per-order events
/// are still emitted.
/// </summary>
public readonly record struct OrderMassCanceledEvent(
    long SecurityId,
    Side Side,
    int CancelledCount,
    ulong TransactTimeNanos,
    uint RptSeq);

/// <summary>
/// Sink of matching events. Implementations MUST NOT call back into the engine
/// from any of these methods — the engine is single-threaded per channel and
/// reentrant commands corrupt internal linked lists.
/// </summary>
public interface IMatchingEventSink
{
    void OnOrderAccepted(in OrderAcceptedEvent e);
    void OnOrderQuantityReduced(in OrderQuantityReducedEvent e);
    void OnOrderCanceled(in OrderCanceledEvent e);
    void OnOrderFilled(in OrderFilledEvent e);
    void OnTrade(in TradeEvent e);
    void OnReject(in RejectEvent e);

    /// <summary>
    /// Optional summary event emitted once per (SecurityId, Side) at the
    /// start of a mass-cancel. Default implementation is a no-op so legacy
    /// sinks keep compiling; the production <c>ChannelDispatcher</c>
    /// overrides it to emit the UMDF <c>MassDeleteOrders_MBO_52</c> frame.
    /// </summary>
    void OnOrderMassCanceled(in OrderMassCanceledEvent e) { }
}
