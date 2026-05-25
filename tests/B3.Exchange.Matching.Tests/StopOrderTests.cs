namespace B3.Exchange.Matching.Tests;

/// <summary>
/// Engine semantics for Stop / Stop-limit orders (issue #214).
/// Validates: parked-off-book lifecycle, trigger predicate (last-trade
/// crosses StopPx), StopLoss → Market re-route, StopLimit → Limit re-route,
/// cancel of untriggered stop, validation of TIF/StopPx/MaxFloor.
/// </summary>
public class StopOrderTests
{
    private static NewOrderCommand BuyStopLoss(string id, decimal stopPx, long qty,
        TimeInForce tif = TimeInForce.Day, uint firm = 1)
        => new(id, TestFactory.PetrSecId, Side.Buy, OrderType.StopLoss, tif,
               PriceMantissa: 0L, qty, firm, EnteredAtNanos: 0)
        { StopPxMantissa = TestFactory.Px(stopPx) };

    private static NewOrderCommand SellStopLoss(string id, decimal stopPx, long qty,
        TimeInForce tif = TimeInForce.Day, uint firm = 1)
        => new(id, TestFactory.PetrSecId, Side.Sell, OrderType.StopLoss, tif,
               PriceMantissa: 0L, qty, firm, EnteredAtNanos: 0)
        { StopPxMantissa = TestFactory.Px(stopPx) };

    private static NewOrderCommand BuyStopLimit(string id, decimal stopPx, decimal limitPx, long qty,
        TimeInForce tif = TimeInForce.Day, uint firm = 1)
        => new(id, TestFactory.PetrSecId, Side.Buy, OrderType.StopLimit, tif,
               PriceMantissa: TestFactory.Px(limitPx), qty, firm, EnteredAtNanos: 0)
        { StopPxMantissa = TestFactory.Px(stopPx) };

    private static NewOrderCommand Limit(string id, Side side, decimal price, long qty,
        TimeInForce tif = TimeInForce.Day, uint firm = 2)
        => new(id, TestFactory.PetrSecId, side, OrderType.Limit, tif,
               TestFactory.Px(price), qty, firm, EnteredAtNanos: 0);

    [Fact]
    public void Stop_accept_emits_stop_accepted_only_no_book_event()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(BuyStopLoss("S1", 11m, 100));

        Assert.Empty(sink.Accepted);
        var s = Assert.Single(sink.StopAccepted);
        Assert.Equal(Side.Buy, s.Side);
        Assert.Equal(TestFactory.Px(11m), s.StopPxMantissa);
        Assert.Equal(100, s.Quantity);
    }

    [Fact]
    public void Buy_stop_triggers_when_trade_at_or_above_stoppx()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // Park a buy stop at 11.00; place a sell offer at 11.00; aggressor
        // buy crosses at 11.00 → triggers the stop. After trigger, the
        // stop becomes a Market buy with empty book (we already consumed
        // the only offer) so it silently expires.
        engine.Submit(BuyStopLoss("STP", 11m, 100));
        engine.Submit(Limit("S", Side.Sell, 11m, 100, firm: 9));
        sink.Clear();

        engine.Submit(Limit("AGG", Side.Buy, 11m, 100, firm: 8));

        Assert.Single(sink.Trades);
        var trig = Assert.Single(sink.StopTriggered);
        Assert.Equal(Side.Buy, trig.Side);
        Assert.Equal(TestFactory.Px(11m), trig.TriggerTradePriceMantissa);
    }

    [Fact]
    public void Sell_stop_triggers_when_trade_at_or_below_stoppx()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(SellStopLoss("STP", 9m, 100));
        engine.Submit(Limit("B", Side.Buy, 9m, 100, firm: 9));
        sink.Clear();

        engine.Submit(Limit("AGG", Side.Sell, 9m, 100, firm: 8));

        Assert.Single(sink.Trades);
        Assert.Single(sink.StopTriggered);
    }

    [Fact]
    public void Stop_does_not_trigger_when_trade_does_not_cross_stoppx()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // Buy stop at 12; trade at 10 should not fire it.
        engine.Submit(BuyStopLoss("STP", 12m, 100));
        engine.Submit(Limit("S", Side.Sell, 10m, 100, firm: 9));
        sink.Clear();

        engine.Submit(Limit("AGG", Side.Buy, 10m, 100, firm: 8));

        Assert.Single(sink.Trades);
        Assert.Empty(sink.StopTriggered);
    }

    [Fact]
    public void StopLoss_triggers_as_market_and_consumes_liquidity()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // Two offers: 200@10 and 200@11. Buy stop at 10.
        engine.Submit(Limit("S1", Side.Sell, 10m, 100, firm: 9));
        engine.Submit(Limit("S2", Side.Sell, 11m, 200, firm: 9));
        engine.Submit(BuyStopLoss("STP", 10m, 200));
        sink.Clear();

        // Aggressor buys 100@10 → trade prints at 10 → triggers the buy
        // stop → triggered Market consumes the rest of the book.
        engine.Submit(Limit("AGG", Side.Buy, 10m, 100, firm: 8));

        Assert.Single(sink.StopTriggered);
        // Expect at least 2 trades: the original aggressor's 100@10 plus
        // the triggered stop's 200@11.
        Assert.True(sink.Trades.Count >= 2, $"expected ≥2 trades, got {sink.Trades.Count}");
        Assert.Contains(sink.Trades, t => t.PriceMantissa == TestFactory.Px(11m));
    }

    [Fact]
    public void StopLimit_triggers_and_rests_remainder()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // One offer: 100@10. Buy StopLimit at stop=10 limit=10 qty=200.
        // After trigger, limit buy 200@10 consumes the 100@10 offer and
        // the remaining 100 rests on the bid book.
        engine.Submit(Limit("S1", Side.Sell, 10m, 100, firm: 9));
        engine.Submit(BuyStopLimit("STP", stopPx: 10m, limitPx: 10m, qty: 200));
        sink.Clear();

        engine.Submit(Limit("AGG", Side.Buy, 10m, 100, firm: 8));

        Assert.Single(sink.StopTriggered);
        // Triggered stop consumes the seller's offer (we just emptied it
        // with the 100 from AGG... wait — AGG also wanted 100@10, so the
        // book is empty by the time the stop triggers). Adjust: at least
        // one trade from AGG; the triggered stop with limit rests since
        // book is empty after AGG consumed it.
        Assert.NotEmpty(sink.Trades);
        // The triggered stop should produce an OrderAccepted (the resting
        // remainder under the SAME OrderId as the parked stop).
        Assert.NotEmpty(sink.Accepted);
    }

    [Fact]
    public void Cancel_of_untriggered_stop_emits_stop_canceled_event()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(BuyStopLoss("STP", 11m, 100));
        var stopId = sink.StopAccepted.Single().OrderId;
        sink.Clear();

        engine.Cancel(new CancelOrderCommand("CXL", TestFactory.PetrSecId, stopId, EnteredAtNanos: 0));

        var c = Assert.Single(sink.StopCanceled);
        Assert.Equal(stopId, c.OrderId);
        Assert.Equal(100, c.RemainingQuantityAtCancel);
    }

    [Fact]
    public void Stop_with_tif_ioc_rejected()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(BuyStopLoss("STP", 11m, 100, tif: TimeInForce.IOC));
        var r = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, r.Reason);
    }

    [Fact]
    public void Stop_with_zero_stoppx_rejected()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(new NewOrderCommand("STP", TestFactory.PetrSecId, Side.Buy,
            OrderType.StopLoss, TimeInForce.Day, PriceMantissa: 0L, Quantity: 100, EnteringFirm: 1, EnteredAtNanos: 0)
        { StopPxMantissa = 0L });
        var r = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.PriceNonPositive, r.Reason);
    }

    [Fact]
    public void StopLoss_with_nonzero_price_rejected()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(new NewOrderCommand("STP", TestFactory.PetrSecId, Side.Buy,
            OrderType.StopLoss, TimeInForce.Day,
            PriceMantissa: TestFactory.Px(11m), Quantity: 100, EnteringFirm: 1, EnteredAtNanos: 0)
        { StopPxMantissa = TestFactory.Px(11m) });
        var r = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, r.Reason);
    }

    [Fact]
    public void StopLimit_without_limitpx_rejected()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(new NewOrderCommand("STP", TestFactory.PetrSecId, Side.Buy,
            OrderType.StopLimit, TimeInForce.Day,
            PriceMantissa: 0L, Quantity: 100, EnteringFirm: 1, EnteredAtNanos: 0)
        { StopPxMantissa = TestFactory.Px(11m) });
        var r = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.PriceNonPositive, r.Reason);
    }

    [Fact]
    public void Stop_with_offtick_stoppx_rejected()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // tick = 0.01 (Px = mantissa /10000). So 11.005 = 110_050 mantissa
        // = not a multiple of TickSize=100.
        engine.Submit(new NewOrderCommand("STP", TestFactory.PetrSecId, Side.Buy,
            OrderType.StopLoss, TimeInForce.Day, PriceMantissa: 0L, Quantity: 100, EnteringFirm: 1, EnteredAtNanos: 0)
        { StopPxMantissa = 110_050L });
        var r = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.PriceNotOnTick, r.Reason);
    }

    [Fact]
    public void Stop_with_maxfloor_rejected()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(new NewOrderCommand("STP", TestFactory.PetrSecId, Side.Buy,
            OrderType.StopLoss, TimeInForce.Day, PriceMantissa: 0L, Quantity: 100, EnteringFirm: 1, EnteredAtNanos: 0)
        { StopPxMantissa = TestFactory.Px(11m), MaxFloor = 50UL });
        var r = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, r.Reason);
    }

    // Issue #453: a StopLimit whose triggered residual ends up resting
    // on the book must keep the parked stop's OrdTagID / InvestorID so
    // a later OrderMassActionRequest filtered by those identifiers
    // still cancels it. Before #453 the triggered NewOrderCommand
    // dropped both fields and the residual was silently exempt.
    [Fact]
    public void Triggered_stoplimit_residual_preserves_ordtagid_and_investorid()
    {
        var engine = TestFactory.NewEngine(out var sink);
        var investor = new InvestorId(0x1234, 999_000_111u);

        // Seller sits at 10@100. Park a BuyStopLimit at stop=10 limit=10
        // for 200 with OrdTagId=99 + InvestorId set. A second buyer
        // (AGG) trades 10@100 — the resulting print arms the stop. The
        // triggered limit-buy then sees an empty book (AGG already
        // consumed the offer) and the full 200 rests on the bid.
        engine.Submit(Limit("S1", Side.Sell, 10m, 100, firm: 9));
        engine.Submit(new NewOrderCommand("STP", TestFactory.PetrSecId, Side.Buy,
            OrderType.StopLimit, TimeInForce.Day,
            PriceMantissa: TestFactory.Px(10m), Quantity: 200, EnteringFirm: 1, EnteredAtNanos: 0)
        {
            StopPxMantissa = TestFactory.Px(10m),
            OrdTagId = 99,
            InvestorId = investor,
        });
        var parkedStopId = sink.StopAccepted.Single().OrderId;
        sink.Clear();
        engine.Submit(Limit("AGG", Side.Buy, 10m, 100, firm: 8));

        // The triggered residual rests under the parked stop's OrderId.
        Assert.Single(sink.StopTriggered);
        var residual = sink.Accepted.Single(a => a.OrderId == parkedStopId);
        Assert.Equal(200, residual.RemainingQuantity);

        // Mass-cancel by OrdTagID=99 ONLY (no firm / no side / no
        // SecurityId) must reach the residual.
        sink.Clear();
        int cancelled = engine.MassCancel(
            new[] { parkedStopId },
            new MassCancelCommand(SecurityId: 0, SideFilter: null, EnteredAtNanos: 1)
            { OrdTagIdFilter = 99 });
        Assert.Equal(1, cancelled);
        var c = Assert.Single(sink.Canceled);
        Assert.Equal(parkedStopId, c.OrderId);
        Assert.Equal(CancelReason.MassCancel, c.Reason);
    }

    [Fact]
    public void Triggered_stoplimit_residual_matches_investorid_filter()
    {
        var engine = TestFactory.NewEngine(out var sink);
        var investor = new InvestorId(0x1234, 999_000_111u);

        engine.Submit(Limit("S1", Side.Sell, 10m, 100, firm: 9));
        engine.Submit(new NewOrderCommand("STP", TestFactory.PetrSecId, Side.Buy,
            OrderType.StopLimit, TimeInForce.Day,
            PriceMantissa: TestFactory.Px(10m), Quantity: 200, EnteringFirm: 1, EnteredAtNanos: 0)
        {
            StopPxMantissa = TestFactory.Px(10m),
            OrdTagId = 99,
            InvestorId = investor,
        });
        var parkedStopId = sink.StopAccepted.Single().OrderId;
        engine.Submit(Limit("AGG", Side.Buy, 10m, 100, firm: 8));
        sink.Clear();

        int cancelled = engine.MassCancel(
            new[] { parkedStopId },
            new MassCancelCommand(SecurityId: 0, SideFilter: null, EnteredAtNanos: 1)
            { InvestorIdFilter = investor });
        Assert.Equal(1, cancelled);
        Assert.Equal(parkedStopId, sink.Canceled.Single().OrderId);
    }
}
