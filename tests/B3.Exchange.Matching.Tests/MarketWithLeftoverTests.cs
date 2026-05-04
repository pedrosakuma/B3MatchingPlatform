namespace B3.Exchange.Matching.Tests;

/// <summary>
/// Engine semantics for MarketWithLeftoverAsLimit (FIX OrdType 'K' /
/// issue #215). Validates: market-style sweeping of the opposite book,
/// leftover-rests-as-Day-Limit at last execution price, validation
/// rejects (TIF, Price, no-liquidity).
/// </summary>
public class MarketWithLeftoverTests
{
    private static NewOrderCommand Mwl(string id, Side side, long qty, uint firm = 1)
        => new(id, TestFactory.PetrSecId, side, OrderType.MarketWithLeftover,
               TimeInForce.Day, PriceMantissa: 0L, qty, firm, EnteredAtNanos: 0);

    private static NewOrderCommand Limit(string id, Side side, decimal price, long qty,
        TimeInForce tif = TimeInForce.Day, uint firm = 2)
        => new(id, TestFactory.PetrSecId, side, OrderType.Limit, tif,
               TestFactory.Px(price), qty, firm, EnteredAtNanos: 0);

    [Fact]
    public void Mwl_consumes_book_and_rests_remainder_at_last_trade_price()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // Two ask levels: 100@10, 200@11. MWL buy 500 → consumes 300,
        // last trade at 11 → 200 leftover rests as Day Limit Buy at 11.
        engine.Submit(Limit("S1", Side.Sell, 10m, 100, firm: 9));
        engine.Submit(Limit("S2", Side.Sell, 11m, 200, firm: 9));
        sink.Clear();

        engine.Submit(Mwl("MWL", Side.Buy, 500));

        Assert.Equal(2, sink.Trades.Count);
        Assert.Equal(TestFactory.Px(10m), sink.Trades[0].PriceMantissa);
        Assert.Equal(TestFactory.Px(11m), sink.Trades[1].PriceMantissa);
        var rested = Assert.Single(sink.Accepted);
        Assert.Equal(TestFactory.Px(11m), rested.PriceMantissa);
        Assert.Equal(200, rested.RemainingQuantity);
        Assert.Equal(Side.Buy, rested.Side);
    }

    [Fact]
    public void Mwl_fully_filled_emits_no_resting_order()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("S", Side.Sell, 10m, 500, firm: 9));
        sink.Clear();

        engine.Submit(Mwl("MWL", Side.Buy, 300));

        Assert.Single(sink.Trades);
        Assert.Equal(300, sink.Trades[0].Quantity);
        Assert.Empty(sink.Accepted);
    }

    [Fact]
    public void Mwl_with_empty_book_rejected_marketnoliquidity()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Mwl("MWL", Side.Buy, 100));
        var r = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MarketNoLiquidity, r.Reason);
    }

    [Fact]
    public void Mwl_with_non_day_tif_rejected()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(new NewOrderCommand("MWL", TestFactory.PetrSecId, Side.Buy,
            OrderType.MarketWithLeftover, TimeInForce.IOC, PriceMantissa: 0L,
            Quantity: 100, EnteringFirm: 1, EnteredAtNanos: 0));
        var r = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, r.Reason);
    }

    [Fact]
    public void Mwl_with_nonzero_price_rejected()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(new NewOrderCommand("MWL", TestFactory.PetrSecId, Side.Buy,
            OrderType.MarketWithLeftover, TimeInForce.Day,
            PriceMantissa: TestFactory.Px(10m),
            Quantity: 100, EnteringFirm: 1, EnteredAtNanos: 0));
        var r = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, r.Reason);
    }

    [Fact]
    public void Mwl_leftover_can_be_traded_against_subsequent_aggressor()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("S1", Side.Sell, 10m, 100, firm: 9));
        engine.Submit(Limit("S2", Side.Sell, 11m, 100, firm: 9));
        engine.Submit(Mwl("MWL", Side.Buy, 300)); // 100@10 + 100@11; 100 rests @11 buy
        sink.Clear();

        // Sell aggressor at 11 hits the rested MWL leftover.
        engine.Submit(Limit("AGG", Side.Sell, 11m, 100, firm: 5, tif: TimeInForce.IOC));

        var t = Assert.Single(sink.Trades);
        Assert.Equal(TestFactory.Px(11m), t.PriceMantissa);
        Assert.Equal(100, t.Quantity);
    }
}
