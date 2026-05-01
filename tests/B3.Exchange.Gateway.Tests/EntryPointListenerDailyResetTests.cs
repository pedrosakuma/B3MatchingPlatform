using System.Net;
using System.Net.Sockets;
using B3.Exchange.Core;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Spec §4.5.1 / #GAP-09 (issue #47): the trading-day rollover must
/// terminate every active FIXP session so clients reconnect with a
/// fresh Negotiate + Establish(nextSeqNo=1). The listener exposes
/// <see cref="EntryPointListener.TerminateAllSessions"/> as the
/// transport-level primitive that both the scheduled timer and the
/// operator HTTP endpoint hit.
/// </summary>
public class EntryPointListenerDailyResetTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public void EnqueueNewOrder(in NewOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue) { }
        public void EnqueueCancel(in CancelOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { }
        public void EnqueueReplace(in ReplaceOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { }
        public void OnDecodeError(B3.Exchange.Contracts.SessionId session, string error) { }
        public void OnSessionClosed(B3.Exchange.Contracts.SessionId session) { }
    }

    [Fact]
    public async Task TerminateAllSessions_ClosesEveryLiveSession_AndCallsCallback()
    {
        var sink = new NoOpEngineSink();
        var closures = new List<string>();
        var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            sink,
            NullLoggerFactory.Instance,
            sessionOptions: new FixpSessionOptions
            {
                HeartbeatIntervalMs = 60_000,
                IdleTimeoutMs = 60_000,
                TestRequestGraceMs = 60_000,
                SuspendedTimeoutMs = 0, // disable reaper so it can't race us
            },
            onSessionClosed: (_, reason) => { lock (closures) closures.Add(reason); });
        listener.Start();
        try
        {
            // Open two clients and wait until both are registered.
            var c1 = new TcpClient(); await c1.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);
            var c2 = new TcpClient(); await c2.ConnectAsync(IPAddress.Loopback, listener.LocalEndpoint!.Port);

            var registered = await TestUtil.WaitUntilAsync(
                () => listener.ActiveSessions.Count(s => s.IsOpen) == 2,
                TimeSpan.FromSeconds(2));
            Assert.True(registered, $"both sessions should register (have {listener.ActiveSessions.Count})");

            int closed = listener.TerminateAllSessions("daily-reset");
            Assert.Equal(2, closed);

            // All sessions are closed (IsOpen=false) and the callback fired
            // for each with the supplied reason.
            var allClosed = await TestUtil.WaitUntilAsync(
                () => listener.ActiveSessions.All(s => !s.IsOpen),
                TimeSpan.FromSeconds(2));
            Assert.True(allClosed);
            lock (closures)
            {
                Assert.Equal(2, closures.Count);
                Assert.All(closures, r => Assert.Equal("daily-reset", r));
            }

            c1.Close(); c2.Close();
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task TerminateAllSessions_OnEmptyListener_ReturnsZero()
    {
        var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            NullLoggerFactory.Instance,
            sessionOptions: new FixpSessionOptions
            {
                HeartbeatIntervalMs = 60_000,
                IdleTimeoutMs = 60_000,
                TestRequestGraceMs = 60_000,
                SuspendedTimeoutMs = 0,
            });
        listener.Start();
        try
        {
            Assert.Equal(0, listener.TerminateAllSessions("daily-reset"));
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }
}
