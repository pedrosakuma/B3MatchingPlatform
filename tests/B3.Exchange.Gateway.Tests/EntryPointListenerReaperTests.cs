using B3.Exchange.Contracts;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #69b-1: <see cref="EntryPointListener"/> must reap FIXP sessions
/// that have lingered in <see cref="FixpState.Suspended"/> beyond
/// <see cref="FixpSessionOptions.SuspendedTimeoutMs"/>. Without this, every
/// transport drop while Established (#69a) leaks the session, its claim,
/// and the engine sink reference until process exit. The reaper exists
/// as a stop-gap; #69b-2 (re-attach) is expected to recover most
/// suspended sessions before the timeout fires.
/// </summary>
public class EntryPointListenerReaperTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public int SessionClosedCalls;
        public bool EnqueueNewOrder(in NewOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue) { return true; }
        public bool EnqueueCancel(in CancelOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { return true; }
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { return true; }
        public bool EnqueueCross(in CrossOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { return true; }
        public bool EnqueueMassCancel(in MassCancelCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { return true; }
        public void OnDecodeError(B3.Exchange.Contracts.SessionId session, string error) { }
        public void OnSessionClosed(B3.Exchange.Contracts.SessionId session) => Interlocked.Increment(ref SessionClosedCalls);
    }

    /// <summary>
    /// Stand up a listener, accept one client, return the resulting
    /// <see cref="FixpSession"/> driven to <see cref="FixpState.Suspended"/>
    /// by closing the client transport while we have already nudged the
    /// state machine to Established (test seam — <c>ApplyTransition</c>
    /// is internal so we don't need to wire the full Negotiate+Establish
    /// handshake just to exercise the reaper).
    /// </summary>
    private static async Task<(EntryPointListener listener, NoOpEngineSink sink, TcpClient client, FixpSession session, List<string> closures)>
        SetupSuspendedSessionAsync(int suspendedTimeoutMs)
    {
        var sink = new NoOpEngineSink();
        var closures = new List<string>();
        var options = new FixpSessionOptions
        {
            HeartbeatIntervalMs = 60_000,
            IdleTimeoutMs = 60_000,
            TestRequestGraceMs = 60_000,
            SuspendedTimeoutMs = suspendedTimeoutMs,
        };
        var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            sink,
            NullLoggerFactory.Instance,
            sessionOptions: options,
            onSessionClosed: (_, reason) => { lock (closures) closures.Add(reason); });
        listener.Start();

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);

        // Wait for the listener's accept loop to register the session.
        var registered = await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Count == 1,
            TimeSpan.FromSeconds(2));
        Assert.True(registered, "listener never registered the accepted session");
        var session = listener.ActiveSessions[0];

        // Drive state to Established, then drop client → recv loop EOF →
        // OnTransportClosed → Suspend.
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        Assert.Equal(FixpState.Established, session.State);
        client.Close();
        var suspended = await TestUtil.WaitUntilAsync(
            () => session.State == FixpState.Suspended && session.SuspendedSinceMs is not null,
            TimeSpan.FromSeconds(2));
        Assert.True(suspended, $"session never became Suspended (state={session.State})");
        return (listener, sink, client, session, closures);
    }

    [Fact]
    public async Task Reaper_ClosesSession_WhenSuspendedLongerThanTimeout()
    {
        var (listener, sink, client, session, closures) = await SetupSuspendedSessionAsync(suspendedTimeoutMs: 200);
        try
        {
            // Background reaper polls every ~50ms (floor) at 200ms timeout,
            // so it should observe the session past timeout within ~250ms.
            var reaped = await TestUtil.WaitUntilAsync(
                () => !session.IsOpen && session.SuspendedSinceMs is null,
                TimeSpan.FromSeconds(2));
            Assert.True(reaped, "reaper did not close the suspended session within 2s");
            Assert.Equal(1, Volatile.Read(ref sink.SessionClosedCalls));
            lock (closures)
            {
                Assert.Single(closures);
                Assert.Equal("suspended-timeout", closures[0]);
            }
            // Listener must remove the closed session from its tracking list.
            Assert.DoesNotContain(session, listener.ActiveSessions);
        }
        finally
        {
            client.Dispose();
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reaper_DoesNotClose_BeforeTimeout()
    {
        // Long timeout so the background loop never fires during the test.
        var (listener, sink, client, session, closures) = await SetupSuspendedSessionAsync(suspendedTimeoutMs: 60_000);
        try
        {
            // Drive one synchronous reaper pass with the actual current
            // tick: should be a no-op because the session has been
            // suspended for a few ms only.
            listener.ReapSuspendedOnce(Environment.TickCount64);
            Assert.True(session.IsOpen || session.State == FixpState.Suspended);
            Assert.Equal(0, Volatile.Read(ref sink.SessionClosedCalls));
            Assert.Empty(closures);
        }
        finally
        {
            client.Dispose();
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reaper_DoesNotTouch_NonSuspendedSessions()
    {
        var sink = new NoOpEngineSink();
        var closures = new List<string>();
        var options = new FixpSessionOptions
        {
            HeartbeatIntervalMs = 60_000,
            IdleTimeoutMs = 60_000,
            TestRequestGraceMs = 60_000,
            SuspendedTimeoutMs = 100,
        };
        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            sink,
            NullLoggerFactory.Instance,
            sessionOptions: options,
            onSessionClosed: (_, reason) => { lock (closures) closures.Add(reason); });
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        await TestUtil.WaitUntilAsync(() => listener.ActiveSessions.Count == 1, TimeSpan.FromSeconds(2));
        var session = listener.ActiveSessions[0];

        // Session is Idle (never suspended). Sleep past the suspend
        // timeout; the background reaper must NOT touch it.
        await Task.Delay(400);
        Assert.True(session.IsOpen);
        Assert.Equal(FixpState.Idle, session.State);
        Assert.Equal(0, Volatile.Read(ref sink.SessionClosedCalls));
        Assert.Empty(closures);
    }

    [Fact]
    public async Task ReaperDisabled_WhenSuspendedTimeoutMsIsZero()
    {
        // SuspendedTimeoutMs=0 must skip starting the reaper Task and skip
        // the synchronous pass entirely. This is the "preserve forever"
        // mode used by tests that want pure suspend semantics.
        var (listener, sink, client, session, closures) = await SetupSuspendedSessionAsync(suspendedTimeoutMs: 0);
        try
        {
            // Even past a generous wait, no reap.
            await Task.Delay(300);
            Assert.False(session.IsOpen); // transport is down (Suspended)
            Assert.Equal(FixpState.Suspended, session.State);
            Assert.NotNull(session.SuspendedSinceMs);
            Assert.Equal(0, Volatile.Read(ref sink.SessionClosedCalls));
            Assert.Empty(closures);
            // Direct call must also be a no-op.
            listener.ReapSuspendedOnce(long.MaxValue);
            Assert.NotNull(session.SuspendedSinceMs);
            Assert.Equal(0, Volatile.Read(ref sink.SessionClosedCalls));
        }
        finally
        {
            client.Dispose();
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reaper_increments_LifecycleMetrics_Reaped_counter()
    {
        var sink = new NoOpEngineSink();
        var metrics = new SessionLifecycleMetrics();
        var options = new FixpSessionOptions
        {
            HeartbeatIntervalMs = 60_000,
            IdleTimeoutMs = 60_000,
            TestRequestGraceMs = 60_000,
            SuspendedTimeoutMs = 200,
            LifecycleMetrics = metrics,
        };
        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            sink,
            NullLoggerFactory.Instance,
            sessionOptions: options);
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
        var registered = await TestUtil.WaitUntilAsync(
            () => listener.ActiveSessions.Count == 1, TimeSpan.FromSeconds(2));
        Assert.True(registered);
        var session = listener.ActiveSessions[0];
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        Assert.Equal(1, metrics.Established);
        client.Close();
        var suspended = await TestUtil.WaitUntilAsync(
            () => session.State == FixpState.Suspended, TimeSpan.FromSeconds(2));
        Assert.True(suspended);
        Assert.Equal(1, metrics.Suspended);

        var reaped = await TestUtil.WaitUntilAsync(
            () => metrics.Reaped >= 1, TimeSpan.FromSeconds(2));
        Assert.True(reaped, $"reaper did not increment Reaped within 2s (Reaped={metrics.Reaped})");
        // Sanity: Rebound stays zero (we never re-attached).
        Assert.Equal(0, metrics.Rebound);
    }
}
