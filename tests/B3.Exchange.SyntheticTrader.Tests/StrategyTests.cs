namespace B3.Exchange.SyntheticTrader.Tests;

/// <summary>
/// Strategy unit tests. Strategies are pure functions of their input
/// (state + RNG); we can drive them with hand-built MarketState snapshots
/// and a seeded Random and assert the exact intent sequence.
/// </summary>
public class StrategyTests
{
    private static MarketState State(long mid = 100_0000, long tick = 100, long lot = 100,
        IReadOnlyList<LiveOrder>? live = null)
        => new(SecurityId: 1, MidMantissa: mid, TickSize: tick, LotSize: lot,
            LastTradePxMantissa: mid, LastTradeQty: 0,
            MyLiveOrders: live ?? Array.Empty<LiveOrder>());

    [Fact]
    public void MarketMaker_FromEmptyBook_PostsLevelsBothSides()
    {
        var mm = new MarketMakerStrategy("mm", levelsPerSide: 3, quoteSpacingTicks: 1, replaceDistanceTicks: 5, quantity: 100);
        var s = State();
        var intents = mm.Tick(s, new Random(1)).ToList();

        Assert.Equal(6, intents.Count);
        // Three bids at 100_0000-100, -200, -300; three asks symmetric.
        var bids = intents.Where(i => i.Kind == OrderIntentKind.New && i.Side == OrderSide.Buy)
            .OrderByDescending(i => i.PriceMantissa).Select(i => i.PriceMantissa).ToArray();
        var asks = intents.Where(i => i.Kind == OrderIntentKind.New && i.Side == OrderSide.Sell)
            .OrderBy(i => i.PriceMantissa).Select(i => i.PriceMantissa).ToArray();
        Assert.Equal(new long[] { 99_9900, 99_9800, 99_9700 }, bids);
        Assert.Equal(new long[] { 100_0100, 100_0200, 100_0300 }, asks);
        Assert.All(intents, i => Assert.Equal(100, i.Quantity));
        Assert.All(intents, i => Assert.Equal(OrderTifIntent.Day, i.Tif));
    }

    [Fact]
    public void MarketMaker_WithSomeLevelsLive_OnlyTopsUpMissingOnes()
    {
        var mm = new MarketMakerStrategy("mm", levelsPerSide: 2, quoteSpacingTicks: 1, replaceDistanceTicks: 5, quantity: 100);
        var live = new List<LiveOrder>
        {
            new("mm-1-B-1", OrderSide.Buy,  99_9900, 100), // matches level 1 bid
            new("mm-1-S-1", OrderSide.Sell, 100_0100, 100), // matches level 1 ask
        };
        var intents = mm.Tick(State(live: live), new Random(1)).ToList();

        // Only level 2 missing on each side.
        Assert.Equal(2, intents.Count);
        var prices = intents.Select(i => i.PriceMantissa).OrderBy(p => p).ToArray();
        Assert.Equal(new long[] { 99_9800, 100_0200 }, prices);
    }

    [Fact]
    public void MarketMaker_DriftedQuote_IsCancelled()
    {
        var mm = new MarketMakerStrategy("mm", levelsPerSide: 1, quoteSpacingTicks: 1, replaceDistanceTicks: 2, quantity: 100);
        // A live bid 50 ticks below mid → way past replaceDistance=2 → cancel.
        var live = new List<LiveOrder>
        {
            new("stale", OrderSide.Buy, 99_5000, 100),
        };
        var intents = mm.Tick(State(live: live), new Random(1)).ToList();
        Assert.Contains(intents, i => i.Kind == OrderIntentKind.Cancel && i.CancelTag == "stale");
        // And a fresh bid at mid-1 tick.
        Assert.Contains(intents, i => i.Kind == OrderIntentKind.New && i.Side == OrderSide.Buy && i.PriceMantissa == 99_9900);
    }

    [Fact]
    public void NoiseTaker_SeededRng_ProducesDeterministicSequence()
    {
        var s = State();
        var nt1 = new NoiseTakerStrategy("noise", orderProbability: 0.5, maxLotMultiple: 3, crossTicks: 2);
        var nt2 = new NoiseTakerStrategy("noise", orderProbability: 0.5, maxLotMultiple: 3, crossTicks: 2);
        var rng1 = new Random(123);
        var rng2 = new Random(123);

        var seq1 = new List<OrderIntent>();
        var seq2 = new List<OrderIntent>();
        for (int i = 0; i < 50; i++)
        {
            seq1.AddRange(nt1.Tick(s, rng1));
            seq2.AddRange(nt2.Tick(s, rng2));
        }
        Assert.Equal(seq1.Count, seq2.Count);
        for (int i = 0; i < seq1.Count; i++)
        {
            Assert.Equal(seq1[i].Side, seq2[i].Side);
            Assert.Equal(seq1[i].Quantity, seq2[i].Quantity);
            Assert.Equal(seq1[i].PriceMantissa, seq2[i].PriceMantissa);
            Assert.Equal(seq1[i].Tif, seq2[i].Tif);
        }
        Assert.NotEmpty(seq1); // probabilistic but with seed=123 and p=0.5 we expect plenty
    }

    [Fact]
    public void NoiseTaker_PriceAlwaysMarketableInDirection()
    {
        var s = State(mid: 100_0000);
        var nt = new NoiseTakerStrategy("noise", orderProbability: 1.0, maxLotMultiple: 1, crossTicks: 2);
        var rng = new Random(7);
        for (int i = 0; i < 30; i++)
        {
            var intents = nt.Tick(s, rng).ToList();
            Assert.Single(intents);
            var ord = intents[0];
            if (ord.Side == OrderSide.Buy)
                Assert.Equal(100_0200, ord.PriceMantissa); // mid + 2*tick
            else
                Assert.Equal(99_9800, ord.PriceMantissa);  // mid - 2*tick
            Assert.Equal(OrderTifIntent.IOC, ord.Tif);
        }
    }
}
