namespace B3.Exchange.Matching.Tests;

using static TestFactory;

/// <summary>
/// Issue #200 (gap-functional §22): the engine must publish an
/// <see cref="OrderBookSideEmptyEvent"/> when a cancel or fill drains
/// the last order from a side; the integration layer translates this to
/// a UMDF <c>EmptyBook_9</c> frame.
/// </summary>
public class EmptyBookSideTests
{
    [Fact]
    public void Cancel_LastOrderOnSide_EmitsBookSideEmpty()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        var oid = sink.Accepted.Single().OrderId;
        sink.Clear();

        eng.Cancel(new CancelOrderCommand("c1", PetrSecId, oid, 11, 2000));

        Assert.Single(sink.Canceled);
        var ev = Assert.Single(sink.BookSideEmpty);
        Assert.Equal(PetrSecId, ev.SecurityId);
        Assert.Equal(Side.Buy, ev.Side);
        Assert.Equal(2000ul, ev.TransactTimeNanos);
    }

    [Fact]
    public void Cancel_LeavesOtherOrdersOnSide_DoesNotEmitBookSideEmpty()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        eng.Submit(new NewOrderCommand("b2", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.99m), 100, 11, 1001));
        var firstOid = sink.Accepted[0].OrderId;
        sink.Clear();

        eng.Cancel(new CancelOrderCommand("c1", PetrSecId, firstOid, 11, 2000));

        Assert.Single(sink.Canceled);
        Assert.Empty(sink.BookSideEmpty);
    }

    [Fact]
    public void Trade_FullyConsumesRestingSide_EmitsBookSideEmpty()
    {
        var eng = NewEngine(out var sink);
        // One resting sell — aggressor buy will fully consume.
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 22, 1000));
        sink.Clear();

        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 2000));

        Assert.Single(sink.Trades);
        Assert.Single(sink.Filled);
        var ev = Assert.Single(sink.BookSideEmpty);
        Assert.Equal(Side.Sell, ev.Side);
        Assert.Equal(PetrSecId, ev.SecurityId);
    }

    [Fact]
    public void MassCancel_DrainsBothSides_EmitsTwoBookSideEmpty()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.01m), 100, 11, 1001));
        var ids = sink.Accepted.Select(a => a.OrderId).ToList();
        sink.Clear();

        eng.MassCancel(ids, enteredAtNanos: 5_000);

        Assert.Equal(2, sink.BookSideEmpty.Count);
        Assert.Contains(sink.BookSideEmpty, e => e.Side == Side.Buy);
        Assert.Contains(sink.BookSideEmpty, e => e.Side == Side.Sell);
    }
}
