using System.IO;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Verifies that <see cref="FixpSession.ApplyTransition"/> increments the
/// process-wide <see cref="SessionLifecycleMetrics"/> exactly when state
/// transitions cross the canonical lifecycle edges that ops cares about
/// (Established / Suspended / Rebound). Reaped is incremented by the
/// listener and is covered by <see cref="EntryPointListenerReaperTests"/>
/// in a separate hook.
/// </summary>
public class SessionLifecycleMetricsWiringTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public void EnqueueNewOrder(in NewOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue) { }
        public void EnqueueCancel(in CancelOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { }
        public void EnqueueReplace(in ReplaceOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { }
        public void OnDecodeError(B3.Exchange.Contracts.SessionId session, string error) { }
        public void OnSessionClosed(B3.Exchange.Contracts.SessionId session) { }
    }

    private static FixpSession NewSession(SessionLifecycleMetrics metrics)
    {
        // No real transport: drive ApplyTransition directly. The session
        // is never Started so the recv loop is dormant.
        return new FixpSession(
            connectionId: 1, enteringFirm: 7, sessionId: 100,
            stream: Stream.Null, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance,
            options: new FixpSessionOptions { LifecycleMetrics = metrics });
    }

    [Fact]
    public void Initial_Negotiate_then_Establish_increments_Established_only()
    {
        var m = new SessionLifecycleMetrics();
        var s = NewSession(m);

        s.ApplyTransition(FixpEvent.Negotiate);
        Assert.Equal(0, m.Established);

        s.ApplyTransition(FixpEvent.Establish);
        Assert.Equal(1, m.Established);
        Assert.Equal(0, m.Suspended);
        Assert.Equal(0, m.Rebound);
    }

    [Fact]
    public void Detach_from_Established_increments_Suspended()
    {
        var m = new SessionLifecycleMetrics();
        var s = NewSession(m);
        s.ApplyTransition(FixpEvent.Negotiate);
        s.ApplyTransition(FixpEvent.Establish);

        s.ApplyTransition(FixpEvent.Detach);
        Assert.Equal(1, m.Suspended);
    }

    [Fact]
    public void Establish_from_Suspended_increments_BOTH_Established_and_Rebound()
    {
        var m = new SessionLifecycleMetrics();
        var s = NewSession(m);
        s.ApplyTransition(FixpEvent.Negotiate);
        s.ApplyTransition(FixpEvent.Establish); // 1st Established
        s.ApplyTransition(FixpEvent.Detach);    // → Suspended
        Assert.Equal(1, m.Established);
        Assert.Equal(1, m.Suspended);

        s.ApplyTransition(FixpEvent.Establish); // re-attach
        Assert.Equal(2, m.Established);
        Assert.Equal(1, m.Rebound);
    }

    [Fact]
    public void Same_state_transition_does_not_double_count()
    {
        var m = new SessionLifecycleMetrics();
        var s = NewSession(m);
        s.ApplyTransition(FixpEvent.Negotiate);
        s.ApplyTransition(FixpEvent.Establish);
        // A second Establish on an already-Established session is rejected
        // by the state machine (no-op); must not bump Established again.
        s.ApplyTransition(FixpEvent.Establish);
        Assert.Equal(1, m.Established);
        Assert.Equal(0, m.Rebound);
    }

    [Fact]
    public void Without_LifecycleMetrics_session_does_not_throw()
    {
        // Sanity: optional wiring; tests + legacy callers may omit metrics.
        var s = new FixpSession(
            connectionId: 1, enteringFirm: 7, sessionId: 100,
            stream: Stream.Null, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance);
        s.ApplyTransition(FixpEvent.Negotiate);
        s.ApplyTransition(FixpEvent.Establish);
        s.ApplyTransition(FixpEvent.Detach);
    }
}
