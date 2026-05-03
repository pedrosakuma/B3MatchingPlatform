using B3.Exchange.Contracts;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Core;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #69 (part 1 — "Suspended state plumbing"): when the underlying
/// TCP transport drops while the FIXP session is <see cref="FixpState.Established"/>,
/// the session must transition to <see cref="FixpState.Suspended"/> instead
/// of being torn down. The session must remain alive (so a future Establish
/// on a new transport — issue #69b — can re-attach) and its claim must NOT
/// be released. The legacy code path (transport drop while still
/// <see cref="FixpState.Idle"/>) must continue to fully close the session.
/// </summary>
public class FixpSessionSuspendTests
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
    /// Bring up a connected (server-side, client-side) <see cref="NetworkStream"/>
    /// pair via a loopback TCP listener so we can drive the FIXP session with
    /// real socket semantics — including <c>EOF</c> when the client side
    /// closes — without needing the full handshake encoders.
    /// </summary>
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

    [Fact]
    public async Task TransportDrop_WhileEstablished_DemotesToSuspended_NotClose()
    {
        var (tcp, serverStream, client) = await ConnectPairAsync();
        try
        {
            var sink = new NoOpEngineSink();
            int onClosedCalls = 0;
            var session = new FixpSession(
                connectionId: 42, enteringFirm: 7, sessionId: 100,
                stream: serverStream, sink: sink,
                logger: NullLogger<FixpSession>.Instance,
                onClosed: (_, _) => Interlocked.Increment(ref onClosedCalls));
            session.Start();

            // Drive state to Established without going through the full
            // handshake (test seam — ApplyTransition is internal). The
            // sequence Idle→Negotiated→Established matches what a real
            // Negotiate + Establish exchange would produce.
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);
            Assert.Equal(FixpState.Established, session.State);

            // Drop the client side: the receive loop will hit EOF and our
            // OnTransportClosed handler should demote rather than close.
            client.Close();

            // Wait for the receive loop to observe EOF and run its finally.
            await TestUtil.WaitUntilAsync(() => !session.IsAttached, TimeSpan.FromSeconds(3));

            Assert.False(session.IsAttached);
            Assert.Equal(FixpState.Suspended, session.State);
            // No Close-side hooks fired — the session is preserved.
            Assert.Equal(0, Volatile.Read(ref onClosedCalls));
            Assert.Equal(0, Volatile.Read(ref sink.SessionClosedCalls));
            // IsOpen is false because the transport is down, so any stray
            // Write*-style call early-returns instead of throwing.
            Assert.False(session.IsOpen);

            // Clean up the suspended session for the test.
            session.Close("test-cleanup");
        }
        finally
        {
            client.Dispose();
            serverStream.Dispose();
            tcp.Stop();
        }
    }

    [Fact]
    public async Task TransportDrop_WhileIdle_StillFullyCloses()
    {
        var (tcp, serverStream, client) = await ConnectPairAsync();
        try
        {
            var sink = new NoOpEngineSink();
            int onClosedCalls = 0;
            var session = new FixpSession(
                connectionId: 43, enteringFirm: 7, sessionId: 101,
                stream: serverStream, sink: sink,
                logger: NullLogger<FixpSession>.Instance,
                onClosed: (_, _) => Interlocked.Increment(ref onClosedCalls));
            session.Start();

            client.Close();

            // Wait for IsOpen to flip false AND onClosed callback to fire.
            // IsOpen becomes false the instant Close() flips _isOpen, but
            // _onClosed is invoked a few statements later — poll on the
            // callback counter to avoid a benign race.
            await TestUtil.WaitUntilAsync(
                () => !session.IsOpen && Volatile.Read(ref onClosedCalls) > 0,
                TimeSpan.FromSeconds(3));

            Assert.False(session.IsOpen);
            Assert.False(session.IsAttached);
            // State machine has no Idle→<Detach>→Suspended transition; the
            // legacy Close path runs instead, so onClosed + sink hooks fire.
            Assert.NotEqual(FixpState.Suspended, session.State);
            Assert.Equal(1, Volatile.Read(ref onClosedCalls));
            Assert.Equal(1, Volatile.Read(ref sink.SessionClosedCalls));
        }
        finally
        {
            client.Dispose();
            serverStream.Dispose();
            tcp.Stop();
        }
    }
}
