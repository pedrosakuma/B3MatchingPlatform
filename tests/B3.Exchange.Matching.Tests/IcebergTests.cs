namespace B3.Exchange.Matching.Tests;

/// <summary>
/// Engine semantics for the Iceberg (MaxFloor) subset of issue #211.
/// Validates: visible-only book exposure, replenish on visible exhaustion,
/// time-priority loss after replenish, full consumption transitions to
/// OrderFilled, validation of MaxFloor against quantity / lot / type / TIF.
/// </summary>
public class IcebergTests
{
    private static NewOrderCommand IcebergSell(string id, decimal price, long qty, ulong maxFloor,
        TimeInForce tif = TimeInForce.Day, uint firm = 1)
        => new(id, TestFactory.PetrSecId, Side.Sell, OrderType.Limit, tif,
               TestFactory.Px(price), qty, firm, 0)
        { MaxFloor = maxFloor };

    private static NewOrderCommand BuyLimit(string id, decimal price, long qty,
        TimeInForce tif = TimeInForce.Day, uint firm = 2)
        => new(id, TestFactory.PetrSecId, Side.Buy, OrderType.Limit, tif,
               TestFactory.Px(price), qty, firm, 0);

    [Fact]
    public void Iceberg_exposes_only_visible_slice_on_accept()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // Total 1000, visible 200.
        engine.Submit(IcebergSell("ICE", 10m, 1000, maxFloor: 200));

        var accept = sink.Accepted.Single();
        Assert.Equal(200, accept.RemainingQuantity);
    }

    [Fact]
    public void Iceberg_replenishes_visible_slice_when_exhausted()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(IcebergSell("ICE", 10m, 1000, maxFloor: 200));
        sink.Clear();

        // Aggressor buys exactly the visible slice → triggers replenish.
        engine.Submit(BuyLimit("AGG", 10m, 200, tif: TimeInForce.IOC));

        Assert.Single(sink.Trades);
        Assert.Equal(200, sink.Trades[0].Quantity);
        // Replenish event with new visible 200 and remaining hidden 600.
        var rep = Assert.Single(sink.Replenishments);
        Assert.Equal(200, rep.NewVisibleQuantity);
        Assert.Equal(600, rep.RemainingHiddenQuantity);
        // No OrderFilled (the iceberg is still alive).
        Assert.Empty(sink.Filled);
    }

    [Fact]
    public void Iceberg_replenish_loses_time_priority_at_same_level()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // Iceberg first, then a regular order at the SAME price → iceberg
        // is at head of queue.
        engine.Submit(IcebergSell("ICE", 10m, 1000, maxFloor: 200));
        engine.Submit(new NewOrderCommand("REG", TestFactory.PetrSecId, Side.Sell,
            OrderType.Limit, TimeInForce.Day, TestFactory.Px(10m), 300, 9, 0));
        sink.Clear();

        // First aggressor consumes iceberg's visible 200 → replenish puts
        // iceberg at the BACK of the level (now after REG).
        engine.Submit(BuyLimit("AGG1", 10m, 200, tif: TimeInForce.IOC));
        Assert.Single(sink.Trades);
        Assert.Equal(sink.Replenishments.Single().OrderId, sink.Trades[0].RestingOrderId);
        sink.Clear();

        // Second aggressor for 300 should hit REG first (now at head),
        // not the replenished iceberg.
        engine.Submit(BuyLimit("AGG2", 10m, 300, tif: TimeInForce.IOC));
        var trade = Assert.Single(sink.Trades);
        Assert.Equal(300, trade.Quantity);
        // The traded resting orderId must be REG, not the iceberg.
        var iceOid = engine; // alias — we don't hold it; assert via Filled event
        var filled = Assert.Single(sink.Filled);
        Assert.Equal(trade.RestingOrderId, filled.OrderId);
    }

    [Fact]
    public void Iceberg_final_consumption_emits_OrderFilled()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // Total 300, visible 100 → 3 slices, third one has hidden 0 left.
        engine.Submit(IcebergSell("ICE", 10m, 300, maxFloor: 100));
        sink.Clear();

        // Walk three visible slices: replenish twice, then final fill.
        engine.Submit(BuyLimit("AGG1", 10m, 100, tif: TimeInForce.IOC));
        engine.Submit(BuyLimit("AGG2", 10m, 100, tif: TimeInForce.IOC));
        engine.Submit(BuyLimit("AGG3", 10m, 100, tif: TimeInForce.IOC));

        Assert.Equal(3, sink.Trades.Count);
        Assert.Equal(2, sink.Replenishments.Count);
        // After the 3rd consumption, hidden was already 0 → OrderFilled.
        var filled = Assert.Single(sink.Filled);
        Assert.Equal(100, filled.FinalFilledQuantity);
    }

    [Fact]
    public void Iceberg_partial_visible_consumption_only_reduces_quantity()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(IcebergSell("ICE", 10m, 1000, maxFloor: 300));
        sink.Clear();

        // Aggressor takes less than the visible slice → no replenish.
        engine.Submit(BuyLimit("AGG", 10m, 100, tif: TimeInForce.IOC));

        Assert.Single(sink.Trades);
        Assert.Empty(sink.Replenishments);
        var qr = Assert.Single(sink.QtyReduced);
        Assert.Equal(200, qr.NewRemainingQuantity);
    }

    [Fact]
    public void Iceberg_with_maxfloor_equal_to_quantity_acts_as_normal_order()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(IcebergSell("ICE", 10m, 200, maxFloor: 200));
        sink.Clear();

        engine.Submit(BuyLimit("AGG", 10m, 200, tif: TimeInForce.IOC));

        // No hidden reserve → full consumption emits OrderFilled, no replenish.
        Assert.Single(sink.Trades);
        Assert.Empty(sink.Replenishments);
        Assert.Single(sink.Filled);
    }

    [Fact]
    public void Iceberg_with_maxfloor_greater_than_quantity_rejects_invalid_field()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(IcebergSell("ICE", 10m, 100, maxFloor: 200));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, rej.Reason);
    }

    [Fact]
    public void Iceberg_with_non_lot_maxfloor_rejects_invalid_field()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // PETR4 LotSize=100; MaxFloor=150 violates lot multiple.
        engine.Submit(IcebergSell("ICE", 10m, 1000, maxFloor: 150));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, rej.Reason);
    }

    [Fact]
    public void Iceberg_on_market_order_rejects()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(BuyLimit("M1", 10m, 100));
        sink.Clear();

        var market = new NewOrderCommand("ICE", TestFactory.PetrSecId, Side.Sell,
            OrderType.Market, TimeInForce.IOC, 0, 1000, 1, 0)
        { MaxFloor = 200 };
        engine.Submit(market);

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, rej.Reason);
    }

    [Fact]
    public void Iceberg_on_ioc_rejects()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(IcebergSell("ICE", 10m, 1000, maxFloor: 200, tif: TimeInForce.IOC));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, rej.Reason);
    }

    [Fact]
    public void Iceberg_aggressor_keeps_consuming_after_replenish_skip()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // Iceberg 1000@10 visible 200, plus a regular 500@11.
        engine.Submit(IcebergSell("ICE", 10m, 1000, maxFloor: 200));
        engine.Submit(IcebergSell("REG", 11m, 500, maxFloor: 0));
        sink.Clear();

        // Buy 600 at 11 → consume iceberg visible 200 (replenish, but
        // replenished slice not re-eligible in the same dispatch),
        // then 400 against REG.
        engine.Submit(BuyLimit("AGG", 11m, 600, tif: TimeInForce.IOC));

        Assert.Equal(2, sink.Trades.Count);
        Assert.Equal(200, sink.Trades[0].Quantity);
        Assert.Equal(400, sink.Trades[1].Quantity);
        Assert.Single(sink.Replenishments);
    }
}
