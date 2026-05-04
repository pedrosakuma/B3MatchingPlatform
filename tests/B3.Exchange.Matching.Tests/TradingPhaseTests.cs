namespace B3.Exchange.Matching.Tests;

using static TestFactory;

/// <summary>
/// Issue #201 (gap-functional §5): per-instrument trading phase. The
/// engine defaults to <see cref="TradingPhase.Open"/>, allows operator
/// transitions via <c>SetTradingPhase</c>, emits a
/// <see cref="TradingPhaseChangedEvent"/> with a fresh <c>RptSeq</c> on
/// every non-trivial transition, and rejects new orders with
/// <see cref="RejectReason.MarketClosed"/> while the phase is anything
/// other than Open.
/// </summary>
public class TradingPhaseTests
{
    [Fact]
    public void DefaultPhase_IsOpen_AllowsSubmission()
    {
        var eng = NewEngine(out var sink);

        Assert.Equal(TradingPhase.Open, eng.GetTradingPhase(PetrSecId));
        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void SetTradingPhase_NonOpen_EmitsEventWithRptSeq()
    {
        var eng = NewEngine(out var sink);

        var changed = eng.SetTradingPhase(PetrSecId, TradingPhase.Close, txnNanos: 5000);

        Assert.True(changed);
        var ev = Assert.Single(sink.PhaseChanges);
        Assert.Equal(PetrSecId, ev.SecurityId);
        Assert.Equal(TradingPhase.Close, ev.Phase);
        Assert.Equal(5000ul, ev.TransactTimeNanos);
        Assert.Equal(1u, ev.RptSeq);
        Assert.Equal(1u, eng.CurrentRptSeq);
        Assert.Equal(TradingPhase.Close, eng.GetTradingPhase(PetrSecId));
    }

    [Fact]
    public void SetTradingPhase_Idempotent_NoEvent()
    {
        var eng = NewEngine(out var sink);

        var changed = eng.SetTradingPhase(PetrSecId, TradingPhase.Open, txnNanos: 5000);

        Assert.False(changed);
        Assert.Empty(sink.PhaseChanges);
        Assert.Equal(0u, eng.CurrentRptSeq);
    }

    [Fact]
    public void Submit_DuringClose_RejectsWithMarketClosed()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Close, 5000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 6000));

        Assert.Empty(sink.Accepted);
        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MarketClosed, rej.Reason);
        Assert.Equal("o1", rej.ClOrdId);
    }

    [Fact]
    public void Submit_DuringPause_RejectsWithMarketClosed()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Pause, 5000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 6000));

        Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.MarketClosed, sink.Rejects[0].Reason);
    }

    [Fact]
    public void Cancel_DuringClose_StillSucceeds()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 1000));
        var oid = sink.Accepted.Single().OrderId;
        eng.SetTradingPhase(PetrSecId, TradingPhase.Close, 5000);
        sink.Clear();

        eng.Cancel(new CancelOrderCommand("c1", PetrSecId, oid, 6000));

        Assert.Single(sink.Canceled);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void SetTradingPhase_BackToOpen_AllowsSubmission()
    {
        var eng = NewEngine(out var sink);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Close, 5000);
        eng.SetTradingPhase(PetrSecId, TradingPhase.Open, 6000);
        sink.Clear();

        eng.Submit(new NewOrderCommand("o1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 11, 7000));

        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void SetTradingPhase_UnknownSecurity_Throws()
    {
        var eng = NewEngine(out _);
        Assert.Throws<KeyNotFoundException>(() => eng.SetTradingPhase(123L, TradingPhase.Close, 1000));
    }
}
