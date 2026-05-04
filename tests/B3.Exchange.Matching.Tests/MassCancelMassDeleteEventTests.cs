namespace B3.Exchange.Matching.Tests;

using static TestFactory;

/// <summary>
/// Issue #199 (gap-functional §8): a single MassCancel must publish one
/// summary <see cref="OrderMassCanceledEvent"/> per (SecurityId, Side)
/// pair before the per-order <see cref="OrderCanceledEvent"/>s. The
/// integration layer translates the summary into a UMDF
/// <c>MassDeleteOrders_MBO_52</c> frame.
/// </summary>
public class MassCancelMassDeleteEventTests
{
    [Fact]
    public void MassCancel_GroupsByBidAndOffer_EmitsTwoSummariesAhead()
    {
        var eng = NewEngine(out var sink);
        // Two bids + two offers resting on the same book.
        eng.Submit(new NewOrderCommand("b1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        eng.Submit(new NewOrderCommand("b2", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.99m), 100, 11, 1001));
        eng.Submit(new NewOrderCommand("s1", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.01m), 100, 11, 1002));
        eng.Submit(new NewOrderCommand("s2", PetrSecId, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.02m), 100, 11, 1003));
        var orderIds = sink.Accepted.Select(a => a.OrderId).ToList();
        Assert.Equal(4, orderIds.Count);
        sink.Clear();

        int n = eng.MassCancel(orderIds, enteredAtNanos: 9_000);

        Assert.Equal(4, n);
        // 2 mass-cancel summaries (Buy, Sell) + 4 per-order cancels.
        Assert.Equal(2, sink.MassCanceled.Count);
        Assert.Equal(4, sink.Canceled.Count);
        // Summaries must come before per-order cancels in the event stream.
        int firstCancelIdx = sink.Events.FindIndex(e => e is OrderCanceledEvent);
        int lastSummaryIdx = sink.Events.FindLastIndex(e => e is OrderMassCanceledEvent);
        Assert.True(lastSummaryIdx < firstCancelIdx,
            $"summaries must precede per-order cancels (lastSummary={lastSummaryIdx}, firstCancel={firstCancelIdx})");

        var bidSummary = Assert.Single(sink.MassCanceled, m => m.Side == Side.Buy);
        var askSummary = Assert.Single(sink.MassCanceled, m => m.Side == Side.Sell);
        Assert.Equal(2, bidSummary.CancelledCount);
        Assert.Equal(2, askSummary.CancelledCount);
        Assert.Equal(PetrSecId, bidSummary.SecurityId);
        Assert.Equal(9_000ul, bidSummary.TransactTimeNanos);
        // Each summary consumes a unique RptSeq strictly before any
        // per-order cancel's RptSeq.
        Assert.True(sink.MassCanceled.All(m =>
                sink.Canceled.All(c => m.RptSeq < c.RptSeq)),
            "summary RptSeq must precede per-order cancel RptSeq");
    }

    [Fact]
    public void MassCancel_EmptyInput_EmitsNoEvents()
    {
        var eng = NewEngine(out var sink);
        int n = eng.MassCancel(Array.Empty<long>(), enteredAtNanos: 0);
        Assert.Equal(0, n);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void MassCancel_UnknownIds_EmitsNoSummary()
    {
        var eng = NewEngine(out var sink);
        int n = eng.MassCancel(new long[] { 99, 100, 101 }, enteredAtNanos: 0);
        Assert.Equal(0, n);
        Assert.Empty(sink.MassCanceled);
        Assert.Empty(sink.Canceled);
    }
}
