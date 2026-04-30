namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Random aggressor: with probability <see cref="OrderProbability"/> per
/// tick, lifts the offer or hits the bid with a marketable IOC limit. Order
/// quantity is a uniform multiple of the lot size in
/// <c>[1, MaxLotMultiple]</c>; the price is the current midprice plus
/// <see cref="CrossTicks"/> ticks of slippage in the aggressor's direction
/// (so the order is reliably marketable against typical market-maker quotes
/// configured with <c>QuoteSpacingTicks &lt;= CrossTicks</c>).
///
/// All randomness flows through the runner-supplied seeded
/// <see cref="Random"/>; the strategy holds no RNG state of its own, so a
/// given (state, rng-state) sequence is fully reproducible.
/// </summary>
public sealed class NoiseTakerStrategy : IStrategy
{
    private long _seq;

    public string Name { get; }
    public double OrderProbability { get; }
    public int MaxLotMultiple { get; }
    public int CrossTicks { get; }

    public NoiseTakerStrategy(string name, double orderProbability, int maxLotMultiple, int crossTicks)
    {
        if (orderProbability < 0 || orderProbability > 1) throw new ArgumentOutOfRangeException(nameof(orderProbability));
        if (maxLotMultiple <= 0) throw new ArgumentOutOfRangeException(nameof(maxLotMultiple));
        if (crossTicks <= 0) throw new ArgumentOutOfRangeException(nameof(crossTicks));
        Name = name;
        OrderProbability = orderProbability;
        MaxLotMultiple = maxLotMultiple;
        CrossTicks = crossTicks;
    }

    public IEnumerable<OrderIntent> Tick(in MarketState state, Random rng)
    {
        if (rng.NextDouble() >= OrderProbability)
            return Array.Empty<OrderIntent>();

        long tick = state.TickSize <= 0 ? 1 : state.TickSize;
        long lot = state.LotSize <= 0 ? 1 : state.LotSize;
        var side = rng.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
        long mult = rng.Next(MaxLotMultiple) + 1;
        long qty = mult * lot;
        long px = side == OrderSide.Buy
            ? state.MidMantissa + (long)CrossTicks * tick
            : state.MidMantissa - (long)CrossTicks * tick;
        if (px <= 0) return Array.Empty<OrderIntent>();
        string tag = $"{Name}-{state.SecurityId}-{(side == OrderSide.Buy ? "B" : "S")}-{++_seq}";
        return new[] { OrderIntent.NewMarketableLimit(state.SecurityId, side, qty, px, tag) };
    }
}
