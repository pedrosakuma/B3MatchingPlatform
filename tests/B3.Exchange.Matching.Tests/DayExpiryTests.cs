namespace B3.Exchange.Matching.Tests;

using static TestFactory;

/// <summary>
/// Issue #506 — Day-order expiry at the daily session boundary. Resting
/// <see cref="TimeInForce.Day"/> limit orders and parked Day stop orders must
/// be cancelled unconditionally when the trading day closes
/// (<see cref="MatchingEngine.ExpireDayOrders"/>), each carrying
/// <see cref="CancelReason.DayExpired"/>. GTC and unexpired GTD orders, plus
/// GTC stops, survive the sweep.
/// </summary>
public class DayExpiryTests
{
    private const ulong Txn = 9_000;

    [Fact]
    public void ExpireDayOrders_CancelsDayLimits_KeepsGtcAndGtd()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("day1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.96m), 100, 11, 1000));
        eng.Submit(new NewOrderCommand("day2", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.97m), 100, 11, 1100));
        eng.Submit(new NewOrderCommand("gtc", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtc, Px(9.98m), 100, 11, 1200));
        eng.Submit(new NewOrderCommand("gtd", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtd, Px(9.99m), 100, 11, 1300) { ExpireDate = 20_001 });
        Assert.Equal(4, eng.OrderCount(PetrSecId));
        sink.Clear();

        int cancelled = eng.ExpireDayOrders(Txn);

        Assert.Equal(2, cancelled);
        Assert.Equal(2, eng.OrderCount(PetrSecId));
        Assert.Equal(2, sink.Canceled.Count);
        Assert.All(sink.Canceled, c => Assert.Equal(CancelReason.DayExpired, c.Reason));
    }

    [Fact]
    public void ExpireDayOrders_Idempotent()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("day1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.96m), 100, 11, 1000));
        sink.Clear();

        Assert.Equal(1, eng.ExpireDayOrders(Txn));
        Assert.Equal(0, eng.ExpireDayOrders(Txn));
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void ExpireDayOrders_CancelsParkedDayStop_KeepsGtcStop()
    {
        var eng = NewEngine(out var sink);
        // Two parked stops that do not trigger: a Day stop and a Gtc stop.
        eng.Submit(new NewOrderCommand("stopDay", PetrSecId, Side.Buy, OrderType.StopLimit, TimeInForce.Day, Px(10.50m), 100, 11, 1000)
        {
            StopPxMantissa = Px(10.45m),
        });
        eng.Submit(new NewOrderCommand("stopGtc", PetrSecId, Side.Buy, OrderType.StopLoss, TimeInForce.Gtc, 0, 200, 11, 1100)
        {
            StopPxMantissa = Px(10.60m),
        });
        Assert.Equal(2, eng.StopOrderCount);
        sink.Clear();

        int cancelled = eng.ExpireDayOrders(Txn);

        Assert.Equal(1, cancelled);
        Assert.Equal(1, eng.StopOrderCount);
        var stop = Assert.Single(sink.StopCanceled);
        Assert.Equal(CancelReason.DayExpired, stop.Reason);
        Assert.Equal(OrderType.StopLimit, stop.StopType);
        Assert.Equal(Px(10.45m), stop.StopPxMantissa);
        Assert.Equal(Px(10.50m), stop.LimitPriceMantissa);
    }

    [Fact]
    public void ExpireDayOrders_NoDayOrders_ReturnsZero()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("gtc", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Gtc, Px(9.98m), 100, 11, 1000));
        sink.Clear();

        Assert.Equal(0, eng.ExpireDayOrders(Txn));
        Assert.Equal(1, eng.OrderCount(PetrSecId));
        Assert.Empty(sink.Canceled);
        Assert.Empty(sink.StopCanceled);
    }
}
