namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Momentum marketable taker. Compares the live mid against the mid
/// observed on the previous <see cref="Tick"/> call; when the per-tick
/// step is at least <see cref="TriggerTicks"/> ticks, sends a marketable
/// IOC limit *in the direction of the move* (buy on up-tick, sell on
/// down-tick) sized at <see cref="LotsPerOrder"/> × the instrument's lot
/// size.
///
/// <para>The first observed tick seeds the previous-mid reference; no
/// orders are produced until the second tick. State is mutated only
/// inside <see cref="Tick"/>, which the runner invokes serially.</para>
///
/// <para>Pairs naturally with <see cref="MeanRevertingStrategy"/> on the
/// same instrument: momentum amplifies short bursts, mean-revert fades
/// extended drifts. The mid random walk in the runner
/// (<c>midDriftProbability</c>) provides the trigger flow.</para>
/// </summary>
public sealed class MomentumStrategy : IStrategy
{
    private long _prevMid;
    private bool _seeded;
    private long _seq;

    public string Name { get; }
    public int TriggerTicks { get; }
    public int CrossTicks { get; }
    public long LotsPerOrder { get; }

    public MomentumStrategy(string name, int triggerTicks, int crossTicks, long lotsPerOrder)
    {
        if (triggerTicks <= 0) throw new ArgumentOutOfRangeException(nameof(triggerTicks));
        if (crossTicks <= 0) throw new ArgumentOutOfRangeException(nameof(crossTicks));
        if (lotsPerOrder <= 0) throw new ArgumentOutOfRangeException(nameof(lotsPerOrder));
        Name = name;
        TriggerTicks = triggerTicks;
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
            _prevMid = mid;
            _seeded = true;
            return Array.Empty<OrderIntent>();
        }

        long step = mid - _prevMid;
        _prevMid = mid;
        long thresholdMantissa = (long)TriggerTicks * tick;
        if (step >= thresholdMantissa)
        {
            long px = mid + (long)CrossTicks * tick;
            string tag = $"{Name}-{state.SecurityId}-B-{++_seq}";
            return new[] { OrderIntent.NewMarketableLimit(state.SecurityId, OrderSide.Buy, LotsPerOrder * lot, px, tag) };
        }
        if (step <= -thresholdMantissa)
        {
            long px = mid - (long)CrossTicks * tick;
            if (px <= 0) return Array.Empty<OrderIntent>();
            string tag = $"{Name}-{state.SecurityId}-S-{++_seq}";
            return new[] { OrderIntent.NewMarketableLimit(state.SecurityId, OrderSide.Sell, LotsPerOrder * lot, px, tag) };
        }
        return Array.Empty<OrderIntent>();
    }
}
