using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Verifies that <see cref="MatchingEngine.CaptureState"/> +
/// <see cref="MatchingEngine.RestoreState"/> round-trips a working book
/// preserving price-time priority, iceberg state and counters
/// (issue #260).
/// </summary>
public class MatchingEngineRestoreTests
{
    private const long Sec = 900_000_000_001L;

    private static Instrument Petr4 => new()
    {
        Symbol = "PETR4",
        SecurityId = Sec,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000.00m,
        Currency = "BRL",
        Isin = "BRPETRACNPR6",
        SecurityType = "EQUITY",
    };

    private sealed class CountingSink : IMatchingEventSink
    {
        public List<long> AcceptedOrderIds { get; } = new();
        public List<TradeEvent> Trades { get; } = new();
        public List<OrderModifiedEvent> Modified { get; } = new();
        public void OnOrderAccepted(in OrderAcceptedEvent e) => AcceptedOrderIds.Add(e.OrderId);
        public void OnOrderQuantityReduced(in OrderQuantityReducedEvent e) { }
        public void OnOrderModified(in OrderModifiedEvent e) => Modified.Add(e);
        public void OnOrderCanceled(in OrderCanceledEvent e) { }
        public void OnOrderFilled(in OrderFilledEvent e) { }
        public void OnTrade(in TradeEvent e) => Trades.Add(e);
        public void OnReject(in RejectEvent e) { }
        public void OnOrderMassCanceled(in OrderMassCanceledEvent e) { }
        public void OnOrderBookSideEmpty(in OrderBookSideEmptyEvent e) { }
        public void OnTradingPhaseChanged(in TradingPhaseChangedEvent e) { }
        public void OnIcebergReplenished(in IcebergReplenishedEvent e) { }
        public void OnStopOrderAccepted(in StopOrderAcceptedEvent e) { }
        public void OnStopOrderTriggered(in StopOrderTriggeredEvent e) { }
        public void OnStopOrderCanceled(in StopOrderCanceledEvent e) { }
        public void OnAuctionTopChanged(in AuctionTopChangedEvent e) { }
        public void OnAuctionPrint(in AuctionPrintEvent e) { }
    }

    private static MatchingEngine NewEngine(out CountingSink sink)
    {
        sink = new CountingSink();
        return new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance);
    }

    private static long Px(decimal p) => (long)(p * 10_000m);

    [Fact]
    public void Restore_PreservesPriceTimePriority_AndCounters()
    {
        var src = NewEngine(out var srcSink);
        // Three resting buys at the same price — FIFO order matters.
        src.Submit(new NewOrderCommand("A", Sec, Side.Buy, OrderType.Limit,
            TimeInForce.Day, Px(10.00m), 100, 100, 1000UL));
        src.Submit(new NewOrderCommand("B", Sec, Side.Buy, OrderType.Limit,
            TimeInForce.Day, Px(10.00m), 100, 200, 2000UL));
        src.Submit(new NewOrderCommand("C", Sec, Side.Buy, OrderType.Limit,
            TimeInForce.Day, Px(10.00m), 100, 300, 3000UL));
        // One sell to consume one of the buys (orderId=1, ClOrdId=A)
        src.Submit(new NewOrderCommand("X", Sec, Side.Sell, OrderType.Limit,
            TimeInForce.IOC, Px(10.00m), 100, 999, 4000UL));
        Assert.Single(srcSink.Trades);
        var firstTrade = srcSink.Trades[0];

        var snap = src.CaptureState();

        // Re-create from snapshot in a fresh engine.
        var dst = NewEngine(out var dstSink);
        dst.RestoreState(snap);

        Assert.Equal(src.PeekNextOrderId, dst.PeekNextOrderId);
        Assert.Equal(src.CurrentRptSeq, dst.CurrentRptSeq);

        // Resting buys after restore should be {B, C} (A was consumed).
        var restingBuys = dst.EnumerateBook(Sec, Side.Buy).ToList();
        Assert.Equal(2, restingBuys.Count);
        Assert.Equal(2L, restingBuys[0].OrderId);  // B is first now
        Assert.Equal(3L, restingBuys[1].OrderId);

        // Submit a fresh sell that consumes them in FIFO order: B first.
        dst.Submit(new NewOrderCommand("Y", Sec, Side.Sell, OrderType.Limit,
            TimeInForce.IOC, Px(10.00m), 100, 999, 5000UL));
        Assert.Single(dstSink.Trades);
        Assert.Equal(2L, dstSink.Trades[0].RestingOrderId);  // B fills first

        // OrderId allocator continues monotonically (no re-issuance).
        // src ended with nextOrderId=5 (A,B,C,X consumed 1..4). After
        // restore Y is allocated 5 and Z is allocated 6 — Z is the last
        // accepted (Y is IOC and does not rest).
        long preZ = dst.PeekNextOrderId;
        dst.Submit(new NewOrderCommand("Z", Sec, Side.Buy, OrderType.Limit,
            TimeInForce.Day, Px(9.50m), 100, 100, 6000UL));
        var lastAccepted = dstSink.AcceptedOrderIds.Last();
        Assert.Equal(preZ, lastAccepted);
        Assert.Equal(preZ + 1, dst.PeekNextOrderId);
    }

    [Fact]
    public void Restore_RefusesOnNonEmptyEngine()
    {
        var src = NewEngine(out _);
        src.Submit(new NewOrderCommand("A", Sec, Side.Buy, OrderType.Limit,
            TimeInForce.Day, Px(10.00m), 100, 100, 1000UL));
        var snap = src.CaptureState();

        var dst = NewEngine(out _);
        dst.Submit(new NewOrderCommand("B", Sec, Side.Buy, OrderType.Limit,
            TimeInForce.Day, Px(11.00m), 100, 100, 2000UL));

        Assert.Throws<InvalidOperationException>(() => dst.RestoreState(snap));
    }

    [Fact]
    public void Restore_PreservesIcebergFields()
    {
        var src = NewEngine(out _);
        src.Submit(new NewOrderCommand("ICE", Sec, Side.Buy, OrderType.Limit,
            TimeInForce.Day, Px(10.00m), 1000, 100, 1000UL)
        { MaxFloor = 100 });

        var snap = src.CaptureState();
        var rec = snap.Books.Single().Orders.Single();
        Assert.Equal(100L, rec.MaxFloor);
        // 1000 total, 100 visible → 900 hidden.
        Assert.Equal(900L, rec.HiddenQuantity);
        Assert.Equal(100L, rec.RemainingQuantity);

        var dst = NewEngine(out _);
        dst.RestoreState(snap);
        // Restored book exposes only the visible slice.
        var view = dst.EnumerateBook(Sec, Side.Buy).Single();
        Assert.Equal(100L, view.RemainingQuantity);
    }

    [Fact]
    public void Restore_MwlLeftoverModifyEchoesReplaceOrdTypeAndRestingProtectionPrice()
    {
        var src = NewEngine(out var srcSink);
        src.Submit(new NewOrderCommand("S", Sec, Side.Sell, OrderType.Limit,
            TimeInForce.Day, Px(10.00m), 100, 100, 1_000UL));
        src.Submit(new NewOrderCommand("MWL", Sec, Side.Buy, OrderType.MarketWithLeftover,
            TimeInForce.Day, 0, 300, 200, 2_000UL));
        long mwlOrderId = srcSink.AcceptedOrderIds.Last();

        var dst = NewEngine(out var dstSink);
        dst.RestoreState(src.CaptureState());

        dst.Replace(new ReplaceOrderCommand("MWL-R", Sec, mwlOrderId, Px(10.00m), 100, 3_000UL)
        {
            NewOrdType = OrderType.MarketWithLeftover,
        });

        var modified = Assert.Single(dstSink.Modified);
        Assert.Equal(OrderType.MarketWithLeftover, modified.OrdType);
        Assert.Equal(Px(10.00m), modified.ProtectionPriceMantissa);
        Assert.Equal(Px(10.00m), modified.NewPriceMantissa);
        Assert.Equal(100, modified.NewRemainingQuantity);
    }
}
