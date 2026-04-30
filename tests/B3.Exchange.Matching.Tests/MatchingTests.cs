namespace B3.Exchange.Matching.Tests;

using static TestFactory;

public class MatchingTests
{
    [Fact]
    public void LimitDay_NoCross_RestsAndAccepts()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        var acc = Assert.Single(sink.Accepted);
        Assert.Equal(100, acc.RemainingQuantity);
        Assert.Equal(Side.Buy, acc.Side);
        Assert.Equal(1u, acc.RptSeq);
        Assert.Empty(sink.Trades);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void Cross_FullFill_NoRemainder()
    {
        var eng = NewEngine(out var sink);
        // Resting sell @10.00 size 100
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 22, 1000));
        sink.Clear();
        // Buy @10.00 size 100 → exact match
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 2000));
        var trade = Assert.Single(sink.Trades);
        Assert.Equal(Px(10m), trade.PriceMantissa);
        Assert.Equal(100, trade.Quantity);
        Assert.Equal(Side.Buy, trade.AggressorSide);
        Assert.Equal(11u, trade.AggressorFirm);
        Assert.Equal(22u, trade.RestingFirm);
        var filled = Assert.Single(sink.Filled);
        Assert.Equal(Side.Sell, filled.Side);
        Assert.Empty(sink.Accepted);    // aggressor consumed, never rested
        Assert.Empty(sink.QtyReduced);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void Cross_PartialAggressorRests()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 22, 1000));
        sink.Clear();
        // Buy 300 @10.00 — eats 100, then rests 200
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 300, 11, 2000));
        Assert.Single(sink.Trades);
        Assert.Single(sink.Filled);
        var acc = Assert.Single(sink.Accepted);
        Assert.Equal(200, acc.RemainingQuantity);
        Assert.Equal(Side.Buy, acc.Side);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void Cross_PartialMakerReducedAndUpdate()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 500, 22, 1000));
        sink.Clear();
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 2000));
        Assert.Single(sink.Trades);
        var upd = Assert.Single(sink.QtyReduced);
        Assert.Equal(400, upd.NewRemainingQuantity);
        Assert.Empty(sink.Filled);
        Assert.Empty(sink.Accepted);   // aggressor fully consumed, no resting
        Assert.Equal(1, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void PriceTimePriority_FifoAcrossMakers()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 22, 1000));
        eng.Submit(new NewOrderCommand("s2", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 33, 1100));
        eng.Submit(new NewOrderCommand("s3", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 44, 1200));
        sink.Clear();
        // Buy 200 → consumes s1 fully, s2 fully (no partial — keeps the
        // assertion mix simple: 2 fills, 0 reduces).
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 200, 11, 2000));
        Assert.Equal(2, sink.Trades.Count);
        Assert.Equal(22u, sink.Trades[0].RestingFirm);
        Assert.Equal(33u, sink.Trades[1].RestingFirm);
        Assert.Equal(100, sink.Trades[0].Quantity);
        Assert.Equal(100, sink.Trades[1].Quantity);
        Assert.Equal(2, sink.Filled.Count);
        Assert.Empty(sink.QtyReduced);
        Assert.Empty(sink.Accepted);
    }

    [Fact]
    public void PriceTimePriority_BetterPricesFirst()
    {
        var eng = NewEngine(out var sink);
        // Sells at 10.02 then 10.00 then 10.01 — engine must walk 10.00, 10.01, 10.02
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.02m), 100, 22, 1000));
        eng.Submit(new NewOrderCommand("s2", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.00m), 100, 33, 1100));
        eng.Submit(new NewOrderCommand("s3", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.01m), 100, 44, 1200));
        sink.Clear();
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.05m), 300, 11, 2000));
        Assert.Equal(3, sink.Trades.Count);
        Assert.Equal(Px(10.00m), sink.Trades[0].PriceMantissa);
        Assert.Equal(Px(10.01m), sink.Trades[1].PriceMantissa);
        Assert.Equal(Px(10.02m), sink.Trades[2].PriceMantissa);
    }

    [Fact]
    public void Limit_NoCross_DoesNotMatch()
    {
        var eng = NewEngine(out var sink);
        // Resting ask @10.00; aggressor buy @ 9.99 — should rest, no trade
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 22, 1000));
        sink.Clear();
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.99m), 100, 11, 2000));
        Assert.Empty(sink.Trades);
        Assert.Single(sink.Accepted);
        Assert.Equal(2, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void IOC_PartialFill_RemainderDropped_NoAcceptedNoCanceled()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 22, 1000));
        sink.Clear();
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.IOC, Px(10m), 300, 11, 2000));
        Assert.Single(sink.Trades);
        Assert.Single(sink.Filled);
        Assert.Empty(sink.Accepted);
        Assert.Empty(sink.Canceled); // no MBO event for never-rested aggressor
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void FOK_Sufficient_FullyFills()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 200, 22, 1000));
        sink.Clear();
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.FOK, Px(10m), 200, 11, 2000));
        Assert.Single(sink.Trades);
        Assert.Single(sink.Filled);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void FOK_Insufficient_RejectedNoSideEffects()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 22, 1000));
        sink.Clear();
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.FOK, Px(10m), 200, 11, 2000));
        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.FokUnfillable, rej.Reason);
        Assert.Empty(sink.Trades);
        Assert.Empty(sink.Filled);
        Assert.Equal(1, eng.OrderCount(PetrSecId)); // s1 still resting
    }

    [Fact]
    public void Market_PartialThenEmpty_NoRestingNoCanceled()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 22, 1000));
        sink.Clear();
        // Market buy 300 — consumes 100 then book empty.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Market, TimeInForce.IOC, 0, 300, 11, 2000));
        Assert.Single(sink.Trades);
        Assert.Single(sink.Filled);
        Assert.Empty(sink.Accepted);
        Assert.Empty(sink.Canceled);
        Assert.Empty(sink.Rejects);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void TradeIds_AreMonotonic()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 22, 1000));
        eng.Submit(new NewOrderCommand("s2", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 33, 1100));
        sink.Clear();
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 200, 11, 2000));
        Assert.Equal(2, sink.Trades.Count);
        Assert.Equal(1u, sink.Trades[0].TradeId);
        Assert.Equal(2u, sink.Trades[1].TradeId);
    }

    [Fact]
    public void RptSeq_IncrementsOnEveryEmittedEvent()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 22, 1000));
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 2000));
        // Expect: Accepted(s1, rpt=1), Trade(rpt=2), Filled(s1, rpt=3)
        Assert.Equal(1u, sink.Accepted[0].RptSeq);
        Assert.Equal(2u, sink.Trades[0].RptSeq);
        Assert.Equal(3u, sink.Filled[0].RptSeq);
        Assert.Equal(3u, eng.CurrentRptSeq);
    }
}
