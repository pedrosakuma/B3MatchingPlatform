namespace B3.Exchange.Matching.Tests;

using B3.Exchange.Instruments;
using static TestFactory;

public class CancelReplaceTests
{
    [Fact]
    public void Cancel_FoundOrder_RemovesAndEmits()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        var oid = sink.Accepted[0].OrderId;
        sink.Clear();
        eng.Cancel(new CancelOrderCommand("c1", PetrSecId, oid, 2000));
        var c = Assert.Single(sink.Canceled);
        Assert.Equal(oid, c.OrderId);
        Assert.Equal(100, c.RemainingQuantityAtCancel);
        Assert.Equal(CancelReason.Client, c.Reason);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void Cancel_UnknownOrderId_Rejects()
    {
        var eng = NewEngine(out var sink);
        eng.Cancel(new CancelOrderCommand("c1", PetrSecId, 12345L, 2000));
        Assert.Equal(RejectReason.UnknownOrderId, sink.Rejects[0].Reason);
    }

    [Fact]
    public void Replace_QtyDecrease_PreservesPriority_EmitsQtyReduced()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 500, 11, 1000));
        var oid = sink.Accepted[0].OrderId;
        sink.Clear();
        eng.Replace(new ReplaceOrderCommand("c1", PetrSecId, oid, Px(10m), 300, 2000));
        var u = Assert.Single(sink.QtyReduced);
        Assert.Equal(300, u.NewRemainingQuantity);
        Assert.Equal(1000UL, u.InsertTimestampNanos); // priority preserved → original timestamp
        Assert.Equal(2000UL, u.TransactTimeNanos);
        Assert.Empty(sink.Canceled);
    }

    [Fact]
    public void Replace_PriceChange_LosesPriority_EmitsCancelThenAccept()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        var oid = sink.Accepted[0].OrderId;
        sink.Clear();
        eng.Replace(new ReplaceOrderCommand("c1", PetrSecId, oid, Px(10.05m), 100, 2000));
        var c = Assert.Single(sink.Canceled);
        Assert.Equal(CancelReason.ReplaceLostPriority, c.Reason);
        var a = Assert.Single(sink.Accepted);
        Assert.NotEqual(oid, a.OrderId);    // new order id
        Assert.Equal(Px(10.05m), a.PriceMantissa);
        Assert.Equal(2000UL, a.InsertTimestampNanos);
    }

    [Fact]
    public void Replace_QtyIncrease_LosesPriority()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        var oid = sink.Accepted[0].OrderId;
        sink.Clear();
        eng.Replace(new ReplaceOrderCommand("c1", PetrSecId, oid, Px(10m), 200, 2000));
        Assert.Single(sink.Canceled);
        Assert.Single(sink.Accepted);
    }

    [Fact]
    public void Replace_PartialFilledThenQtyDecrease_PreservesPriority()
    {
        var eng = NewEngine(out var sink);
        // Resting buy 500 @10
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 500, 11, 1000));
        var bid = sink.Accepted[0].OrderId;
        // Sell 200 crosses → buy now has 300 remaining
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 200, 22, 1500));
        sink.Clear();
        // Replace down to 200 → still <= remaining(300), priority kept
        eng.Replace(new ReplaceOrderCommand("b1", PetrSecId, bid, Px(10m), 200, 2000));
        var u = Assert.Single(sink.QtyReduced);
        Assert.Equal(200, u.NewRemainingQuantity);
        Assert.Empty(sink.Canceled);
    }

    [Fact]
    public void Replace_PriceChange_AggressivelyCrosses()
    {
        var eng = NewEngine(out var sink);
        // Resting sell @10, buy @9.95
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 22, 1000));
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.95m), 100, 11, 1100));
        var bid = sink.Accepted[1].OrderId;
        sink.Clear();
        // Replace buy to @10.00 → should cross s1
        eng.Replace(new ReplaceOrderCommand("b1", PetrSecId, bid, Px(10m), 100, 2000));
        Assert.Single(sink.Canceled);     // old buy removed (priority lost)
        Assert.Single(sink.Trades);       // crossed s1
        Assert.Single(sink.Filled);       // s1 filled
        Assert.Empty(sink.Accepted);      // replacement consumed, did not rest
    }

    [Fact]
    public void Replace_InvalidPrice_RejectsLeavesBookUnchanged()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        var oid = sink.Accepted[0].OrderId;
        sink.Clear();
        eng.Replace(new ReplaceOrderCommand("c1", PetrSecId, oid, 100050, 100, 2000));
        Assert.Equal(RejectReason.PriceNotOnTick, sink.Rejects[0].Reason);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
        Assert.Empty(sink.Canceled);
        Assert.Empty(sink.QtyReduced);
    }

    [Fact]
    public void Replace_UnknownOrderId_Rejects()
    {
        var eng = NewEngine(out var sink);
        eng.Replace(new ReplaceOrderCommand("c1", PetrSecId, 999L, Px(10m), 100, 2000));
        Assert.Equal(RejectReason.UnknownOrderId, sink.Rejects[0].Reason);
    }
}

public class SnapshotEnumerationTests
{
    [Fact]
    public void EnumerateOrders_PriceTimePriority()
    {
        var eng = NewEngine(out _);
        eng.Submit(new NewOrderCommand("a", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.02m), 100, 22, 1000));
        eng.Submit(new NewOrderCommand("b", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.00m), 200, 33, 1100));
        eng.Submit(new NewOrderCommand("c", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.00m), 300, 44, 1200));
        var ord = eng.EnumerateBook(PetrSecId, Side.Sell).ToList();
        Assert.Equal(3, ord.Count);
        // Best price first; within price, FIFO by insert order
        Assert.Equal(Px(10.00m), ord[0].PriceMantissa); Assert.Equal(200, ord[0].RemainingQuantity); Assert.Equal(33u, ord[0].EnteringFirm);
        Assert.Equal(Px(10.00m), ord[1].PriceMantissa); Assert.Equal(300, ord[1].RemainingQuantity); Assert.Equal(44u, ord[1].EnteringFirm);
        Assert.Equal(Px(10.02m), ord[2].PriceMantissa);
    }

    [Fact]
    public void EnumerateOrders_BuysHighPriceFirst()
    {
        var eng = NewEngine(out _);
        eng.Submit(new NewOrderCommand("a", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.95m), 100, 22, 1000));
        eng.Submit(new NewOrderCommand("b", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.00m), 100, 33, 1100));
        eng.Submit(new NewOrderCommand("c", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.99m), 100, 44, 1200));
        var ord = eng.EnumerateBook(PetrSecId, Side.Buy).ToList();
        Assert.Equal(Px(10.00m), ord[0].PriceMantissa);
        Assert.Equal(Px(9.99m), ord[1].PriceMantissa);
        Assert.Equal(Px(9.95m), ord[2].PriceMantissa);
    }
}

public class ReentrancyTests
{
    private sealed class ReentrantSink : IMatchingEventSink
    {
        public MatchingEngine? Engine;
        public Exception? Captured;
        public void OnOrderAccepted(in OrderAcceptedEvent e)
        {
            try { Engine!.Cancel(new CancelOrderCommand("x", e.SecurityId, e.OrderId, 0)); }
            catch (Exception ex) { Captured = ex; }
        }
        public void OnOrderQuantityReduced(in OrderQuantityReducedEvent e) { }
        public void OnOrderCanceled(in OrderCanceledEvent e) { }
        public void OnOrderFilled(in OrderFilledEvent e) { }
        public void OnTrade(in TradeEvent e) { }
        public void OnReject(in RejectEvent e) { }
    }

    [Fact]
    public void ReentrantCallFromSink_Throws()
    {
        var sink = new ReentrantSink();
        var eng = new MatchingEngine(new[] { Petr4 }, sink);
        sink.Engine = eng;
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        Assert.NotNull(sink.Captured);
        Assert.IsType<InvalidOperationException>(sink.Captured);
    }
}

public class InstrumentRulesTests
{
    [Fact]
    public void TickSize_NotIntegerScaled_Throws()
    {
        var bad = new Instrument
        {
            Symbol = "X", SecurityId = 1, TickSize = 0.000001m, LotSize = 1,
            MinPrice = 1m, MaxPrice = 2m, Currency = "BRL", Isin = "X", SecurityType = "EQUITY",
        };
        Assert.Throws<ArgumentException>((Action)(() => { _ = new InstrumentTradingRules(bad); }));
    }

    [Fact]
    public void MinPrice_NotMultipleOfTick_Throws()
    {
        var bad = new Instrument
        {
            Symbol = "X", SecurityId = 1, TickSize = 0.05m, LotSize = 1,
            MinPrice = 0.07m, MaxPrice = 1m, Currency = "BRL", Isin = "X", SecurityType = "EQUITY",
        };
        Assert.Throws<ArgumentException>((Action)(() => { _ = new InstrumentTradingRules(bad); }));
    }
}
