namespace B3.Exchange.Matching.Tests;

using static TestFactory;

/// <summary>
/// Issue #229 (Onda M · M2): the engine recomputes the Theoretical
/// Opening Price (TOP) and the auction imbalance every time the
/// auction-phase book mutates and emits an
/// <see cref="AuctionTopChangedEvent"/> only when the indicative state
/// actually changes.
/// </summary>
public class AuctionTopComputationTests
{
    [Fact]
    public void NoCrossing_OnlyBuySide_EmitsImbalanceWithoutTop()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 11, 6000));

        var ev = Assert.Single(sink.AuctionTops);
        Assert.False(ev.HasTop);
        Assert.True(ev.HasImbalance);
        Assert.Equal(Side.Buy, ev.ImbalanceSide);
        Assert.Equal(100, ev.ImbalanceQuantity);
    }

    [Fact]
    public void Crossing_PerfectMatch_EmitsTopAtSinglePriceWithoutImbalance()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, 6001));

        // After both orders rested, last event should show TOP discovered.
        var last = sink.AuctionTops[^1];
        Assert.True(last.HasTop);
        Assert.Equal(100, last.TopQuantity);
        Assert.False(last.HasImbalance);
    }

    [Fact]
    public void Crossing_ImbalancedDepth_TopAtMaxMatchedAndImbalanceOnHeavySide()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        sink.Clear();

        // Buys 300 @ 10.05, Sells 100 @ 10.00 → max-matched = 100, residual = 200 buy.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 300, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, 6001));

        var last = sink.AuctionTops[^1];
        Assert.True(last.HasTop);
        Assert.Equal(100, last.TopQuantity);
        Assert.True(last.HasImbalance);
        Assert.Equal(Side.Buy, last.ImbalanceSide);
        Assert.Equal(200, last.ImbalanceQuantity);
    }

    [Fact]
    public void DuplicateAccumulation_DoesNotEmitWhenStateUnchanged()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);

        // Seed buy-only state.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 11, 6000));
        sink.Clear();

        // Cancel the buy → state changes (one event), then re-add identical
        // qty/price → state changes back. Two emissions expected.
        eng.Cancel(new CancelOrderCommand("b1", PetrSecId, OrderId: 1, EnteredAtNanos: 6001));
        Assert.Single(sink.AuctionTops);
        eng.Submit(new NewOrderCommand("b2", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 11, 6002));
        Assert.Equal(2, sink.AuctionTops.Count);
    }

    [Fact]
    public void CancelDuringAuction_RecomputesTop()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);

        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, 6001));
        sink.Clear();

        eng.Cancel(new CancelOrderCommand("s1", PetrSecId, OrderId: 2, EnteredAtNanos: 6002));

        var last = sink.AuctionTops[^1];
        Assert.False(last.HasTop);
        Assert.True(last.HasImbalance);
        Assert.Equal(Side.Buy, last.ImbalanceSide);
        Assert.Equal(100, last.ImbalanceQuantity);
    }

    [Fact]
    public void NotEmitted_OutsideAuctionPhase()
    {
        var eng = NewEngine(out var sink);
        // Default phase is Open (no SetTradingPhase). Submit a regular order.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.00m), 100, 11, 6000));
        Assert.Empty(sink.AuctionTops);
    }
}
