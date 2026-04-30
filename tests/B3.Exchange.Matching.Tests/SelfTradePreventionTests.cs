namespace B3.Exchange.Matching.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using static TestFactory;

public class SelfTradePreventionTests
{
    private const uint FirmA = 11;
    private const uint FirmB = 22;

    private static MatchingEngine NewEngine(SelfTradePrevention stp, out RecordingSink sink)
    {
        sink = new RecordingSink();
        return new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance, stp);
    }

    [Fact]
    public void Default_None_AllowsSelfTrade()
    {
        var eng = NewEngine(SelfTradePrevention.None, out var sink);
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 1000));
        sink.Clear();
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 2000));

        var trade = Assert.Single(sink.Trades);
        Assert.Equal(FirmA, trade.AggressorFirm);
        Assert.Equal(FirmA, trade.RestingFirm);
        Assert.Single(sink.Filled);
        Assert.Empty(sink.Rejects);
        Assert.Empty(sink.Canceled);
    }

    // ---------- cancel-aggressor ----------

    [Fact]
    public void CancelAggressor_FullSelfTrade_NoTradeAggressorRejected()
    {
        var eng = NewEngine(SelfTradePrevention.CancelAggressor, out var sink);
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 1000));
        sink.Clear();
        uint baseRpt = eng.CurrentRptSeq;
        long expectedAggressorOrderId = eng.PeekNextOrderId;

        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 2000));

        Assert.Empty(sink.Trades);
        Assert.Empty(sink.Filled);
        Assert.Empty(sink.QtyReduced);
        Assert.Empty(sink.Canceled);          // resting was NOT canceled
        Assert.Empty(sink.Accepted);          // aggressor never rested
        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.SelfTradePrevention, rej.Reason);
        Assert.Equal(expectedAggressorOrderId, rej.OrderIdOrZero);
        Assert.Equal(1, eng.OrderCount(PetrSecId));   // s1 still resting, untouched
        Assert.Equal(baseRpt, eng.CurrentRptSeq);     // Reject does NOT bump RptSeq
    }

    [Fact]
    public void CancelAggressor_PartialFillAgainstOtherFirm_ThenSelfTradeStops()
    {
        var eng = NewEngine(SelfTradePrevention.CancelAggressor, out var sink);
        // Best ask = FirmB at 10.00 (100). Then FirmA at 10.00 (200) behind.
        eng.Submit(new NewOrderCommand("sB", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmB, 1000));
        eng.Submit(new NewOrderCommand("sA", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 200, FirmA, 1100));
        sink.Clear();

        // FirmA buys 300 — fills 100 vs FirmB, then hits FirmA at same level → cancel-aggressor.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 300, FirmA, 2000));

        var trade = Assert.Single(sink.Trades);
        Assert.Equal(FirmB, trade.RestingFirm);
        Assert.Equal(100, trade.Quantity);
        Assert.Single(sink.Filled);                       // sB fully filled
        var rej = Assert.Single(sink.Rejects);            // residual canceled
        Assert.Equal(RejectReason.SelfTradePrevention, rej.Reason);
        Assert.Empty(sink.Canceled);                      // sA untouched
        Assert.Empty(sink.Accepted);                      // aggressor never rests
        Assert.Equal(1, eng.OrderCount(PetrSecId));       // sA still there
    }

    // ---------- cancel-resting ----------

    [Fact]
    public void CancelResting_RemovesConflict_ThenContinuesMatching()
    {
        var eng = NewEngine(SelfTradePrevention.CancelResting, out var sink);
        // Best ask: FirmA 100 @10.00 (will be canceled), then FirmB 100 @10.00.
        eng.Submit(new NewOrderCommand("sA", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 1000));
        eng.Submit(new NewOrderCommand("sB", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmB, 1100));
        sink.Clear();

        // FirmA buys 100 — should cancel sA, then trade against sB.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 2000));

        var cancel = Assert.Single(sink.Canceled);
        Assert.Equal(CancelReason.SelfTradePrevention, cancel.Reason);
        Assert.Equal(100, cancel.RemainingQuantityAtCancel);
        var trade = Assert.Single(sink.Trades);
        Assert.Equal(FirmB, trade.RestingFirm);
        Assert.Equal(100, trade.Quantity);
        Assert.Single(sink.Filled);
        Assert.Empty(sink.Rejects);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void CancelResting_MultipleSelfMakers_AllCanceled_ThenRest()
    {
        var eng = NewEngine(SelfTradePrevention.CancelResting, out var sink);
        // Two FirmA makers, then a FirmA maker behind a FirmB maker is unrealistic;
        // here all asks at 10.00 are FirmA. Buy from FirmA at 10.00 should cancel both,
        // then since aggressor is Day with remainder, it rests on the bid side.
        eng.Submit(new NewOrderCommand("sA1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 1000));
        eng.Submit(new NewOrderCommand("sA2", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 1100));
        sink.Clear();

        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 200, FirmA, 2000));

        Assert.Equal(2, sink.Canceled.Count);
        Assert.All(sink.Canceled, c => Assert.Equal(CancelReason.SelfTradePrevention, c.Reason));
        Assert.Empty(sink.Trades);
        Assert.Empty(sink.Filled);
        var acc = Assert.Single(sink.Accepted); // aggressor rests
        Assert.Equal(200, acc.RemainingQuantity);
        Assert.Equal(Side.Buy, acc.Side);
        Assert.Equal(1, eng.OrderCount(PetrSecId)); // only the new bid resting
    }

    [Fact]
    public void CancelResting_MultiLevel_MixedFirms()
    {
        var eng = NewEngine(SelfTradePrevention.CancelResting, out var sink);
        // Asks: 10.00 → FirmA 100 (cancel), 10.01 → FirmB 100 (trade), 10.02 → FirmA 100 (cancel).
        eng.Submit(new NewOrderCommand("sA1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.00m), 100, FirmA, 1000));
        eng.Submit(new NewOrderCommand("sB", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.01m), 100, FirmB, 1100));
        eng.Submit(new NewOrderCommand("sA2", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.02m), 100, FirmA, 1200));
        sink.Clear();

        // FirmA buy 300 @10.05 → walks all three levels.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10.05m), 300, FirmA, 2000));

        Assert.Equal(2, sink.Canceled.Count);
        Assert.All(sink.Canceled, c => Assert.Equal(CancelReason.SelfTradePrevention, c.Reason));
        var trade = Assert.Single(sink.Trades);
        Assert.Equal(Px(10.01m), trade.PriceMantissa);
        Assert.Equal(FirmB, trade.RestingFirm);
        Assert.Single(sink.Filled);
        // aggressor still has 200 unfilled → rests as a bid
        var acc = Assert.Single(sink.Accepted);
        Assert.Equal(200, acc.RemainingQuantity);
        Assert.Equal(Side.Buy, acc.Side);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
    }

    // ---------- cancel-both ----------

    [Fact]
    public void CancelBoth_FullSelfTrade_BothCanceled()
    {
        var eng = NewEngine(SelfTradePrevention.CancelBoth, out var sink);
        eng.Submit(new NewOrderCommand("sA", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 1000));
        sink.Clear();
        long expectedAggressorOrderId = eng.PeekNextOrderId;

        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 2000));

        var cancel = Assert.Single(sink.Canceled);
        Assert.Equal(CancelReason.SelfTradePrevention, cancel.Reason);
        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.SelfTradePrevention, rej.Reason);
        Assert.Equal(expectedAggressorOrderId, rej.OrderIdOrZero);
        Assert.Empty(sink.Trades);
        Assert.Empty(sink.Filled);
        Assert.Empty(sink.Accepted);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void CancelBoth_PartialFillAgainstOtherFirm_ThenBothCanceled()
    {
        var eng = NewEngine(SelfTradePrevention.CancelBoth, out var sink);
        eng.Submit(new NewOrderCommand("sB", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmB, 1000));
        eng.Submit(new NewOrderCommand("sA", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 200, FirmA, 1100));
        sink.Clear();

        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 300, FirmA, 2000));

        var trade = Assert.Single(sink.Trades);
        Assert.Equal(FirmB, trade.RestingFirm);
        Assert.Single(sink.Filled);                                    // sB filled
        var cancel = Assert.Single(sink.Canceled);                     // sA canceled
        Assert.Equal(CancelReason.SelfTradePrevention, cancel.Reason);
        var rej = Assert.Single(sink.Rejects);                         // aggressor residual
        Assert.Equal(RejectReason.SelfTradePrevention, rej.Reason);
        Assert.Empty(sink.Accepted);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    // ---------- bookkeeping invariants ----------

    [Fact]
    public void CancelResting_RptSeqAndIdsRemainMonotonic()
    {
        var eng = NewEngine(SelfTradePrevention.CancelResting, out var sink);
        eng.Submit(new NewOrderCommand("sA", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 1000));
        eng.Submit(new NewOrderCommand("sB", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmB, 1100));
        // Two Accepted events so far → RptSeq=2, no trades emitted.
        Assert.Equal(2u, eng.CurrentRptSeq);
        sink.Clear();

        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, FirmA, 2000));

        // Expect: Canceled(sA, rpt=3), Trade(rpt=4), Filled(sB, rpt=5).
        Assert.Equal(3u, sink.Canceled[0].RptSeq);
        Assert.Equal(4u, sink.Trades[0].RptSeq);
        Assert.Equal(5u, sink.Filled[0].RptSeq);
        Assert.Equal(5u, eng.CurrentRptSeq);
        Assert.Equal(1u, sink.Trades[0].TradeId);
    }
}
