namespace B3.Exchange.Matching;

public enum Side : byte { Buy, Sell }

public enum TimeInForce : byte { Day, IOC, FOK }

public enum OrderType : byte { Limit, Market }

/// <summary>
/// Reasons a matching command can be rejected. Some rejects occur before any state change;
/// others (e.g., <see cref="SelfTradePrevention"/>) may occur after fills against other firms.
/// </summary>
public enum RejectReason : byte
{
    UnknownInstrument,
    PriceOutOfBand,
    PriceNotOnTick,
    PriceNonPositive,
    QuantityNotMultipleOfLot,
    QuantityNonPositive,
    UnknownOrderId,
    FokUnfillable,
    MarketNoLiquidity,
    MarketNotImmediateOrCancel,
    InvalidTimeInForceForMarket,
    /// <summary>The aggressor would have crossed against a resting order from
    /// the same <see cref="NewOrderCommand.EnteringFirm"/> and the channel's
    /// <see cref="SelfTradePrevention"/> policy is configured to cancel the
    /// aggressor's residual quantity.</summary>
    SelfTradePrevention,
}

/// <summary>
/// Per-channel self-trade prevention policy. Evaluated by the engine each time
/// an aggressor would cross against a resting order whose
/// <c>EnteringFirm</c> matches the aggressor's. Default <see cref="None"/>
/// preserves the original "trade as today" behaviour.
/// </summary>
public enum SelfTradePrevention : byte
{
    /// <summary>No self-trade prevention; aggressor and maker trade normally.</summary>
    None,
    /// <summary>Cancel the aggressor's remaining (post-other-firm-fills) residual
    /// and stop further matching. Trades already executed against other firms
    /// stand. The conflicting resting order is left untouched.</summary>
    CancelAggressor,
    /// <summary>Cancel the conflicting resting order and continue matching the
    /// aggressor against the next maker (which may be from a different firm).</summary>
    CancelResting,
    /// <summary>Cancel both the conflicting resting order and the aggressor's
    /// residual; stop further matching.</summary>
    CancelBoth,
}

/// <summary>
/// Reason an order leaves the book via <see cref="IMatchingEventSink.OnOrderCanceled"/>.
/// </summary>
public enum CancelReason : byte
{
    /// <summary>Explicit <see cref="CancelOrderCommand"/> from the client.</summary>
    Client,
    /// <summary>IOC remainder after partial fill.</summary>
    IocRemainder,
    /// <summary>Market remainder after liquidity was exhausted.</summary>
    MarketRemainder,
    /// <summary>Replace lost time priority — old resting order is logically deleted
    /// before the new one is inserted (caller emits DEL+NEW MBO frames).</summary>
    ReplaceLostPriority,
    /// <summary>Resting order removed by the channel's <see cref="SelfTradePrevention"/>
    /// policy because an incoming aggressor from the same firm would have
    /// crossed against it.</summary>
    SelfTradePrevention,
    /// <summary>Resting order cancelled by an OrderMassActionRequest
    /// (template 701 → ER_Cancel; spec §4.8 / #GAP-19).</summary>
    MassCancel,
}

/// <summary>
/// New order command (Limit DAY/IOC/FOK or Market IOC/FOK).
/// All timestamps are caller-supplied UNIX-epoch nanoseconds; the engine
/// does not consult any clock so tests are fully deterministic.
/// </summary>
public sealed record NewOrderCommand(
    string ClOrdId,
    long SecurityId,
    Side Side,
    OrderType Type,
    TimeInForce Tif,
    long PriceMantissa,
    long Quantity,
    uint EnteringFirm,
    ulong EnteredAtNanos);

public sealed record CancelOrderCommand(
    string ClOrdId,
    long SecurityId,
    long OrderId,
    ulong EnteredAtNanos);

/// <summary>
/// Replace command. <see cref="NewQuantity"/> is interpreted as the new
/// <em>remaining open quantity</em> (not the original total). Replace cannot
/// change Side, SecurityId, Type or TIF — those would be a new order.
/// Priority is preserved iff (PriceMantissa unchanged AND NewQuantity &lt;= current
/// remaining qty); otherwise the engine emits a <see cref="OrderCanceledEvent"/>
/// with <see cref="CancelReason.ReplaceLostPriority"/> followed by an
/// <see cref="OrderAcceptedEvent"/> for the replacement (which may then cross
/// or rest like a brand-new order, including emitting trades).
/// </summary>
public sealed record ReplaceOrderCommand(
    string ClOrdId,
    long SecurityId,
    long OrderId,
    long NewPriceMantissa,
    long NewQuantity,
    ulong EnteredAtNanos);

/// <summary>
/// Cross order command (NewOrderCross template 106 / spec §4.6.1 / §16.1.5).
/// Submitted as a single inbound frame containing both legs at the same
/// security/qty/price; the engine processes them as a single atomic dispatch
/// turn so the cross cannot be split, dropped half-way, or interleaved with
/// other producers (Self-Trading Prevention is tracked separately as #14;
/// without STP both legs cross naturally on the book).
/// </summary>
public sealed record CrossOrderCommand(
    NewOrderCommand Buy,
    NewOrderCommand Sell,
    ulong BuyClOrdIdValue,
    ulong SellClOrdIdValue,
    ulong CrossId);

/// <summary>
/// Mass-cancel command (OrderMassActionRequest template 701, spec §4.8 /
/// #GAP-19). The engine itself only ever sees a flat list of orderIds —
/// the channel dispatcher resolves the (session, firm, optional Side,
/// optional SecurityId) filter set against its <c>OrderOwnership</c> map
/// before invoking <see cref="MatchingEngine.MassCancel"/>. A
/// <c>SecurityId</c> of zero (the schema's null sentinel) means "any
/// instrument in this dispatcher's books"; the host router fans the
/// inbound command out to every channel when no security id is set.
/// </summary>
public sealed record MassCancelCommand(
    long SecurityId,
    Side? SideFilter,
    ulong EnteredAtNanos);
