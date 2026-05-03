namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Mean-reverting marketable taker. Maintains an exponentially-weighted
/// moving average (EWMA) of the observed midprice and, when the live mid
/// deviates from the EWMA by at least <see cref="EntryThresholdTicks"/>
/// ticks, sends a marketable IOC limit in the *contrarian* direction
/// (sell when above the EWMA, buy when below) sized at
/// <see cref="LotsPerOrder"/> × the instrument's lot size.
///
/// <para>The EWMA uses <see cref="Alpha"/> as the smoothing factor:
/// <c>ema = alpha*mid + (1 - alpha)*ema</c>. The first observed tick
/// seeds the EWMA verbatim — until then, no orders are produced.</para>
///
/// <para>State (<c>_ema</c>, <c>_seeded</c>) is mutated only inside
/// <see cref="Tick"/>, which the runner contract guarantees is invoked
/// serially on the runner's tick task. The strategy holds no RNG of its
/// own; all randomness flows through the supplied seeded
/// <see cref="Random"/>.</para>
/// </summary>
public sealed class MeanRevertingStrategy : IStrategy
{
    private double _ema;
    private bool _seeded;
    private long _seq;

    public string Name { get; }
    public double Alpha { get; }
    public int EntryThresholdTicks { get; }
    public int CrossTicks { get; }
    public long LotsPerOrder { get; }

    public MeanRevertingStrategy(string name, double alpha, int entryThresholdTicks, int crossTicks, long lotsPerOrder)
    {
        if (alpha <= 0 || alpha > 1) throw new ArgumentOutOfRangeException(nameof(alpha));
        if (entryThresholdTicks <= 0) throw new ArgumentOutOfRangeException(nameof(entryThresholdTicks));
        if (crossTicks <= 0) throw new ArgumentOutOfRangeException(nameof(crossTicks));
        if (lotsPerOrder <= 0) throw new ArgumentOutOfRangeException(nameof(lotsPerOrder));
        Name = name;
        Alpha = alpha;
        EntryThresholdTicks = entryThresholdTicks;
        CrossTicks = crossTicks;
        LotsPerOrder = lotsPerOrder;
    }

    public IEnumerable<OrderIntent> Tick(in MarketState state, Random rng)
    {
        long mid = state.MidMantissa;
        long tick = state.TickSize <= 0 ? 1 : state.TickSize;
        long lot = state.LotSize <= 0 ? 1 : state.LotSize;

        if (!_seeded)
        {
            _ema = mid;
            _seeded = true;
            return Array.Empty<OrderIntent>();
        }

        double prevEma = _ema;
        _ema = Alpha * mid + (1 - Alpha) * prevEma;

        long thresholdMantissa = (long)EntryThresholdTicks * tick;
        long deviation = mid - (long)prevEma;
        if (deviation >= thresholdMantissa)
        {
            // Mid is rich vs EWMA → fade the rally with a SELL.
            long px = mid - (long)CrossTicks * tick;
            if (px <= 0) return Array.Empty<OrderIntent>();
            string tag = $"{Name}-{state.SecurityId}-S-{++_seq}";
            return new[] { OrderIntent.NewMarketableLimit(state.SecurityId, OrderSide.Sell, LotsPerOrder * lot, px, tag) };
        }
        if (deviation <= -thresholdMantissa)
        {
            // Mid is cheap vs EWMA → fade the dip with a BUY.
            long px = mid + (long)CrossTicks * tick;
            string tag = $"{Name}-{state.SecurityId}-B-{++_seq}";
            return new[] { OrderIntent.NewMarketableLimit(state.SecurityId, OrderSide.Buy, LotsPerOrder * lot, px, tag) };
        }
        return Array.Empty<OrderIntent>();
    }
}
