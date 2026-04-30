namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Strategy-side order action. The runner translates intents into
/// EntryPoint wire frames.
/// </summary>
public enum OrderIntentKind { New, Cancel }

public enum OrderSide : byte { Buy, Sell }

public enum OrderTypeIntent : byte { Limit, Market }

public enum OrderTifIntent : byte { Day, IOC, FOK }

/// <summary>
/// One unit of work emitted by an <see cref="IStrategy"/>. Strategies are
/// pure functions of <see cref="MarketState"/> and a seeded <see cref="Random"/>;
/// they do not perform I/O. The runner submits the intents and feeds back
/// fills/acks via <see cref="IStrategy"/> callbacks.
///
/// For <see cref="OrderIntentKind.New"/>, <see cref="ClientTag"/> is a
/// strategy-private label the runner echoes back on acks/fills so the
/// strategy can correlate orders without owning the global ClOrdID counter.
///
/// For <see cref="OrderIntentKind.Cancel"/>, <see cref="CancelTag"/> targets
/// a previously-acked order by its strategy-private tag.
/// </summary>
public readonly record struct OrderIntent(
    OrderIntentKind Kind,
    long SecurityId,
    OrderSide Side,
    OrderTypeIntent Type,
    OrderTifIntent Tif,
    long Quantity,
    long PriceMantissa,
    string ClientTag,
    string CancelTag)
{
    public static OrderIntent NewLimit(long securityId, OrderSide side, long qty, long priceMantissa, string clientTag)
        => new(OrderIntentKind.New, securityId, side, OrderTypeIntent.Limit, OrderTifIntent.Day, qty, priceMantissa, clientTag, "");

    public static OrderIntent NewMarketableLimit(long securityId, OrderSide side, long qty, long priceMantissa, string clientTag)
        => new(OrderIntentKind.New, securityId, side, OrderTypeIntent.Limit, OrderTifIntent.IOC, qty, priceMantissa, clientTag, "");

    public static OrderIntent Cancel(long securityId, string cancelTag)
        => new(OrderIntentKind.Cancel, securityId, OrderSide.Buy, OrderTypeIntent.Limit, OrderTifIntent.Day, 0, 0, "", cancelTag);
}
