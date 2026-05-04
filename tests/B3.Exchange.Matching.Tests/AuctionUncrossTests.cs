namespace B3.Exchange.Matching.Tests;

using static TestFactory;

/// <summary>
/// Issue #230 (Onda M · M3): operator-issued auction uncross drains
/// crossing volume at a single Theoretical Opening Price and
/// transitions the trading phase. Iceberg semantics from K6b carry
/// over (replenished slice goes to the back of the level — no
/// double-trade in the same uncross).
/// </summary>
public class AuctionUncrossTests
{
    [Fact]
    public void Uncross_PerfectMatch_OneTradeAtTop_TransitionsToOpen()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, 6001));
        sink.Clear();

        bool any = eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        Assert.True(any);
        var trade = Assert.Single(sink.Trades);
        Assert.Equal(100, trade.Quantity);
        Assert.Equal(7000UL, trade.TransactTimeNanos);
        Assert.Equal(2, sink.Filled.Count);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
        Assert.Equal(TradingPhase.Open, eng.GetTradingPhase(PetrSecId));
        Assert.Equal(TradingPhase.Open, sink.PhaseChanges[^1].Phase);
    }

    [Fact]
    public void Uncross_OverflowOnBuySide_LeavesResidualResting()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 300, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, 6001));
        sink.Clear();

        eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        var trade = Assert.Single(sink.Trades);
        Assert.Equal(100, trade.Quantity);
        // Buy maker partially filled → OrderQuantityReduced; sell fully filled.
        Assert.Single(sink.Filled);
        Assert.Single(sink.QtyReduced);
        Assert.Equal(200, sink.QtyReduced[0].NewRemainingQuantity);
        // M5 (#232): GoodForAuction residual that did not fully fill at
        // the uncross is expired before the phase transitions to Open.
        Assert.Equal(0, eng.OrderCount(PetrSecId));
        var expired = Assert.Single(sink.Canceled);
        Assert.Equal(CancelReason.AuctionExpired, expired.Reason);
    }

    [Fact]
    public void Uncross_MultipleMakers_BatchedTrades_AllAtTopWithSameTxnTime()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        // Three buy makers @ 10.05 (each 100), three sell makers @ 10.00 (each 100).
        for (int i = 0; i < 3; i++)
            eng.Submit(new NewOrderCommand("b" + i, PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 100, 11, (ulong)(6000 + i)));
        for (int i = 0; i < 3; i++)
            eng.Submit(new NewOrderCommand("s" + i, PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, (ulong)(6100 + i)));
        sink.Clear();

        eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        Assert.Equal(3, sink.Trades.Count);
        // All trades print at the SINGLE TOP price and share txnNanos.
        // matched@10.00 = matched@10.05 = 300, imbalance=0 at both →
        // lowest-price tiebreak picks 10.00.
        Assert.All(sink.Trades, t => Assert.Equal(Px(10.00m), t.PriceMantissa));
        Assert.All(sink.Trades, t => Assert.Equal(7000UL, t.TransactTimeNanos));
        // Trade IDs are monotonic.
        for (int i = 1; i < sink.Trades.Count; i++)
            Assert.True(sink.Trades[i].TradeId > sink.Trades[i - 1].TradeId);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void Uncross_NoCrossing_NoTrades_StillTransitionsPhase()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        // Only buys — no crossing.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 11, 6000));
        sink.Clear();

        bool any = eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        Assert.False(any);
        Assert.Empty(sink.Trades);
        Assert.Equal(TradingPhase.Open, eng.GetTradingPhase(PetrSecId));
        // M5 (#232): GoodForAuction order that did not participate in
        // any uncross trade is expired on the phase transition.
        Assert.Equal(0, eng.OrderCount(PetrSecId));
        var expired = Assert.Single(sink.Canceled);
        Assert.Equal(CancelReason.AuctionExpired, expired.Reason);
    }

    [Fact]
    public void Uncross_FromFinalClosingCall_TransitionsToClose()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.FinalClosingCall, 5000);
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.AtClose, Px(10.05m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.AtClose, Px(10.00m), 100, 12, 6001));
        sink.Clear();

        eng.UncrossAuction(PetrSecId, TradingPhase.Close, 7000);

        Assert.Single(sink.Trades);
        Assert.Equal(TradingPhase.Close, eng.GetTradingPhase(PetrSecId));
    }

    [Fact]
    public void Uncross_RejectsFromInvalidPhase()
    {
        var eng = NewEngine(out _);
        // Default phase is Open.
        Assert.Throws<InvalidOperationException>(() =>
            eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000));
    }

    [Fact]
    public void Uncross_RejectsInvalidTargetPhase()
    {
        var eng = NewEngine(out _);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        // Reserved → Close is not allowed (must be Open).
        Assert.Throws<InvalidOperationException>(() =>
            eng.UncrossAuction(PetrSecId, TradingPhase.Close, 7000));
    }

    [Fact]
    public void Uncross_TopChangedEvent_FiresFinalDeleteAfterDrain()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, 6001));
        sink.Clear();

        eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        // After perfect-match drain, the recompute should fire one
        // final AuctionTopChangedEvent collapsing to HasTop=false /
        // HasImbalance=false. The phase change follows.
        var topEvents = sink.AuctionTops;
        Assert.NotEmpty(topEvents);
        var last = topEvents[^1];
        Assert.False(last.HasTop);
        Assert.False(last.HasImbalance);
    }

    [Fact]
    public void Uncross_DeterministicTieOnAggressorSide_BuyWinsOnEqualTimestamp()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        // Both orders at the same timestamp → tiebreak: buy is aggressor.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, 6000));
        sink.Clear();

        eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        var trade = Assert.Single(sink.Trades);
        Assert.Equal(Side.Buy, trade.AggressorSide);
    }

    [Fact]
    public void Uncross_AggressorSide_IsYoungerOrder()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        // Buy first (older), sell later (younger) → sell is aggressor.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, 6500));
        sink.Clear();

        eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        var trade = Assert.Single(sink.Trades);
        Assert.Equal(Side.Sell, trade.AggressorSide);
    }

    // ---------------- Onda M · M4 (issue #231) — auction prints ----------------

    /// <summary>
    /// Reserved → Open uncross that prints emits exactly one
    /// <c>AuctionPrintEvent</c> with <c>Kind=Opening</c>, the cleared
    /// price (auction TOP), and total cleared volume.
    /// </summary>
    [Fact]
    public void Uncross_OpeningWithCrossing_EmitsSingleOpeningPrint()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 200, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 200, 12, 6001));
        sink.Clear();

        eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        var print = Assert.Single(sink.AuctionPrints);
        Assert.Equal(AuctionPrintKind.Opening, print.Kind);
        Assert.Equal(PetrSecId, print.SecurityId);
        Assert.Equal(200L, print.ClearedQuantity);
        Assert.Equal(7000UL, print.TransactTimeNanos);
        // Same TOP-tiebreak rule as M3 trade-price: lowest crossing price.
        Assert.Equal(Px(10.00m), print.PriceMantissa);
    }

    /// <summary>
    /// FinalClosingCall → Close uncross that prints emits a
    /// <c>Kind=Closing</c> event with cleared volume aggregated across
    /// all trades in the uncross.
    /// </summary>
    [Fact]
    public void Uncross_ClosingWithCrossing_EmitsSingleClosingPrint_AggregatesVolume()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.FinalClosingCall, 5000);
        // Two buy makers, two sell makers — uncross drains all four.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.AtClose, Px(10.05m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("b2", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.AtClose, Px(10.05m), 200, 11, 6001));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.AtClose, Px(10.00m), 100, 12, 6002));
        eng.Submit(new NewOrderCommand("s2", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.AtClose, Px(10.00m), 200, 12, 6003));
        sink.Clear();

        eng.UncrossAuction(PetrSecId, TradingPhase.Close, 7000);

        var print = Assert.Single(sink.AuctionPrints);
        Assert.Equal(AuctionPrintKind.Closing, print.Kind);
        Assert.Equal(300L, print.ClearedQuantity);
        Assert.Equal(Px(10.00m), print.PriceMantissa);
    }

    /// <summary>
    /// Uncross with no crossing in the book emits NO auction print —
    /// "no print on no trade". Phase still transitions.
    /// </summary>
    [Fact]
    public void Uncross_NoCrossing_EmitsNoAuctionPrint()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        // Bid below ask — no crossing.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(9.95m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 100, 12, 6001));
        sink.Clear();

        bool any = eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        Assert.False(any);
        Assert.Empty(sink.AuctionPrints);
        Assert.Equal(TradingPhase.Open, eng.GetTradingPhase(PetrSecId));
    }

    // ─── Onda M · M5 (issue #232) ─────────────────────────────────────
    // Survivors of an auction call that did not match must NOT carry
    // over into the continuous phase: GoodForAuction expires after the
    // opening uncross, AtClose after the closing uncross.

    /// <summary>
    /// Multiple unfilled GoodForAuction orders all expire when Reserved
    /// → Open. Each emits an OrderCanceledEvent with reason
    /// AuctionExpired. Book is empty before phase flips.
    /// </summary>
    [Fact]
    public void Uncross_Opening_ExpiresAllUnfilledGoodForAuction()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(9.95m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("b2", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(9.90m), 200, 11, 6001));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.10m), 100, 12, 6002));
        sink.Clear();

        bool any = eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        Assert.False(any);
        Assert.Empty(sink.Trades);
        Assert.Equal(3, sink.Canceled.Count);
        Assert.All(sink.Canceled, c => Assert.Equal(CancelReason.AuctionExpired, c.Reason));
        Assert.Equal(0, eng.OrderCount(PetrSecId));
        Assert.Equal(TradingPhase.Open, eng.GetTradingPhase(PetrSecId));
    }

    /// <summary>
    /// Closing-call analogue: AtClose survivors expire on Final →
    /// Close transition. GoodForAuction left over from a previous
    /// opening would have already been cleared, so this test only
    /// exercises AtClose.
    /// </summary>
    [Fact]
    public void Uncross_Closing_ExpiresAllUnfilledAtClose()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.FinalClosingCall, 5000);
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.AtClose, Px(9.95m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.AtClose, Px(10.05m), 100, 12, 6001));
        sink.Clear();

        bool any = eng.UncrossAuction(PetrSecId, TradingPhase.Close, 7000);

        Assert.False(any);
        Assert.Empty(sink.Trades);
        Assert.Equal(2, sink.Canceled.Count);
        Assert.All(sink.Canceled, c => Assert.Equal(CancelReason.AuctionExpired, c.Reason));
        Assert.Equal(0, eng.OrderCount(PetrSecId));
        Assert.Equal(TradingPhase.Close, eng.GetTradingPhase(PetrSecId));
    }

    /// <summary>
    /// Fully-filled GoodForAuction orders must not be double-cancelled:
    /// they have already been removed from the book by the drain.
    /// Asserts no AuctionExpired event is emitted for either side.
    /// </summary>
    [Fact]
    public void Uncross_Opening_FullyFilledGoodForAuction_NotDoubleCanceled()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5000);
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.05m), 100, 11, 6000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10.00m), 100, 12, 6001));
        sink.Clear();

        bool any = eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        Assert.True(any);
        Assert.Single(sink.Trades);
        Assert.Empty(sink.Canceled);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    /// <summary>
    /// Mixed-TIF scenario: Day orders submitted in continuous Open
    /// then carried into a Reserved auction call (operator-driven phase
    /// transition) survive the uncross, while GoodForAuction siblings
    /// expire. Pins the "do not over-expire" invariant: only
    /// auction-bound TIFs are touched by the M5 sweep.
    /// </summary>
    [Fact]
    public void Uncross_Opening_DayOrders_SurviveIntoContinuous()
    {
        var eng = NewEngine(out var sink);
        // Day order submitted in Open (TIF gate accepts Day in Open).
        eng.Submit(new NewOrderCommand("d1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.95m), 100, 11, 5500));
        // Operator transitions to Reserved (intraday auction call).
        eng.SetTradingPhase(PetrSecId, TradingPhase.Reserved, 5800);
        // Auction-only sibling joins during Reserved.
        eng.Submit(new NewOrderCommand("a1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(9.90m), 100, 11, 6001));
        sink.Clear();

        eng.UncrossAuction(PetrSecId, TradingPhase.Open, 7000);

        // Only the GoodForAuction order is expired.
        var expired = Assert.Single(sink.Canceled);
        Assert.Equal(CancelReason.AuctionExpired, expired.Reason);
        // Day order survives.
        Assert.Equal(1, eng.OrderCount(PetrSecId));
    }
}
