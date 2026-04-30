namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Posts <see cref="LevelsPerSide"/> resting limits on each side, evenly
/// spaced by <see cref="QuoteSpacingTicks"/> ticks around the current
/// midprice. On each tick we top up missing levels (cancelled, filled, or
/// drifted out of band) so the book always shows fresh quotes.
///
/// Refresh policy:
///  - if there is no live order at the target price/side, post one;
///  - if a live order's price has drifted more than <see cref="ReplaceDistanceTicks"/>
///    from the target, cancel it (it'll be reposted next tick).
/// </summary>
public sealed class MarketMakerStrategy : IStrategy
{
    private readonly long _baseQty;
    private long _seq;

    public string Name { get; }
    public int LevelsPerSide { get; }
    public int QuoteSpacingTicks { get; }
    public int ReplaceDistanceTicks { get; }

    public MarketMakerStrategy(string name, int levelsPerSide, int quoteSpacingTicks, int replaceDistanceTicks, long quantity)
    {
        if (levelsPerSide <= 0) throw new ArgumentOutOfRangeException(nameof(levelsPerSide));
        if (quoteSpacingTicks <= 0) throw new ArgumentOutOfRangeException(nameof(quoteSpacingTicks));
        if (replaceDistanceTicks <= 0) throw new ArgumentOutOfRangeException(nameof(replaceDistanceTicks));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        Name = name;
        LevelsPerSide = levelsPerSide;
        QuoteSpacingTicks = quoteSpacingTicks;
        ReplaceDistanceTicks = replaceDistanceTicks;
        _baseQty = quantity;
    }

    public IEnumerable<OrderIntent> Tick(in MarketState state, Random rng)
    {
        // RNG unused on purpose: market-maker is deterministic given mid.
        // Touching it here keeps the strategy compatible with seeded test
        // scenarios that share an rng across strategies.
        _ = rng;

        var intents = new List<OrderIntent>(capacity: LevelsPerSide * 2);
        long mid = state.MidMantissa;
        long tick = state.TickSize <= 0 ? 1 : state.TickSize;
        long lot = state.LotSize <= 0 ? 1 : state.LotSize;
        long qty = (_baseQty / lot) * lot;
        if (qty <= 0) qty = lot;

        for (int i = 1; i <= LevelsPerSide; i++)
        {
            long bidPx = mid - (long)i * QuoteSpacingTicks * tick;
            long askPx = mid + (long)i * QuoteSpacingTicks * tick;
            if (bidPx <= 0) continue;
            EmitForLevel(intents, state, OrderSide.Buy, bidPx, qty, tick);
            EmitForLevel(intents, state, OrderSide.Sell, askPx, qty, tick);
        }
        return intents;
    }

    private void EmitForLevel(List<OrderIntent> intents, in MarketState state, OrderSide side, long targetPx, long qty, long tick)
    {
        long maxDrift = (long)ReplaceDistanceTicks * tick;
        bool haveAtTarget = false;
        foreach (var live in state.MyLiveOrders)
        {
            if (live.Side != side) continue;
            long delta = live.PriceMantissa - targetPx;
            if (delta < 0) delta = -delta;
            if (delta == 0) { haveAtTarget = true; continue; }
            if (delta > maxDrift)
            {
                intents.Add(OrderIntent.Cancel(state.SecurityId, live.ClientTag));
            }
        }
        if (!haveAtTarget)
        {
            string tag = $"{Name}-{state.SecurityId}-{(side == OrderSide.Buy ? "B" : "S")}-{++_seq}";
            intents.Add(OrderIntent.NewLimit(state.SecurityId, side, qty, targetPx, tag));
        }
    }
}
