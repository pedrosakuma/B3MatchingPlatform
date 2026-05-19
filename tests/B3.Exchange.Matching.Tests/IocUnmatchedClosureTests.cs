namespace B3.Exchange.Matching.Tests;

/// <summary>
/// Issue #357: a new Limit IOC / Market IOC / FOK aggressor that finds
/// zero crossing liquidity on submission must emit a terminal
/// <see cref="OrderCanceledEvent"/> with reason
/// <see cref="CancelReason.IocUnmatched"/> so the originating session
/// receives an <c>ExecutionReport_Cancel</c> instead of hanging
/// indefinitely. The signal must NOT fire on the Replace path or on a
/// stop-triggered execution (both of those preserve the prior
/// silent-drop semantics).
/// </summary>
public class IocUnmatchedClosureTests
{
    private static NewOrderCommand Limit(string id, Side side, decimal price, long qty,
        TimeInForce tif = TimeInForce.IOC, uint firm = 1)
        => new(id, TestFactory.PetrSecId, side, OrderType.Limit, tif,
               TestFactory.Px(price), qty, firm, 0);

    private static NewOrderCommand Market(string id, Side side, long qty,
        TimeInForce tif = TimeInForce.IOC, uint firm = 1)
        => new(id, TestFactory.PetrSecId, side, OrderType.Market, tif,
               0, qty, firm, 0);

    [Fact]
    public void Limit_IOC_with_empty_opposite_emits_iocUnmatched_cancel()
    {
        var engine = TestFactory.NewEngine(out var sink);

        engine.Submit(Limit("A1", Side.Sell, 32m, 100, tif: TimeInForce.IOC));

        Assert.Empty(sink.Accepted);
        Assert.Empty(sink.Trades);
        Assert.Empty(sink.Rejects);
        var cancel = Assert.Single(sink.Canceled);
        Assert.Equal(CancelReason.IocUnmatched, cancel.Reason);
        Assert.Equal(Side.Sell, cancel.Side);
        Assert.Equal(100, cancel.RemainingQuantityAtCancel);
        Assert.Equal(TestFactory.Px(32m), cancel.PriceMantissa);
    }

    [Fact]
    public void Limit_IOC_non_crossing_against_resting_emits_iocUnmatched_cancel()
    {
        var engine = TestFactory.NewEngine(out var sink);
        // Resting bid @ 31 — does not cross an IOC sell @ 32.
        engine.Submit(new NewOrderCommand("M1", TestFactory.PetrSecId, Side.Buy,
            OrderType.Limit, TimeInForce.Day, TestFactory.Px(31m), 100, EnteringFirm: 8, EnteredAtNanos: 0));
        sink.Clear();

        engine.Submit(Limit("A1", Side.Sell, 32m, 100, tif: TimeInForce.IOC));

        Assert.Empty(sink.Trades);
        Assert.Empty(sink.Accepted);
        var cancel = Assert.Single(sink.Canceled);
        Assert.Equal(CancelReason.IocUnmatched, cancel.Reason);
    }

    [Fact]
    public void Limit_IOC_with_partial_fill_does_not_emit_iocUnmatched_cancel()
    {
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(new NewOrderCommand("M1", TestFactory.PetrSecId, Side.Buy,
            OrderType.Limit, TimeInForce.Day, TestFactory.Px(32m), 400, EnteringFirm: 8, EnteredAtNanos: 0));
        sink.Clear();

        // IOC sell of 1000 @ 32 takes 400 from the resting bid; remaining 600
        // is silently dropped without an iocUnmatched cancel (the trade
        // event already terminates the aggressor for this issue's scope).
        engine.Submit(Limit("A1", Side.Sell, 32m, 1000, tif: TimeInForce.IOC));

        Assert.Single(sink.Trades);
        Assert.DoesNotContain(sink.Canceled, c => c.Reason == CancelReason.IocUnmatched);
    }

    [Fact]
    public void Market_IOC_with_empty_opposite_is_preRejected_not_iocUnmatched()
    {
        // Pre-existing behavior (MarketNoLiquidity reject) takes precedence
        // over the new IocUnmatched closure for Market orders against empty.
        var engine = TestFactory.NewEngine(out var sink);

        engine.Submit(Market("A1", Side.Sell, 100, tif: TimeInForce.IOC));

        var reject = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MarketNoLiquidity, reject.Reason);
        Assert.Empty(sink.Canceled);
    }

    [Fact]
    public void FOK_with_empty_opposite_is_preRejected_not_iocUnmatched()
    {
        // FOK with no crossing liquidity is rejected upfront with
        // FokUnfillable — preserves existing behavior.
        var engine = TestFactory.NewEngine(out var sink);

        engine.Submit(Limit("A1", Side.Sell, 32m, 100, tif: TimeInForce.FOK));

        var reject = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.FokUnfillable, reject.Reason);
        Assert.Empty(sink.Canceled);
    }

    [Fact]
    public void Replace_to_IOC_with_empty_counter_side_does_not_emit_iocUnmatched()
    {
        // Issue #357 must NOT change the Replace-to-IOC silent-drop semantics
        // covered by ReplaceTypeAndTifTests — the original order's DEL is the
        // closure signal there; the re-entered IOC remainder stays silent.
        var engine = TestFactory.NewEngine(out var sink);
        engine.Submit(new NewOrderCommand("M1", TestFactory.PetrSecId, Side.Buy,
            OrderType.Limit, TimeInForce.Day, TestFactory.Px(10m), 100, EnteringFirm: 1, EnteredAtNanos: 0));
        var original = sink.Accepted.Single();
        sink.Clear();

        // Replace to a worse price with TIF=IOC against empty counter side.
        engine.Replace(new ReplaceOrderCommand("R1", TestFactory.PetrSecId,
            original.OrderId, TestFactory.Px(11m), 100, 0)
        { NewTif = TimeInForce.IOC });

        // Original is canceled via ReplaceLostPriority; the re-entered IOC
        // finds no crossing liquidity and is silently dropped — no
        // IocUnmatched cancel from the Replace path.
        Assert.DoesNotContain(sink.Canceled, c => c.Reason == CancelReason.IocUnmatched);
    }

    [Fact]
    public void Stp_cancelAggressor_does_not_double_emit_iocUnmatched()
    {
        // STP=CancelAggressor pre-cancels the IOC aggressor mid-walk before
        // any trades print. The silent-drop branch is reached with
        // stpAggressorCanceled=true and anyTrade=false; we must NOT emit a
        // second cancel event (the STP path is already a terminal signal).
        var sink = new RecordingSink();
        var engine = new MatchingEngine(new[] { TestFactory.Petr4 }, sink,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchingEngine>.Instance,
            SelfTradePrevention.CancelAggressor);
        engine.Submit(new NewOrderCommand("M1", TestFactory.PetrSecId, Side.Buy,
            OrderType.Limit, TimeInForce.Day, TestFactory.Px(32m), 100, EnteringFirm: 7, EnteredAtNanos: 0));
        sink.Clear();

        engine.Submit(Limit("A1", Side.Sell, 32m, 100, tif: TimeInForce.IOC, firm: 7));

        Assert.Empty(sink.Trades);
        Assert.DoesNotContain(sink.Canceled, c => c.Reason == CancelReason.IocUnmatched);
    }
}
