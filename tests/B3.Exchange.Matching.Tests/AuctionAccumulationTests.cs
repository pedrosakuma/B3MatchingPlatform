namespace B3.Exchange.Matching.Tests;

using static TestFactory;

/// <summary>
/// Issue #228 (Onda M · M1): in <see cref="TradingPhase.Reserved"/>
/// (opening call) and <see cref="TradingPhase.FinalClosingCall"/>
/// (closing call) the engine must accept new orders without running
/// continuous matching — orders accumulate on the book until an
/// operator-issued auction uncross (M3) clears them at a single price.
/// </summary>
public class AuctionAccumulationTests
{
    [Fact]
    public void GoodForAuction_CrossingPair_DuringReserved_DoesNotTrade()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        sink.Clear();

        // Buy @ 10.05 and Sell @ 10.00 would cross immediately during
        // continuous Open — during the auction call they must rest in
        // place and wait for uncrossing.
        eng.Submit(new NewOrderCommand("buy", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("sell", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, 6001));

        Assert.Empty(sink.Trades);
        Assert.Empty(sink.Rejects);
        Assert.Equal(2, sink.Accepted.Count);
        Assert.Equal(2, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void AtClose_CrossingPair_DuringFinalClosingCall_DoesNotTrade()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.FinalClosingCall, 5000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("buy", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.AtClose, Px(10.05m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("sell", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.AtClose, Px(10.00m), 100, 12, 6001));

        Assert.Empty(sink.Trades);
        Assert.Empty(sink.Rejects);
        Assert.Equal(2, sink.Accepted.Count);
        Assert.Equal(2, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void GoodForAuction_RestedOnAuctionBook_TradesOnceTransitionsToOpen()
    {
        // Verifies the transition path: orders accumulated in Reserved
        // remain on the book after SetTradingPhase(Open) and a new
        // crossing aggressor matches against them via the normal
        // continuous-matching path. (M3 will replace this implicit
        // path with an explicit uncrossing call, but M1 must not
        // break it.)
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("rest", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 11, 6000));
        Assert.Empty(sink.Trades);

        eng.SetTradingPhase(PetrSecId, TradingPhase.Open, 6500);
        sink.Clear();

        eng.Submit(new NewOrderCommand("agg", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.00m), 100, 12, 7000));

        Assert.Single(sink.Trades);
        Assert.Equal(100, sink.Trades[0].Quantity);
        Assert.Equal(Px(10.00m), sink.Trades[0].PriceMantissa);
    }

    [Fact]
    public void GoodForAuction_Iceberg_RejectedByExistingTifGuard()
    {
        // #211 already restricts MaxFloor to Day/Gtc TIFs (an iceberg
        // resting under GoodForAuction has no replenish opportunity
        // because the order expires post-uncross). M1 does not relax
        // that guard. This test pins the behavior so a future change
        // to iceberg+auction must update both this test and #211.
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("ice", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 500, 11, 6000)
        { MaxFloor = 100 });

        Assert.Single(sink.Rejects);
        Assert.Empty(sink.Accepted);
    }

    [Fact]
    public void OpenPhase_ContinuousMatchingPath_StillTrades()
    {
        // Sanity: the M1 branch must not affect the Open phase.
        var eng = NewEngine(out var sink);

        eng.Submit(new NewOrderCommand("rest", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.00m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("agg", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.00m), 100, 12, 6001));

        Assert.Single(sink.Trades);
    }
}
