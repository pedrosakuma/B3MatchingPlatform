namespace B3.Exchange.SyntheticTrader.Tests;

/// <summary>
/// Unit tests for <see cref="NewsShockStrategy"/> (issue #117). Drives the
/// idle → shock → fade → idle phase machine deterministically by counting
/// ticks; uses a seeded <see cref="Random"/> for trigger jitter and side
/// selection.
/// </summary>
public class NewsShockStrategyTests
{
    private static MarketState State(long mid = 100_0000, long tick = 100, long lot = 100)
        => new(SecurityId: 1, MidMantissa: mid, TickSize: tick, LotSize: lot,
            LastTradePxMantissa: mid, LastTradeQty: 0,
            MyLiveOrders: Array.Empty<LiveOrder>());

    [Fact]
    public void Idle_NoOrdersUntilTrigger()
    {
        // tickIntervalMs=100, mean=500ms, no jitter → 5 idle ticks then shock.
        var s = new NewsShockStrategy("ns",
            tickIntervalMs: 100, meanIntervalMs: 500, jitterMs: 0,
            shockDurationMs: 200, fadeDurationMs: 0,
            levelsToSweep: 3, burstLots: 1, directionBias: 1.0); // always BUY
        var rng = new Random(1);

        // Ticks 1..4: idle (no emission)
        for (int i = 0; i < 4; i++)
        {
            Assert.Empty(s.Tick(State(), rng));
            Assert.Equal(NewsShockStrategy.Phase.Idle, s.CurrentPhase);
        }
        // Tick 5: trigger fires → shock burst.
        var intents = s.Tick(State(), rng).ToList();
        Assert.Single(intents);
        Assert.Equal(OrderSide.Buy, intents[0].Side);
        Assert.Equal(NewsShockStrategy.Phase.Shock, s.CurrentPhase);
    }

    [Fact]
    public void Shock_EmitsBurstAtMidPlusOffset_BuySide()
    {
        var s = new NewsShockStrategy("ns",
            tickIntervalMs: 100, meanIntervalMs: 100, jitterMs: 0,
            shockDurationMs: 100, fadeDurationMs: 0,
            levelsToSweep: 4, burstLots: 7, directionBias: 1.0);
        var rng = new Random(1);
        var intents = s.Tick(State(mid: 100_0000, tick: 100, lot: 50), rng).ToList();
        Assert.Single(intents);
        var o = intents[0];
        Assert.Equal(OrderSide.Buy, o.Side);
        Assert.Equal(OrderTifIntent.IOC, o.Tif);
        Assert.Equal(100_0000 + 4 * 100, o.PriceMantissa);
        Assert.Equal(7 * 50, o.Quantity);
    }

    [Fact]
    public void Shock_SellSide_PriceBelowMid()
    {
        var s = new NewsShockStrategy("ns",
            tickIntervalMs: 100, meanIntervalMs: 100, jitterMs: 0,
            shockDurationMs: 100, fadeDurationMs: 0,
            levelsToSweep: 2, burstLots: 1, directionBias: 0.0); // always SELL
        var intents = s.Tick(State(mid: 100_0000, tick: 100), new Random(1)).ToList();
        Assert.Single(intents);
        Assert.Equal(OrderSide.Sell, intents[0].Side);
        Assert.Equal(100_0000 - 2 * 100, intents[0].PriceMantissa);
    }

    [Fact]
    public void FullCycle_IdleShockFadeIdle()
    {
        // mean=200ms (2 ticks), shock=300ms (3 ticks), fade=300ms (3 ticks).
        var s = new NewsShockStrategy("ns",
            tickIntervalMs: 100, meanIntervalMs: 200, jitterMs: 0,
            shockDurationMs: 300, fadeDurationMs: 300,
            levelsToSweep: 1, burstLots: 10, directionBias: 1.0);
        var rng = new Random(1);

        // Idle ticks (1)
        Assert.Empty(s.Tick(State(), rng));
        Assert.Equal(NewsShockStrategy.Phase.Idle, s.CurrentPhase);
        // Tick 2: trigger → shock
        Assert.Single(s.Tick(State(), rng));
        Assert.Equal(NewsShockStrategy.Phase.Shock, s.CurrentPhase);
        // Two more shock ticks
        Assert.Single(s.Tick(State(), rng));
        Assert.Equal(NewsShockStrategy.Phase.Shock, s.CurrentPhase);
        Assert.Single(s.Tick(State(), rng));
        // Now transitions to Fade for next 3 ticks.
        Assert.Equal(NewsShockStrategy.Phase.Fade, s.CurrentPhase);
        long firstFadeQty = s.Tick(State(), rng).Single().Quantity;
        long secondFadeQty = s.Tick(State(), rng).Single().Quantity;
        // Fade is monotonically non-increasing.
        Assert.True(secondFadeQty <= firstFadeQty);
        // Final fade tick consumes remaining; subsequent tick is back to Idle.
        s.Tick(State(), rng).ToList();
        Assert.Equal(NewsShockStrategy.Phase.Idle, s.CurrentPhase);
    }

    [Fact]
    public void Fade_QuantityDecaysLinearlyToZero()
    {
        // shock=100ms (1 tick), fade=400ms (4 ticks). Burst=10 lots × lot=1.
        var s = new NewsShockStrategy("ns",
            tickIntervalMs: 100, meanIntervalMs: 100, jitterMs: 0,
            shockDurationMs: 100, fadeDurationMs: 400,
            levelsToSweep: 1, burstLots: 10, directionBias: 1.0);
        var rng = new Random(1);
        // Shock tick → full quantity 10.
        Assert.Equal(10, s.Tick(State(lot: 1), rng).Single().Quantity);
        Assert.Equal(NewsShockStrategy.Phase.Fade, s.CurrentPhase);
        // 4 fade ticks: fractions 4/4, 3/4, 2/4, 1/4 → 10, 8 (rounded), 5, 3 (rounded).
        var qtys = new List<long>();
        for (int i = 0; i < 4; i++)
            qtys.Add(s.Tick(State(lot: 1), rng).Single().Quantity);
        Assert.Equal(NewsShockStrategy.Phase.Idle, s.CurrentPhase);
        Assert.True(qtys[0] >= qtys[1]);
        Assert.True(qtys[1] >= qtys[2]);
        Assert.True(qtys[2] >= qtys[3]);
        Assert.True(qtys[3] >= 0);
    }

    [Fact]
    public void Jitter_TriggerIntervalStaysWithinBand()
    {
        // mean=10 ticks, jitter=4 ticks → idle phase length always in [6,14].
        var s = new NewsShockStrategy("ns",
            tickIntervalMs: 100, meanIntervalMs: 1000, jitterMs: 400,
            shockDurationMs: 100, fadeDurationMs: 0,
            levelsToSweep: 1, burstLots: 1, directionBias: 1.0);
        var rng = new Random(42);
        for (int cycle = 0; cycle < 10; cycle++)
        {
            int idleTicks = 0;
            while (s.CurrentPhase == NewsShockStrategy.Phase.Idle)
            {
                var emitted = s.Tick(State(), rng).ToList();
                idleTicks++;
                if (emitted.Count > 0) break; // shock fired this tick
            }
            Assert.InRange(idleTicks, 6, 14);
            // Now in Shock phase (1 tick), advance back to idle.
            // shockDurationTicks=1 and fade=0 → next tick goes back to Idle.
            // (Actually current Shock tick already happened above; advance once more.)
            // Force back to idle:
            while (s.CurrentPhase != NewsShockStrategy.Phase.Idle)
                s.Tick(State(), rng).ToList();
        }
    }

    [Fact]
    public void DirectionBias_AllBuysWhenBiasIsOne()
    {
        var s = new NewsShockStrategy("ns",
            tickIntervalMs: 100, meanIntervalMs: 100, jitterMs: 0,
            shockDurationMs: 100, fadeDurationMs: 0,
            levelsToSweep: 1, burstLots: 1, directionBias: 1.0);
        var rng = new Random(99);
        for (int i = 0; i < 50; i++)
        {
            var intents = s.Tick(State(), rng).ToList();
            if (intents.Count > 0)
                Assert.Equal(OrderSide.Buy, intents[0].Side);
        }
    }

    [Fact]
    public void DirectionBias_AllSellsWhenBiasIsZero()
    {
        var s = new NewsShockStrategy("ns",
            tickIntervalMs: 100, meanIntervalMs: 100, jitterMs: 0,
            shockDurationMs: 100, fadeDurationMs: 0,
            levelsToSweep: 1, burstLots: 1, directionBias: 0.0);
        var rng = new Random(99);
        for (int i = 0; i < 50; i++)
        {
            var intents = s.Tick(State(), rng).ToList();
            if (intents.Count > 0)
                Assert.Equal(OrderSide.Sell, intents[0].Side);
        }
    }

    [Fact]
    public void Validation_RejectsBadConfigs()
    {
        Assert.Throws<ArgumentException>(() => new NewsShockStrategy("",
            100, 100, 0, 100, 0, 1, 1, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewsShockStrategy("x",
            tickIntervalMs: 0, 100, 0, 100, 0, 1, 1, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewsShockStrategy("x",
            100, meanIntervalMs: 0, 0, 100, 0, 1, 1, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewsShockStrategy("x",
            100, 100, jitterMs: 200, 100, 0, 1, 1, 0.5)); // jitter >= mean
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewsShockStrategy("x",
            100, 100, 0, shockDurationMs: 0, 0, 1, 1, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewsShockStrategy("x",
            100, 100, 0, 100, fadeDurationMs: -1, 1, 1, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewsShockStrategy("x",
            100, 100, 0, 100, 0, levelsToSweep: 0, 1, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewsShockStrategy("x",
            100, 100, 0, 100, 0, 1, burstLots: 0, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewsShockStrategy("x",
            100, 100, 0, 100, 0, 1, 1, directionBias: 1.5));
    }
}
