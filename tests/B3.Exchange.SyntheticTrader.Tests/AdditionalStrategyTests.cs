namespace B3.Exchange.SyntheticTrader.Tests;

/// <summary>
/// Unit tests for the additional taker strategies added with issue #13:
/// MeanReverting, Momentum, Sweeper. Same fixed-RNG / hand-built
/// MarketState approach as <see cref="StrategyTests"/>.
/// </summary>
public class AdditionalStrategyTests
{
    private static MarketState State(long mid = 100_0000, long tick = 100, long lot = 100)
        => new(SecurityId: 1, MidMantissa: mid, TickSize: tick, LotSize: lot,
            LastTradePxMantissa: mid, LastTradeQty: 0,
            MyLiveOrders: Array.Empty<LiveOrder>());

    // ---- MeanReverting --------------------------------------------------

    [Fact]
    public void MeanReverting_FirstTick_SeedsAndEmitsNothing()
    {
        var mr = new MeanRevertingStrategy("mr", alpha: 0.1, entryThresholdTicks: 3, crossTicks: 2, lotsPerOrder: 1);
        Assert.Empty(mr.Tick(State(), new Random(1)));
    }

    [Fact]
    public void MeanReverting_MidWellAboveEwma_SellsMarketable()
    {
        var mr = new MeanRevertingStrategy("mr", alpha: 0.1, entryThresholdTicks: 3, crossTicks: 2, lotsPerOrder: 2);
        var rng = new Random(1);
        // Seed at 100_0000 (EMA = 100_0000).
        Assert.Empty(mr.Tick(State(mid: 100_0000), rng));
        // Jump up to 100_1000 → deviation = +1000 mantissa = 10 ticks (>3) → SELL.
        var intents = mr.Tick(State(mid: 100_1000), rng).ToList();
        Assert.Single(intents);
        var ord = intents[0];
        Assert.Equal(OrderSide.Sell, ord.Side);
        Assert.Equal(OrderTifIntent.IOC, ord.Tif);
        Assert.Equal(100_1000 - 200, ord.PriceMantissa); // mid - crossTicks*tick
        Assert.Equal(2 * 100, ord.Quantity);             // lotsPerOrder * lotSize
    }

    [Fact]
    public void MeanReverting_MidWellBelowEwma_BuysMarketable()
    {
        var mr = new MeanRevertingStrategy("mr", alpha: 0.1, entryThresholdTicks: 3, crossTicks: 2, lotsPerOrder: 1);
        var rng = new Random(1);
        Assert.Empty(mr.Tick(State(mid: 100_0000), rng));
        var intents = mr.Tick(State(mid: 99_9000), rng).ToList();
        Assert.Single(intents);
        var ord = intents[0];
        Assert.Equal(OrderSide.Buy, ord.Side);
        Assert.Equal(99_9000 + 200, ord.PriceMantissa);
    }

    [Fact]
    public void MeanReverting_SmallMove_NoOrder()
    {
        var mr = new MeanRevertingStrategy("mr", alpha: 0.1, entryThresholdTicks: 5, crossTicks: 2, lotsPerOrder: 1);
        var rng = new Random(1);
        Assert.Empty(mr.Tick(State(mid: 100_0000), rng));
        // 2-tick move (< threshold of 5).
        Assert.Empty(mr.Tick(State(mid: 100_0200), rng));
    }

    [Fact]
    public void MeanReverting_InvalidArgs_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeanRevertingStrategy("x", alpha: 0, entryThresholdTicks: 1, crossTicks: 1, lotsPerOrder: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeanRevertingStrategy("x", alpha: 1.5, entryThresholdTicks: 1, crossTicks: 1, lotsPerOrder: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeanRevertingStrategy("x", alpha: 0.1, entryThresholdTicks: 0, crossTicks: 1, lotsPerOrder: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeanRevertingStrategy("x", alpha: 0.1, entryThresholdTicks: 1, crossTicks: 0, lotsPerOrder: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeanRevertingStrategy("x", alpha: 0.1, entryThresholdTicks: 1, crossTicks: 1, lotsPerOrder: 0));
    }

    // ---- Momentum -------------------------------------------------------

    [Fact]
    public void Momentum_FirstTick_SeedsAndEmitsNothing()
    {
        var m = new MomentumStrategy("mo", triggerTicks: 2, crossTicks: 1, lotsPerOrder: 1);
        Assert.Empty(m.Tick(State(), new Random(1)));
    }

    [Fact]
    public void Momentum_UpTickAboveThreshold_BuysWithMid()
    {
        var m = new MomentumStrategy("mo", triggerTicks: 2, crossTicks: 1, lotsPerOrder: 3);
        var rng = new Random(1);
        Assert.Empty(m.Tick(State(mid: 100_0000), rng));
        var intents = m.Tick(State(mid: 100_0300), rng).ToList(); // +3 ticks
        Assert.Single(intents);
        var ord = intents[0];
        Assert.Equal(OrderSide.Buy, ord.Side);
        Assert.Equal(OrderTifIntent.IOC, ord.Tif);
        Assert.Equal(100_0300 + 100, ord.PriceMantissa); // mid + 1 tick
        Assert.Equal(3 * 100, ord.Quantity);
    }

    [Fact]
    public void Momentum_DownTickAboveThreshold_Sells()
    {
        var m = new MomentumStrategy("mo", triggerTicks: 2, crossTicks: 1, lotsPerOrder: 1);
        var rng = new Random(1);
        Assert.Empty(m.Tick(State(mid: 100_0000), rng));
        var intents = m.Tick(State(mid: 99_9700), rng).ToList(); // -3 ticks
        Assert.Single(intents);
        Assert.Equal(OrderSide.Sell, intents[0].Side);
        Assert.Equal(99_9700 - 100, intents[0].PriceMantissa);
    }

    [Fact]
    public void Momentum_SubThresholdStep_NoOrder()
    {
        var m = new MomentumStrategy("mo", triggerTicks: 5, crossTicks: 1, lotsPerOrder: 1);
        var rng = new Random(1);
        Assert.Empty(m.Tick(State(mid: 100_0000), rng));
        Assert.Empty(m.Tick(State(mid: 100_0200), rng)); // +2 ticks < 5
    }

    [Fact]
    public void Momentum_PrevMidUpdatesEachTick()
    {
        var m = new MomentumStrategy("mo", triggerTicks: 2, crossTicks: 1, lotsPerOrder: 1);
        var rng = new Random(1);
        Assert.Empty(m.Tick(State(mid: 100_0000), rng));
        // Step 1: +2 ticks → BUY. Prev becomes 100_0200.
        Assert.Single(m.Tick(State(mid: 100_0200), rng));
        // Step 2: same mid → 0 step → no order. Prev stays 100_0200.
        Assert.Empty(m.Tick(State(mid: 100_0200), rng));
        // Step 3: another +2 ticks vs prev → BUY again.
        Assert.Single(m.Tick(State(mid: 100_0400), rng));
    }

    // ---- Sweeper --------------------------------------------------------

    [Fact]
    public void Sweeper_NeverFiresWhenProbabilityZero()
    {
        var sw = new SweeperStrategy("sw", triggerProbability: 0.0, sweepLots: 10, crossTicks: 5);
        var rng = new Random(1);
        for (int i = 0; i < 100; i++)
            Assert.Empty(sw.Tick(State(), rng));
    }

    [Fact]
    public void Sweeper_AlwaysFiresWhenProbabilityOne_AndSizesCorrectly()
    {
        var sw = new SweeperStrategy("sw", triggerProbability: 1.0, sweepLots: 7, crossTicks: 4);
        var rng = new Random(1);
        for (int i = 0; i < 20; i++)
        {
            var intents = sw.Tick(State(mid: 100_0000), rng).ToList();
            Assert.Single(intents);
            var ord = intents[0];
            Assert.Equal(OrderTifIntent.IOC, ord.Tif);
            Assert.Equal(7 * 100, ord.Quantity); // sweepLots * lot
            long expectedPx = ord.Side == OrderSide.Buy ? 100_0400 : 99_9600;
            Assert.Equal(expectedPx, ord.PriceMantissa);
        }
    }

    [Fact]
    public void Sweeper_DeterministicForSeededRng()
    {
        var s = State();
        var a = new SweeperStrategy("sw", triggerProbability: 0.4, sweepLots: 5, crossTicks: 3);
        var b = new SweeperStrategy("sw", triggerProbability: 0.4, sweepLots: 5, crossTicks: 3);
        var ra = new Random(123);
        var rb = new Random(123);
        var seqA = new List<OrderIntent>();
        var seqB = new List<OrderIntent>();
        for (int i = 0; i < 200; i++)
        {
            seqA.AddRange(a.Tick(s, ra));
            seqB.AddRange(b.Tick(s, rb));
        }
        Assert.NotEmpty(seqA);
        Assert.Equal(seqA.Count, seqB.Count);
        for (int i = 0; i < seqA.Count; i++)
        {
            Assert.Equal(seqA[i].Side, seqB[i].Side);
            Assert.Equal(seqA[i].PriceMantissa, seqB[i].PriceMantissa);
            Assert.Equal(seqA[i].Quantity, seqB[i].Quantity);
        }
    }

    [Fact]
    public void Sweeper_InvalidArgs_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweeperStrategy("x", triggerProbability: -0.1, sweepLots: 1, crossTicks: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweeperStrategy("x", triggerProbability: 1.1, sweepLots: 1, crossTicks: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweeperStrategy("x", triggerProbability: 0.5, sweepLots: 0, crossTicks: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweeperStrategy("x", triggerProbability: 0.5, sweepLots: 1, crossTicks: 0));
    }

    // ---- Config wiring --------------------------------------------------

    [Fact]
    public void BuildStrategy_KnownKinds_BuildsAllNewStrategies()
    {
        Assert.IsType<MeanRevertingStrategy>(SyntheticTraderConfigLoader.BuildStrategy(new StrategyConfig
        {
            Kind = "meanReverting",
            Alpha = 0.2,
            EntryThresholdTicks = 4,
            CrossTicks = 2,
            LotsPerOrder = 3,
        }, tickIntervalMs: 100));
        Assert.IsType<MomentumStrategy>(SyntheticTraderConfigLoader.BuildStrategy(new StrategyConfig
        {
            Kind = "momentum",
            TriggerTicks = 2,
            CrossTicks = 1,
            LotsPerOrder = 1,
        }, tickIntervalMs: 100));
        Assert.IsType<SweeperStrategy>(SyntheticTraderConfigLoader.BuildStrategy(new StrategyConfig
        {
            Kind = "sweeper",
            TriggerProbability = 0.05,
            SweepLots = 25,
            CrossTicks = 5,
        }, tickIntervalMs: 100));
        Assert.IsType<NewsShockStrategy>(SyntheticTraderConfigLoader.BuildStrategy(new StrategyConfig
        {
            Kind = "newsShock",
            MeanIntervalMs = 5000,
            JitterMs = 1000,
            ShockDurationMs = 1000,
            FadeDurationMs = 2000,
            LevelsToSweep = 3,
            BurstQtyLots = 5,
            DirectionBias = 0.5,
        }, tickIntervalMs: 100));
    }
}
