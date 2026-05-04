namespace B3.Exchange.Matching.Tests;

/// <summary>
/// Engine semantics for #204 — Replace honors TIF/OrdType per the
/// EntryPoint OrderCancelReplaceRequest schema. Validates the
/// priority-loss re-entry path uses the explicit overrides (or
/// preserves the resting order's originals when the wire field is null).
/// </summary>
public class ReplaceTypeAndTifTests
{
    private static NewOrderCommand Limit(string id, Side side, decimal price, long qty,
        TimeInForce tif = TimeInForce.Day, uint firm = 1)
        => new(id, TestFactory.PetrSecId, side, OrderType.Limit, tif,
               TestFactory.Px(price), qty, firm, 0);

    [Fact]
    public void Replace_with_no_overrides_preserves_original_tif_day()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Buy, 10m, 100));
        var original = sink.Accepted.Single();
        sink.Clear();

        // Same price, smaller qty: priority preserved (no DEL+NEW).
        engine.Replace(new ReplaceOrderCommand("R1", TestFactory.PetrSecId,
            original.OrderId, TestFactory.Px(10m), 100, 0));

        Assert.Empty(sink.Canceled);
        Assert.Single(sink.QtyReduced);
    }

    [Fact]
    public void Replace_to_ioc_drops_residual_after_partial_fill()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // Resting buy at 10
        engine.Submit(Limit("M1", Side.Buy, 10m, 500));
        var original = sink.Accepted.Single();
        // Counter-side liquidity at 11 (so price-up replace crosses)
        engine.Submit(Limit("M2", Side.Sell, 11m, 100));
        sink.Clear();

        // Replace to price 11 with TIF=IOC: the original order is cancelled
        // (priority lost) and the replacement crosses against M2 for 100,
        // dropping the IOC remainder instead of resting.
        engine.Replace(new ReplaceOrderCommand("R1", TestFactory.PetrSecId,
            original.OrderId, TestFactory.Px(11m), 500, 0)
        { NewTif = TimeInForce.IOC });

        var cancel = Assert.Single(sink.Canceled);
        Assert.Equal(CancelReason.ReplaceLostPriority, cancel.Reason);
        Assert.Single(sink.Trades);
        // No new accept: IOC remainder didn't rest.
        Assert.Empty(sink.Accepted);
    }

    [Fact]
    public void Replace_to_market_ioc_consumes_book_and_does_not_rest()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Buy, 10m, 500));
        var original = sink.Accepted.Single();
        engine.Submit(Limit("M2", Side.Sell, 11m, 100));
        engine.Submit(Limit("M3", Side.Sell, 12m, 100));
        sink.Clear();

        engine.Replace(new ReplaceOrderCommand("R1", TestFactory.PetrSecId,
            original.OrderId, 0, 200, 0)
        { NewOrdType = OrderType.Market, NewTif = TimeInForce.IOC });

        Assert.Single(sink.Canceled);
        Assert.Equal(2, sink.Trades.Count);
        Assert.Empty(sink.Accepted);
    }

    [Fact]
    public void Replace_to_market_with_day_tif_rejects()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Buy, 10m, 100));
        var original = sink.Accepted.Single();
        sink.Clear();

        engine.Replace(new ReplaceOrderCommand("R1", TestFactory.PetrSecId,
            original.OrderId, 0, 100, 0)
        { NewOrdType = OrderType.Market, NewTif = TimeInForce.Day });

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MarketNotImmediateOrCancel, rej.Reason);
        Assert.Empty(sink.Canceled);
    }

    [Fact]
    public void Replace_with_tif_change_loses_priority_even_at_same_price_and_qty()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Buy, 10m, 100, tif: TimeInForce.Day));
        var original = sink.Accepted.Single();
        sink.Clear();

        // Counter-side empty so IOC replace will be cancelled with no fills,
        // proving the order is gone (priority lost branch was taken).
        engine.Replace(new ReplaceOrderCommand("R1", TestFactory.PetrSecId,
            original.OrderId, TestFactory.Px(10m), 100, 0)
        { NewTif = TimeInForce.IOC });

        var cancel = Assert.Single(sink.Canceled);
        Assert.Equal(CancelReason.ReplaceLostPriority, cancel.Reason);
        // IOC remainder is silently dropped — no new accept.
        Assert.Empty(sink.Accepted);
        Assert.Empty(sink.Trades);
    }

    [Fact]
    public void Replace_during_close_phase_rejects_market_closed()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Buy, 10m, 100));
        var original = sink.Accepted.Single();
        engine.SetTradingPhase(TestFactory.PetrSecId, TradingPhase.Close, 0);
        sink.Clear();

        engine.Replace(new ReplaceOrderCommand("R1", TestFactory.PetrSecId,
            original.OrderId, TestFactory.Px(11m), 100, 0));

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MarketClosed, rej.Reason);
    }

    [Fact]
    public void Replace_with_omitted_tif_preserves_gtc()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(Limit("M1", Side.Buy, 10m, 100, tif: TimeInForce.Gtc));
        var original = sink.Accepted.Single();
        sink.Clear();

        // Different price → priority lost path. NewTif null preserves GTC,
        // so the replacement rests like a GTC order (residual stays on book).
        engine.Replace(new ReplaceOrderCommand("R1", TestFactory.PetrSecId,
            original.OrderId, TestFactory.Px(11m), 100, 0));

        Assert.Single(sink.Canceled);
        Assert.Single(sink.Accepted);
    }
}
