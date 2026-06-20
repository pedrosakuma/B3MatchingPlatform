namespace B3.Exchange.Matching.Tests;

using static TestFactory;

/// <summary>
/// GAP-23 / issue #499 — Good-Till-Date (GTD) expiry semantics. A resting
/// GTD order carries a B3 <c>LocalMktDate</c> (<c>ExpireDate</c>, days since
/// the Unix epoch) and is cancelled by the end-of-trading-day sweep
/// (<see cref="MatchingEngine.ExpireGtdOrders"/>) when the trading day being
/// closed is on or after that date. Non-GTD orders are never swept. Replace
/// resolves the effective expiry from the resting order plus any wire
/// override.
/// </summary>
public class GtdExpiryTests
{
    private const ulong Txn = 9_000;

    private static long PlaceGtd(MatchingEngine eng, RecordingSink sink, ushort expireDate,
        long qty = 100, decimal px = 10m, Side side = Side.Buy, ulong at = 1000)
    {
        eng.Submit(new NewOrderCommand("g" + expireDate, PetrSecId, side, OrderType.Limit, TimeInForce.Gtd, Px(px), qty, 11, at)
        {
            ExpireDate = expireDate,
        });
        return sink.Accepted[^1].OrderId;
    }

    [Fact]
    public void ExpireGtdOrders_CancelsOnAndBeforeBoundary_KeepsAfter()
    {
        var eng = NewEngine(out var sink);
        // Three GTD orders at distinct, non-crossing buy prices so they all rest.
        PlaceGtd(eng, sink, expireDate: 19_999, px: 9.97m); // before boundary -> cancel
        PlaceGtd(eng, sink, expireDate: 20_000, px: 9.98m); // on boundary     -> cancel
        PlaceGtd(eng, sink, expireDate: 20_001, px: 9.99m); // after boundary  -> keep
        Assert.Equal(3, eng.OrderCount(PetrSecId));
        sink.Clear();

        int cancelled = eng.ExpireGtdOrders(currentDate: 20_000, txnNanos: Txn);

        Assert.Equal(2, cancelled);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
        Assert.All(sink.Canceled, c => Assert.Equal(CancelReason.GtdExpired, c.Reason));
        Assert.Equal(2, sink.Canceled.Count);
    }

    [Fact]
    public void ExpireGtdOrders_DoesNotTouchGtcOrDayOrders()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("day", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.98m), 100, 11, 1000));
        eng.Submit(new NewOrderCommand("gtc", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtc, Px(9.99m), 100, 11, 1100));
        Assert.Equal(2, eng.OrderCount(PetrSecId));
        sink.Clear();

        int cancelled = eng.ExpireGtdOrders(currentDate: 60_000, txnNanos: Txn);

        Assert.Equal(0, cancelled);
        Assert.Equal(2, eng.OrderCount(PetrSecId));
        Assert.Empty(sink.Canceled);
    }

    [Fact]
    public void Replace_PriorityKept_PreservesExpireDate()
    {
        var eng = NewEngine(out var sink);
        long oid = PlaceGtd(eng, sink, expireDate: 20_000, qty: 200);
        sink.Clear();

        // Reduce qty at the same price with no TIF/ExpireDate override:
        // priority kept, ExpireDate preserved.
        eng.Replace(new ReplaceOrderCommand("r1", PetrSecId, oid, Px(10m), 100, 2000));
        Assert.Empty(sink.Rejects);

        Assert.Equal(0, eng.ExpireGtdOrders(currentDate: 19_999, txnNanos: Txn));
        Assert.Equal(1, eng.ExpireGtdOrders(currentDate: 20_000, txnNanos: Txn));
    }

    [Fact]
    public void Replace_PriorityKept_AmendsExpireDate()
    {
        var eng = NewEngine(out var sink);
        long oid = PlaceGtd(eng, sink, expireDate: 20_000, qty: 200);
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("r1", PetrSecId, oid, Px(10m), 100, 2000)
        {
            NewExpireDate = 30_000,
        });
        Assert.Empty(sink.Rejects);

        // Old boundary no longer cancels; the amended later boundary does.
        Assert.Equal(0, eng.ExpireGtdOrders(currentDate: 20_000, txnNanos: Txn));
        Assert.Equal(1, eng.ExpireGtdOrders(currentDate: 30_000, txnNanos: Txn));
    }

    [Fact]
    public void Replace_DayToGtd_RequiresExpireDate()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("d1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        long oid = sink.Accepted[^1].OrderId;
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("r1", PetrSecId, oid, Px(10m), 100, 2000)
        {
            NewTif = TimeInForce.Gtd,
        });

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, rej.Reason);
    }

    [Fact]
    public void Replace_DayToGtd_WithExpireDate_RestsAndExpires()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("d1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        long oid = sink.Accepted[^1].OrderId;
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("r1", PetrSecId, oid, Px(10m), 100, 2000)
        {
            NewTif = TimeInForce.Gtd,
            NewExpireDate = 20_000,
        });
        Assert.Empty(sink.Rejects);
        Assert.Equal(1, eng.OrderCount(PetrSecId));

        Assert.Equal(1, eng.ExpireGtdOrders(currentDate: 20_000, txnNanos: Txn));
    }

    [Fact]
    public void Replace_DayToGtd_WithPastExpireDate_RejectsAsUnsupportedAndDoesNotRestGtd()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("d1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        long oid = sink.Accepted[^1].OrderId;
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("r1", PetrSecId, oid, Px(10m), 100, 2000)
        {
            NewTif = TimeInForce.Gtd,
            NewExpireDate = 19_999,
            CurrentMarketDate = 20_000,
        });

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.UnsupportedOrderCharacteristic, rej.Reason);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
        Assert.Equal(0, eng.ExpireGtdOrders(currentDate: 20_000, txnNanos: Txn));
    }

    [Fact]
    public void Replace_PreserveGtd_WithPastNewExpireDate_RejectsAsUnsupportedAndKeepsOriginalExpiry()
    {
        var eng = NewEngine(out var sink);
        long oid = PlaceGtd(eng, sink, expireDate: 20_010, qty: 200);
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("r1", PetrSecId, oid, Px(10m), 100, 2000)
        {
            NewExpireDate = 19_999,
            CurrentMarketDate = 20_000,
        });

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.UnsupportedOrderCharacteristic, rej.Reason);
        Assert.Equal(0, eng.ExpireGtdOrders(currentDate: 20_000, txnNanos: Txn));
        Assert.Equal(1, eng.ExpireGtdOrders(currentDate: 20_010, txnNanos: Txn));
    }

    [Fact]
    public void Replace_GtdExpireDateEqualCurrentMarketDate_Accepted()
    {
        var eng = NewEngine(out var sink);
        long oid = PlaceGtd(eng, sink, expireDate: 20_010, qty: 200);
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("r1", PetrSecId, oid, Px(10m), 100, 2000)
        {
            NewExpireDate = 20_000,
            CurrentMarketDate = 20_000,
        });

        Assert.Empty(sink.Rejects);
        Assert.Equal(0, eng.ExpireGtdOrders(currentDate: 19_999, txnNanos: Txn));
        Assert.Equal(1, eng.ExpireGtdOrders(currentDate: 20_000, txnNanos: Txn));
    }

    [Fact]
    public void Replace_ToDay_WithSuppliedExpireDate_StillRejectsInvalidField()
    {
        var eng = NewEngine(out var sink);
        long oid = PlaceGtd(eng, sink, expireDate: 20_010, qty: 100);
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("r1", PetrSecId, oid, Px(10m), 100, 2000)
        {
            NewTif = TimeInForce.Day,
            NewExpireDate = 19_999,
            CurrentMarketDate = 20_000,
        });

        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InvalidField, rej.Reason);
    }

    [Fact]
    public void Replace_PastGtdExpireDate_WithoutCurrentMarketDate_PreservesExistingBehavior()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("d1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        long oid = sink.Accepted[^1].OrderId;
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("r1", PetrSecId, oid, Px(10m), 100, 2000)
        {
            NewTif = TimeInForce.Gtd,
            NewExpireDate = 19_999,
        });

        Assert.Empty(sink.Rejects);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
        Assert.Equal(1, eng.ExpireGtdOrders(currentDate: 20_000, txnNanos: Txn));
    }

    [Fact]
    public void Replace_GtdToDay_ClearsExpiryAndIsNotSwept()
    {
        var eng = NewEngine(out var sink);
        long oid = PlaceGtd(eng, sink, expireDate: 20_000, qty: 100);
        sink.Clear();

        eng.Replace(new ReplaceOrderCommand("r1", PetrSecId, oid, Px(10m), 100, 2000)
        {
            NewTif = TimeInForce.Day,
        });
        Assert.Empty(sink.Rejects);
        Assert.Equal(1, eng.OrderCount(PetrSecId));

        // Now a Day order: never swept, even well past the old ExpireDate.
        Assert.Equal(0, eng.ExpireGtdOrders(currentDate: 60_000, txnNanos: Txn));
        Assert.Equal(1, eng.OrderCount(PetrSecId));
    }
}
