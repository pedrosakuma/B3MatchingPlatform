namespace B3.Exchange.Matching.Tests;

/// <summary>
/// Engine semantics for the MinQty subset of issue #203.
/// </summary>
public class MinQtyTests
{
    private static NewOrderCommand Limit(string id, Side side, decimal price, long qty, uint firm = 1, ulong minQty = 0)
        => new(id, TestFactory.PetrSecId, side, OrderType.Limit, TimeInForce.Day,
               TestFactory.Px(price), qty, firm, 0)
        { MinQty = minQty };

    [Fact]
    public void MinQty_zero_preserves_legacy_behaviour()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Sell, 10m, 100));
        engine.Submit(Limit("A1", Side.Buy, 10m, 200, minQty: 0));

        Assert.Single(sink.Trades);
        Assert.Empty(sink.Rejects);
        // Aggressor accepted, partial residual rests.
        Assert.Equal(2, sink.Accepted.Count);
    }

    [Fact]
    public void MinQty_met_by_book_allows_aggression_and_residual_rests()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Sell, 10m, 100));
        engine.Submit(Limit("M2", Side.Sell, 10m, 100));
        engine.Submit(Limit("A1", Side.Buy, 10m, 500, minQty: 200));

        Assert.Equal(2, sink.Trades.Count);
        Assert.Empty(sink.Rejects);
        Assert.Equal(3, sink.Accepted.Count); // 2 makers + aggressor
    }

    [Fact]
    public void MinQty_unmet_rejects_without_state_change()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Sell, 10m, 100));
        sink.Clear();

        engine.Submit(Limit("A1", Side.Buy, 10m, 500, minQty: 200));

        Assert.Empty(sink.Trades);
        Assert.Empty(sink.Accepted);
        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MinQtyNotMet, rej.Reason);
    }

    [Fact]
    public void MinQty_unmet_at_limit_price_rejects_even_with_deeper_levels()
    {
        // Only the level that crosses counts. A worse-priced level is ignored.
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Sell, 10m, 100));
        engine.Submit(Limit("M2", Side.Sell, 11m, 1_000));
        sink.Clear();

        engine.Submit(Limit("A1", Side.Buy, 10m, 500, minQty: 200));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MinQtyNotMet, rej.Reason);
    }

    [Fact]
    public void MinQty_greater_than_quantity_is_invalid_field()
    {
        var engine = TestFactory.NewEngine(out var sink);

        engine.Submit(Limit("A1", Side.Buy, 10m, 100, minQty: 200));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, rej.Reason);
    }

    [Fact]
    public void MinQty_with_empty_book_rejects_minqty_not_met()
    {
        var engine = TestFactory.NewEngine(out var sink);

        engine.Submit(Limit("A1", Side.Buy, 10m, 500, minQty: 200));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MinQtyNotMet, rej.Reason);
    }

    [Fact]
    public void MinQty_with_market_order_uses_full_opposite_book()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Sell, 10m, 100));
        engine.Submit(Limit("M2", Side.Sell, 11m, 200));
        sink.Clear();

        var market = new NewOrderCommand("A1", TestFactory.PetrSecId, Side.Buy,
            OrderType.Market, TimeInForce.IOC, 0, 200, 1, 0)
        { MinQty = 200 };
        engine.Submit(market);

        Assert.Empty(sink.Rejects);
        Assert.Equal(2, sink.Trades.Count);
    }

    [Fact]
    public void MinQty_with_market_order_unmet_rejects()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Sell, 10m, 100));
        sink.Clear();

        var market = new NewOrderCommand("A1", TestFactory.PetrSecId, Side.Buy,
            OrderType.Market, TimeInForce.IOC, 0, 500, 1, 0)
        { MinQty = 200 };
        engine.Submit(market);

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MinQtyNotMet, rej.Reason);
    }
}
