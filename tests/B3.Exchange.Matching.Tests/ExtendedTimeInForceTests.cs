namespace B3.Exchange.Matching.Tests;

using static TestFactory;

/// <summary>
/// Issue #202 (gap-functional §9): the engine must accept the full
/// B3 TimeInForce surface — Day, IOC, FOK, GTC, GTD, AtClose,
/// GoodForAuction. GTC is currently identical to Day (the daily-reset
/// operator command exists but GTC is not swept); GTD (GAP-23 / #499)
/// rests with an <c>ExpireDate</c> and is swept at the end-of-day GTD
/// boundary; AtClose and GoodForAuction are gated on the instrument's
/// <see cref="TradingPhase"/> (FinalClosingCall and Reserved
/// respectively).
/// </summary>
public class ExtendedTimeInForceTests
{
    [Fact]
    public void Gtc_Limit_RestsLikeDayOrder()
    {
        var eng = NewEngine(out var sink);

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtc, Px(10m), 100, 11, 1000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void Gtd_WithExpireDate_RestsLikeDayOrder()
    {
        var eng = NewEngine(out var sink);

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtd, Px(10m), 100, 11, 1000)
        {
            ExpireDate = 20_000,
        });

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void Gtd_WithoutExpireDate_RejectedAsInvalidField()
    {
        var eng = NewEngine(out var sink);

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtd, Px(10m), 100, 11, 1000));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, rej.Reason);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void NonGtd_WithExpireDate_RejectedAsInvalidField()
    {
        // Defense for direct/WAL-replay commands: an ExpireDate is only
        // meaningful on a GTD order.
        var eng = NewEngine(out var sink);

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000)
        {
            ExpireDate = 20_000,
        });

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, rej.Reason);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void AtClose_DuringOpen_RejectsAsMarketClosed()
    {
        var eng = NewEngine(out var sink);

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.AtClose, Px(10m), 100, 11, 1000));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MarketClosed, rej.Reason);
    }

    [Fact]
    public void AtClose_DuringFinalClosingCall_Accepted()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.FinalClosingCall, 5000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.AtClose, Px(10m), 100, 11, 6000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void GoodForAuction_DuringOpen_RejectsAsMarketClosed()
    {
        var eng = NewEngine(out var sink);

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10m), 100, 11, 1000));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MarketClosed, rej.Reason);
    }

    [Fact]
    public void GoodForAuction_DuringReserved_Accepted()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10m), 100, 11, 6000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void DayOrder_DuringFinalClosingCall_RejectsAsMarketClosed()
    {
        // Day TIF still requires Open phase even when the market is in
        // FinalClosingCall — only AtClose is admitted then.
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.FinalClosingCall, 5000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 6000));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MarketClosed, rej.Reason);
    }

    [Fact]
    public void Gtc_Market_Rejected()
    {
        // Market orders must be IOC/FOK regardless of TIF extension.
        var eng = NewEngine(out var sink);

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Market, TimeInForce.Gtc, 0, 100, 11, 1000));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MarketNotImmediateOrCancel, rej.Reason);
    }

    [Fact]
    public void Gtc_CrossesAndFills_Normally()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("seller", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        sink.Clear();

        eng.Submit(new NewOrderCommand("buyer", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtc, Px(10m), 100, 12, 2000));

        Assert.Single(sink.Trades);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }
}
