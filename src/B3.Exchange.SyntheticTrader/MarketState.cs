namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Snapshot of what a strategy sees for a single instrument at the start of
/// a tick. Prices are mantissas (B3 implicit /10000); quantities are integer
/// share counts. The list of live orders is filtered to those owned by the
/// strategy's own logical book — i.e. the runner tracks acks per strategy.
/// </summary>
public readonly record struct MarketState(
    long SecurityId,
    long MidMantissa,
    long TickSize,
    long LotSize,
    long LastTradePxMantissa,
    long LastTradeQty,
    IReadOnlyList<LiveOrder> MyLiveOrders);

/// <summary>
/// A live working order owned by the strategy. <see cref="ClientTag"/> is
/// the tag the strategy used when emitting the original NEW intent.
/// </summary>
public readonly record struct LiveOrder(
    string ClientTag,
    OrderSide Side,
    long PriceMantissa,
    long RemainingQty);

/// <summary>
/// Fill notification handed back to the strategy.
/// </summary>
public readonly record struct FillEvent(
    string ClientTag,
    OrderSide Side,
    long PriceMantissa,
    long LastQty,
    long LeavesQty);
