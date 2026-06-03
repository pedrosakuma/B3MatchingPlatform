using B3.Exchange.Contracts;
using System.Net;
using System.Net.Sockets;
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
        public bool EnqueueNewOrder(in NewOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue) { return true; }
        public bool EnqueueCancel(in CancelOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { return true; }
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { return true; }
        public bool EnqueueCross(in CrossOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { return true; }
        public bool EnqueueMassCancel(in MassCancelCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { return true; }
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
    public async Task TryReattach_resets_suspend_guard_allowing_a_second_suspend_cycle()
    {
        // Regression for the #496 follow-up: SuspendLocked's one-shot
        // _suspendInProgress guard was never cleared on a successful
        // suspend, and TryReattach did not reset it — so a SECOND
        // disconnect after any reattach silently no-opped the suspend,
        // wedging the session Established over a dead transport. Reattach
        // must begin a fresh suspend cycle.
        var session = await NewSuspendedSessionAsync();
        try
        {
            var (_, serverStream, client) = await ConnectPairAsync();
            try
            {
                Assert.True(session.TryReattach(serverStream));
                // Drive the state machine back to Established as a real
                // Establish replay would, so the second Suspend has an
                // Established → Suspended transition available.
                session.ApplyTransition(FixpEvent.Establish);
                Assert.Equal(FixpState.Established, session.State);

                // Second suspend cycle must actually demote the session.
                session.Suspend("second-cycle");
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
    public async Task SuspendForTakeover_does_not_arm_cancel_on_disconnect()
    {
        // Issue #496: an Establish-path takeover demotes a still-Established
        // session whose successor transport has already arrived, so CoD must
        // NOT be armed (the disconnect is immediately superseded and resting
        // orders must survive). A reattach must then succeed.
        var (_, serverStream0, client0) = await ConnectPairAsync();
        var session = new FixpSession(
            connectionId: 1, enteringFirm: 7, sessionId: 100,
            stream: serverStream0, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance);
        try
        {
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);
            // Opt the session into cancel-on-disconnect with a zero window so
            // the normal Suspend path would fire CoD essentially immediately.
            session.SetCancelOnDisconnectForTest(
                B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType.CANCEL_ON_DISCONNECT_OR_TERMINATE,
                codTimeoutWindowMs: 0);

            session.SuspendForTakeover("establish-takeover:test");
            Assert.Equal(FixpState.Suspended, session.State);

            var (_, serverStream1, client1) = await ConnectPairAsync();
            try
            {
                Assert.True(session.TryReattach(serverStream1));
                Assert.True(session.IsAttached);
            }
            finally { client1.Close(); }
        }
        finally
        {
            client0.Close();
            session.Close("test-cleanup");
            await session.DisposeAsync();
        }
    }
}
