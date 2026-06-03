namespace B3.Exchange.Matching.Tests;

using static TestFactory;

/// <summary>
/// GAP-26 / issue #498 — daily Good-Till restatement semantics. At the
/// session boundary <see cref="MatchingEngine.RestateGtOrders"/> emits a
/// private <see cref="OrderRestatedEvent"/> for every surviving GTC order and
/// every GTD order whose <c>ExpireDate</c> is strictly after the trading day
/// being closed. Day orders and already-expired GTD orders are never
/// restated, and the book is left untouched (no cancel, no quantity change,
/// no RptSeq advance).
/// </summary>
public class GtRestatementTests
{
    private const ulong Txn = 9_000;

    [Fact]
    public void RestateGtOrders_RestatesGtcAndUnexpiredGtd_NotDayOrExpiredGtd()
    {
        var eng = NewEngine(out var sink);
        // Distinct non-crossing buy prices so all four rest.
        eng.Submit(new NewOrderCommand("day", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.96m), 100, 11, 1000));
        eng.Submit(new NewOrderCommand("gtc", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtc, Px(9.97m), 100, 11, 1100));
        eng.Submit(new NewOrderCommand("gtdFuture", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtd, Px(9.98m), 100, 11, 1200) { ExpireDate = 20_001 });
        eng.Submit(new NewOrderCommand("gtdPast", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtd, Px(9.99m), 100, 11, 1300) { ExpireDate = 20_000 });
        long gtcOid = sink.Accepted[1].OrderId;
        long gtdFutureOid = sink.Accepted[2].OrderId;
        Assert.Equal(4, eng.OrderCount(PetrSecId));
        sink.Clear();

        int restated = eng.RestateGtOrders(currentDate: 20_000, txnNanos: Txn);

        // Only GTC + future-dated GTD are restated; Day and on-boundary GTD skipped.
        Assert.Equal(2, restated);
        Assert.Equal(2, sink.Restated.Count);
        Assert.Contains(sink.Restated, r => r.OrderId == gtcOid && r.Tif == TimeInForce.Gtc);
        Assert.Contains(sink.Restated, r => r.OrderId == gtdFutureOid && r.Tif == TimeInForce.Gtd && r.ExpireDate == 20_001);
        // Book is untouched.
        Assert.Equal(4, eng.OrderCount(PetrSecId));
        Assert.Empty(sink.Canceled);
    }

    [Fact]
    public void RestateGtOrders_ReportsOpenQuantityIncludingHiddenIceberg()
    {
        var eng = NewEngine(out var sink);
        // Iceberg GTC: 1000 total, 100 displayed -> 900 hidden. Open = 1000.
        eng.Submit(new NewOrderCommand("ice", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtc, Px(9.97m), 1000, 11, 1000)
        {
            MaxFloor = 100,
        });
        sink.Clear();

        int restated = eng.RestateGtOrders(currentDate: 20_000, txnNanos: Txn);

        Assert.Equal(1, restated);
        var r = Assert.Single(sink.Restated);
        Assert.Equal(1000, r.OpenQuantity);
        Assert.Equal(Px(9.97m), r.PriceMantissa);
        Assert.Equal(Txn, r.TransactTimeNanos);
    }

    [Fact]
    public void RestateGtOrders_NoGtOrders_ReturnsZero()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("day", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.97m), 100, 11, 1000));
        sink.Clear();

        Assert.Equal(0, eng.RestateGtOrders(currentDate: 20_000, txnNanos: Txn));
        Assert.Empty(sink.Restated);
    }
}
