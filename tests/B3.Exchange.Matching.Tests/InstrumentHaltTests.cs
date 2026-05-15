namespace B3.Exchange.Matching.Tests;

using static TestFactory;

/// <summary>
/// Issue #322: per-instrument administrative-halt overlay on the engine.
/// These tests pin the contract independent of the dispatcher /
/// HTTP layer so any future refactor of the wire transport must keep
/// the engine semantics identical.
/// </summary>
public class InstrumentHaltTests
{
    [Fact]
    public void HaltInstrument_FirstCall_EmitsEventAndReportsHalted()
    {
        var eng = NewEngine(out var sink);
        bool changed = eng.HaltInstrument(PetrSecId, HaltReason.RegulatoryHalt, "manual", txnNanos: 1000UL);
        Assert.True(changed);
        var evt = Assert.Single(sink.Halted);
        Assert.Equal(PetrSecId, evt.SecurityId);
        Assert.Equal(HaltReason.RegulatoryHalt, evt.Reason);
        Assert.Equal("manual", evt.Note);
        Assert.Equal(1000UL, evt.TransactTimeNanos);
        Assert.True(eng.IsHalted(PetrSecId, out var state));
        Assert.Equal(HaltReason.RegulatoryHalt, state.Reason);
        Assert.Equal(1000UL, state.HaltedAtNanos);
        Assert.Equal("manual", state.Note);
    }

    [Fact]
    public void HaltInstrument_RepeatedCall_IsIdempotentNoEvent()
    {
        var eng = NewEngine(out var sink);
        eng.HaltInstrument(PetrSecId, HaltReason.RegulatoryHalt, null, 1000UL);
        sink.Clear();
        bool changed = eng.HaltInstrument(PetrSecId, HaltReason.NewsHold, "ignored", 2000UL);
        Assert.False(changed);
        Assert.Empty(sink.Halted);
        Assert.True(eng.IsHalted(PetrSecId, out var state));
        // First-write-wins: re-halt does not overwrite reason/note.
        Assert.Equal(HaltReason.RegulatoryHalt, state.Reason);
        Assert.Equal(1000UL, state.HaltedAtNanos);
    }

    [Fact]
    public void HaltInstrument_UnknownSecurity_Throws()
    {
        var eng = NewEngine(out _);
        Assert.Throws<KeyNotFoundException>(() =>
            eng.HaltInstrument(securityId: 999_999L, HaltReason.RegulatoryHalt, null, 1000UL));
    }

    [Fact]
    public void Submit_WhileHalted_RejectsWithInstrumentHaltedReason()
    {
        var eng = NewEngine(out var sink);
        eng.HaltInstrument(PetrSecId, HaltReason.VolatilityCircuitBreaker, null, 1000UL);
        sink.Clear();
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day,
            Px(10m), 100, 11, 2000));
        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InstrumentHalted, rej.Reason);
        Assert.Empty(sink.Accepted);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void Replace_WhileHalted_RejectsWithInstrumentHaltedReason()
    {
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day,
            Px(10m), 100, 11, 1000));
        var orderId = Assert.Single(sink.Accepted).OrderId;
        eng.HaltInstrument(PetrSecId, HaltReason.NewsHold, null, 2000UL);
        sink.Clear();
        eng.Replace(new ReplaceOrderCommand("c1r", PetrSecId, orderId, Px(11m), 100, 3000UL));
        var rej = Assert.Single(sink.Rejects);
        Assert.Equal(RejectReason.InstrumentHalted, rej.Reason);
        Assert.Equal(1, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void Cancel_WhileHalted_StillAllowed()
    {
        // Per spec: traders can pull resting orders during halt.
        var eng = NewEngine(out var sink);
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day,
            Px(10m), 100, 11, 1000));
        var orderId = Assert.Single(sink.Accepted).OrderId;
        eng.HaltInstrument(PetrSecId, HaltReason.PendingDisclosure, null, 2000UL);
        sink.Clear();
        eng.Cancel(new CancelOrderCommand("x", PetrSecId, orderId, 3000UL));
        Assert.Single(sink.Canceled);
        Assert.Empty(sink.Rejects);
        Assert.Equal(0, eng.OrderCount(PetrSecId));
    }

    [Fact]
    public void ResumeInstrument_AfterHalt_EmitsEventAndAllowsNewOrders()
    {
        var eng = NewEngine(out var sink);
        eng.HaltInstrument(PetrSecId, HaltReason.RegulatoryHalt, null, 1000UL);
        sink.Clear();
        bool changed = eng.ResumeInstrument(PetrSecId, txnNanos: 2000UL);
        Assert.True(changed);
        var evt = Assert.Single(sink.Resumed);
        Assert.Equal(PetrSecId, evt.SecurityId);
        Assert.Equal(2000UL, evt.TransactTimeNanos);
        Assert.False(eng.IsHalted(PetrSecId, out _));
        sink.Clear();
        eng.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day,
            Px(10m), 100, 11, 3000));
        Assert.Single(sink.Accepted);
        Assert.Empty(sink.Rejects);
    }

    [Fact]
    public void ResumeInstrument_WhenNotHalted_IsNoOp()
    {
        var eng = NewEngine(out var sink);
        bool changed = eng.ResumeInstrument(PetrSecId, txnNanos: 1000UL);
        Assert.False(changed);
        Assert.Empty(sink.Resumed);
    }

    [Fact]
    public void CaptureRestoreState_RoundTripsHaltOverlay()
    {
        var eng = NewEngine(out _);
        eng.HaltInstrument(PetrSecId, HaltReason.VolatilityCircuitBreaker, "circuit-break", 5000UL);
        var snap = eng.CaptureState();
        Assert.NotNull(snap.Halts);
        var entry = Assert.Single(snap.Halts!);
        Assert.Equal(PetrSecId, entry.SecurityId);
        Assert.Equal((byte)HaltReason.VolatilityCircuitBreaker, entry.Reason);
        Assert.Equal(5000UL, entry.HaltedAtNanos);
        Assert.Equal("circuit-break", entry.Note);

        var fresh = NewEngine(out var sink2);
        fresh.RestoreState(snap);
        Assert.True(fresh.IsHalted(PetrSecId, out var state));
        Assert.Equal(HaltReason.VolatilityCircuitBreaker, state.Reason);
        Assert.Equal("circuit-break", state.Note);
        // Submit after restore is still rejected.
        fresh.Submit(new NewOrderCommand("c1", PetrSecId, Side.Buy, OrderType.Limit, TimeInForce.Day,
            Px(10m), 100, 11, 6000));
        Assert.Equal(RejectReason.InstrumentHalted, Assert.Single(sink2.Rejects).Reason);
    }
}
