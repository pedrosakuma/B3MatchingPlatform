namespace B3.Exchange.Matching;

public enum Side : byte { Buy, Sell }

public enum TimeInForce : byte { Day, IOC, FOK }

public enum OrderType : byte { Limit, Market }

/// <summary>
/// Reasons a matching command can be rejected without any state change.
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
