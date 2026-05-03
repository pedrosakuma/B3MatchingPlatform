namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Periodic large marketable order designed to clear several levels of
/// the resting book in one shot — useful for exercising trade-through
/// fills, partial-fill bookkeeping, and the snapshot/incremental
/// recovery path on the consumer side.
///
/// <para>On each tick, with probability <see cref="TriggerProbability"/>,
/// emits a single marketable IOC limit:</para>
/// <list type="bullet">
///   <item>Side is uniform-random (RNG-driven).</item>
///   <item>Quantity is <see cref="SweepLots"/> × the instrument's lot
///   size. Set this several times larger than a typical
///   market-maker level so the sweep actually walks the book.</item>
///   <item>Price is the live mid offset by <see cref="CrossTicks"/>
///   ticks in the aggressor's direction; for a true "clear N levels"
///   sweep, configure <c>crossTicks ≥ N × marketMaker.quoteSpacingTicks</c>.</item>
/// </list>
///
/// <para>Holds no internal state across ticks beyond a per-instance
/// monotonic <c>_seq</c> for tag uniqueness. RNG is the runner-supplied
/// seeded <see cref="Random"/>; the strategy is fully deterministic for a
/// given (state, rng-state) sequence.</para>
/// </summary>
public sealed class SweeperStrategy : IStrategy
{
    private long _seq;

    public string Name { get; }
    public double TriggerProbability { get; }
    public long SweepLots { get; }
    public int CrossTicks { get; }

    public SweeperStrategy(string name, double triggerProbability, long sweepLots, int crossTicks)
    {
        if (triggerProbability < 0 || triggerProbability > 1) throw new ArgumentOutOfRangeException(nameof(triggerProbability));
        if (sweepLots <= 0) throw new ArgumentOutOfRangeException(nameof(sweepLots));
        if (crossTicks <= 0) throw new ArgumentOutOfRangeException(nameof(crossTicks));
        Name = name;
        TriggerProbability = triggerProbability;
        SweepLots = sweepLots;
        CrossTicks = crossTicks;
    }

    public IEnumerable<OrderIntent> Tick(in MarketState state, Random rng)
    {
        if (rng.NextDouble() >= TriggerProbability)
            return Array.Empty<OrderIntent>();

        long tick = state.TickSize <= 0 ? 1 : state.TickSize;
        long lot = state.LotSize <= 0 ? 1 : state.LotSize;
        var side = rng.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
        long qty = SweepLots * lot;
        long px = side == OrderSide.Buy
            ? state.MidMantissa + (long)CrossTicks * tick
            : state.MidMantissa - (long)CrossTicks * tick;
        if (px <= 0) return Array.Empty<OrderIntent>();
        string tag = $"{Name}-{state.SecurityId}-{(side == OrderSide.Buy ? "B" : "S")}-{++_seq}";
        return new[] { OrderIntent.NewMarketableLimit(state.SecurityId, side, qty, px, tag) };
    }
}
