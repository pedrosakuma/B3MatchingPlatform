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
/// Fired when the trading phase for an instrument transitions. Carries
/// the new phase plus a per-event RptSeq so the integration layer can
/// emit a sequenced UMDF <c>SecurityStatus_3</c> frame
/// (gap-functional §5 / #201).
/// </summary>
public readonly record struct TradingPhaseChangedEvent(
    long SecurityId,
    TradingPhase Phase,
    ulong TransactTimeNanos,
    uint RptSeq);

/// <summary>
/// Fired when a book side that had at least one resting order becomes
/// empty as a result of an order cancel/fill in the same dispatch turn.
/// The integration layer translates this to the UMDF
/// <c>EmptyBook_9</c> frame (gap-functional §22 / #200). Carries no
/// <c>RptSeq</c> because the spec uses <c>EmptyBook_9</c> as a
/// boundary marker, not a per-instrument sequenced event.
/// </summary>
public readonly record struct OrderBookSideEmptyEvent(
    long SecurityId,
    Side Side,
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
/// Fired when an iceberg order's visible slice is fully consumed by
/// trades and a fresh visible slice is taken from the hidden reserve
/// (issue #211). Carries the same <see cref="OrderId"/> as the
/// original order — the order is logically still the same. The
/// integration layer translates this to a UMDF
/// <c>DeleteOrder_MBO_51</c> for the consumed spot followed by an
/// <c>OrderAdded_MBO_50</c> for the replenished slice at the back of
/// the same price level (time-priority loss). No
/// <c>ExecutionReport</c> is emitted: the per-trade ER_Trade frames
/// already covered the fills, and the order has not changed cumulative
/// quantity or status from the client's perspective.
/// <see cref="NewVisibleQuantity"/> is the size of the new exposed
/// slice; <see cref="RemainingHiddenQuantity"/> is what is left in the
/// reserve after this replenish (may be zero on the last replenish).
/// </summary>
public readonly record struct IcebergReplenishedEvent(
    long SecurityId,
    long OrderId,
    Side Side,
    long PriceMantissa,
    long NewVisibleQuantity,
    long RemainingHiddenQuantity,
    ulong InsertTimestampNanos,
    ulong TransactTimeNanos,
    uint DeleteRptSeq,
    uint AddRptSeq);

/// <summary>
/// Fired during an auction phase whenever the indicative state of the
/// auction changes (TOP price, TOP quantity, or imbalance side/size).
/// Issue #229 (Onda M · M2). The integration layer translates this into
/// a paired UMDF emission: <c>TheoreticalOpeningPrice_16</c> +
/// <c>AuctionImbalance_19</c>. The engine throttles emission so an
/// accumulation event that does not change the indicative state does
/// not produce duplicate frames.
/// <para>
/// When <see cref="HasTop"/> is false there is no crossing in the book
/// (one side is empty, or the best bid is below the best ask). The
/// integration layer encodes the TOP frame with <c>mDUpdateAction=DELETE</c>
/// and NULL price/qty so any prior TOP value at the consumer is
/// invalidated.
/// </para>
/// <para>
/// <see cref="ImbalanceSide"/> is <see cref="Side.Buy"/> for "more
/// buyers" and <see cref="Side.Sell"/> for "more sellers"; it is
/// undefined when <see cref="HasImbalance"/> is false (the auction is
/// perfectly balanced and would clear with no leftover at the TOP
/// price).
/// </para>
/// </summary>
public readonly record struct AuctionTopChangedEvent(
    long SecurityId,
    bool HasTop,
    long TopPriceMantissa,
    long TopQuantity,
    bool HasImbalance,
    Side ImbalanceSide,
    long ImbalanceQuantity,
    ulong TransactTimeNanos,
    uint RptSeq);

/// <summary>
/// Fired when a stop order (StopLoss or StopLimit) is accepted by the
/// engine and parked off-book in the per-instrument trigger book.
/// Issue #214. Carries the original order parameters so the integration
/// layer can emit ER_New back to the originating session. The order is
/// off-book so NO MBO frame is emitted; only ER_New surfaces to the
/// client. The order remains parked until either:
/// <list type="bullet">
///   <item>a trade prints at or through <see cref="StopPxMantissa"/>
///   on the watching side, in which case it triggers and is re-routed
///   through the matching path (see <see cref="StopOrderTriggeredEvent"/>);</item>
///   <item>or it is cancelled by the client (see <see cref="StopOrderCanceledEvent"/>).</item>
/// </list>
/// </summary>
public readonly record struct StopOrderAcceptedEvent(
    long SecurityId,
    long OrderId,
    string ClOrdId,
    Side Side,
    OrderType StopType,
    TimeInForce Tif,
    long StopPxMantissa,
    long LimitPriceMantissa,
    long Quantity,
    uint EnteringFirm,
    ulong InsertTimestampNanos,
    uint RptSeq);

/// <summary>
/// Fired when a parked stop order's trigger condition fires. Issue #214.
/// Informational: the engine internally re-routes the triggered order
/// through the normal aggression path (which then emits Trade /
/// OrderAccepted / OrderFilled etc. with the SAME <see cref="OrderId"/>).
/// The integration layer's default is to log this and emit no separate
/// frame; per-trade and per-resting-event frames already cover the
/// client-visible state changes.
/// </summary>
public readonly record struct StopOrderTriggeredEvent(
    long SecurityId,
    long OrderId,
    Side Side,
    long StopPxMantissa,
    long TriggerTradePriceMantissa,
    ulong TransactTimeNanos,
    uint RptSeq);

/// <summary>
/// Fired when a parked stop order is cancelled by the client (the only
/// reason for a stop cancel — stops do not auto-expire in this engine).
/// Issue #214. The integration layer translates this to ER_Cancel back
/// to the owning session; no MBO frame is emitted because the stop was
/// never on the book.
/// </summary>
public readonly record struct StopOrderCanceledEvent(
    long SecurityId,
    long OrderId,
    Side Side,
    long StopPxMantissa,
    long RemainingQuantityAtCancel,
    ulong TransactTimeNanos,
    uint RptSeq);

/// <summary>
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

    /// <summary>
    /// Optional event emitted when a book side empties out after a cancel
    /// or fill. Default is a no-op so legacy sinks compile; the production
    /// <c>ChannelDispatcher</c> overrides it to emit <c>EmptyBook_9</c>.
    /// </summary>
    void OnOrderBookSideEmpty(in OrderBookSideEmptyEvent e) { }

    /// <summary>
    /// Optional event emitted when an instrument's trading phase changes.
    /// Default no-op so legacy sinks compile; the production
    /// <c>ChannelDispatcher</c> overrides it to emit
    /// <c>SecurityStatus_3</c>.
    /// </summary>
    void OnTradingPhaseChanged(in TradingPhaseChangedEvent e) { }

    /// <summary>
    /// Optional event emitted when an iceberg order replenishes its
    /// visible slice from the hidden reserve. Default no-op so legacy
    /// sinks compile; the production <c>ChannelDispatcher</c> overrides
    /// it to emit a UMDF <c>DeleteOrder_MBO_51</c> + <c>OrderAdded_MBO_50</c>
    /// pair for the same OrderID. Issue #211.
    /// </summary>
    void OnIcebergReplenished(in IcebergReplenishedEvent e) { }

    /// <summary>
    /// Optional event emitted when a stop order is accepted off-book and
    /// parked in the trigger book (issue #214). Default no-op so legacy
    /// sinks compile; the production <c>ChannelDispatcher</c> overrides
    /// it to emit ER_New only (NO MBO — the stop is off-book).
    /// </summary>
    void OnStopOrderAccepted(in StopOrderAcceptedEvent e) { }

    /// <summary>
    /// Optional informational event emitted when a parked stop order's
    /// trigger condition fires (issue #214). The engine internally
    /// re-routes the triggered order via the normal matching path; the
    /// integration layer typically logs this and emits no separate
    /// frame, since per-trade and per-resting-event frames cover the
    /// client-visible state changes.
    /// </summary>
    void OnStopOrderTriggered(in StopOrderTriggeredEvent e) { }

    /// <summary>
    /// Optional event emitted when a parked stop order is cancelled by
    /// the client (issue #214). Default no-op; production sink emits
    /// ER_Cancel back to the owning session and evicts the canonical
    /// order-state entry. NO MBO frame is emitted because the stop was
    /// never on the book.
    /// </summary>
    void OnStopOrderCanceled(in StopOrderCanceledEvent e) { }

    /// <summary>
    /// Optional event emitted during an auction phase when the
    /// indicative TOP / imbalance state changes (issue #229). Default
    /// no-op; production sink fans out to UMDF
    /// <c>TheoreticalOpeningPrice_16</c> + <c>AuctionImbalance_19</c>.
    /// </summary>
    void OnAuctionTopChanged(in AuctionTopChangedEvent e) { }
}
