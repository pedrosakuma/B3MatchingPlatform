using System.Net;
using System.Net.Sockets;
using B3.Exchange.Core;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #69b-2: <see cref="FixpSession.TryReattach"/> must accept a
/// fresh transport only when the session is currently Suspended (and
/// open and not already attached). Negative cases must leave session
/// state untouched and return false so the listener closes the new
/// socket.
/// </summary>
public class FixpSessionReattachTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public void EnqueueNewOrder(in NewOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue) { }
        public void EnqueueCancel(in CancelOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { }
        public void EnqueueReplace(in ReplaceOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { }
        public void EnqueueCross(in CrossOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { }
        public void OnDecodeError(B3.Exchange.Contracts.SessionId session, string error) { }
        public void OnSessionClosed(B3.Exchange.Contracts.SessionId session) { }
    }

    private static async Task<(TcpListener tcp, NetworkStream serverSide, TcpClient client)> ConnectPairAsync()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var client = new TcpClient();
        var connectTask = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)tcp.LocalEndpoint).Port);
        var serverSock = await tcp.AcceptSocketAsync();
        await connectTask;
        return (tcp, new NetworkStream(serverSock, ownsSocket: true), client);
    }

    private static async Task<FixpSession> NewSuspendedSessionAsync()
    {
        var (_, serverStream, client) = await ConnectPairAsync();
        var session = new FixpSession(
            connectionId: 1, enteringFirm: 7, sessionId: 100,
            stream: serverStream, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance);
        session.Start();
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        client.Close();
        await TestUtil.WaitUntilAsync(
            () => session.State == FixpState.Suspended && session.SuspendedSinceMs is not null,
            TimeSpan.FromSeconds(3));
        Assert.Equal(FixpState.Suspended, session.State);
        return session;
    }

    [Fact]
    public async Task TryReattach_returns_false_when_not_suspended()
    {
        var (_, serverStream, client) = await ConnectPairAsync();
        try
        {
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 7, sessionId: 100,
                stream: serverStream, sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance);
            session.Start();
            // Still Idle / attached → re-attach must refuse.
            using var fakeStream = new MemoryStream();
            Assert.False(session.TryReattach(fakeStream));
            session.Close("test-cleanup");
        }
        finally { client.Close(); }
    }

    [Fact]
    public async Task TryReattach_returns_false_after_close()
    {
        var session = await NewSuspendedSessionAsync();
        session.Close("test-close");
        using var fakeStream = new MemoryStream();
        Assert.False(session.TryReattach(fakeStream));
    }

    [Fact]
    public async Task TryReattach_clears_SuspendedSinceMs_and_marks_attached()
    {
        var session = await NewSuspendedSessionAsync();
        try
        {
            // Provide a stream the new recv loop can read from; we won't
            // feed it a real Establish frame but we don't need to — we
            // only assert TryReattach's pre/post conditions. The loop
            // will block on the empty stream until we close the session.
            var (_, serverStream, client) = await ConnectPairAsync();
            try
            {
                Assert.True(session.TryReattach(serverStream));
                Assert.True(session.IsAttached);
                Assert.Null(session.SuspendedSinceMs);
                // State machine still Suspended until the recv loop
                // processes a real Establish — TryReattach itself does
                // not advance the state machine.
                Assert.Equal(FixpState.Suspended, session.State);
            }
            finally { client.Close(); }
        }
        finally
        {
            session.Close("test-cleanup");
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryReattach_concurrent_calls_only_one_wins()
    {
        var session = await NewSuspendedSessionAsync();
        try
        {
            var (_, s1, c1) = await ConnectPairAsync();
            var (_, s2, c2) = await ConnectPairAsync();
            try
            {
                using var barrier = new ManualResetEventSlim(false);
                var winners = 0;
                var t1 = Task.Run(() => { barrier.Wait(); if (session.TryReattach(s1)) Interlocked.Increment(ref winners); });
                var t2 = Task.Run(() => { barrier.Wait(); if (session.TryReattach(s2)) Interlocked.Increment(ref winners); });
                barrier.Set();
                await Task.WhenAll(t1, t2);
                Assert.Equal(1, winners);
            }
            finally { c1.Close(); c2.Close(); }
        }
        finally
        {
            session.Close("test-cleanup");
            await session.DisposeAsync();
        }
    }
}
