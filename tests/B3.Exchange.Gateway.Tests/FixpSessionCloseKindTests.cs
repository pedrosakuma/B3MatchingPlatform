using System.Net;
using System.Net.Sockets;
using B3.Exchange.Contracts;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #405 (Commit 4): the <see cref="CloseKind"/> classification
/// is threaded through every <c>FixpSession</c> close path so the
/// future persistence wire-up can decide whether to erase or preserve
/// per-session state. This commit does not change behavior — only
/// surfaces the classification on <see cref="FixpSession.LastCloseKind"/>.
/// </summary>
public sealed class FixpSessionCloseKindTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId s, uint f, ulong c) => true;
        public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId s, uint f, ulong c, ulong o) => true;
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId s, uint f, ulong c, ulong o) => true;
        public bool EnqueueCross(in CrossOrderCommand cmd, SessionId s, uint f) => true;
        public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId s, uint f) => true;
        public void OnDecodeError(SessionId s, string e) { }
        public void OnSessionClosed(SessionId s) { }
    }

    private static async Task<(TcpListener tcp, NetworkStream serverSide, TcpClient client)> ConnectPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var clientTask = Task.Run(() =>
        {
            var tc = new TcpClient();
            tc.Connect(IPAddress.Loopback, endpoint.Port);
            return tc;
        });
        var server = await listener.AcceptTcpClientAsync();
        var client = await clientTask;
        return (listener, server.GetStream(), client);
    }

    private static FixpSession NewSession(NetworkStream serverSide)
    {
        return new FixpSession(
            connectionId: 1, enteringFirm: 1, sessionId: 1,
            stream: serverSide,
            sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance);
    }

    [Fact]
    public async Task LastCloseKind_starts_null_until_close()
    {
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var session = NewSession(serverSide);
            Assert.Null(session.LastCloseKind);
            session.Close("test");
            Assert.Equal(CloseKind.PeerTerminate, session.LastCloseKind);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Close_with_kind_records_kind()
    {
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var session = NewSession(serverSide);
            session.Close("graceful", CloseKind.HostShutdown);
            Assert.Equal(CloseKind.HostShutdown, session.LastCloseKind);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Close_is_idempotent_keeps_first_kind()
    {
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var session = NewSession(serverSide);
            session.Close("first", CloseKind.TransportError);
            session.Close("second", CloseKind.PeerTerminate);
            // Second call is short-circuited by the _isOpen CAS, so
            // _lastCloseKind reflects the first (winning) close.
            Assert.Equal(CloseKind.TransportError, session.LastCloseKind);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(CloseKind.PeerTerminate)]
    [InlineData(CloseKind.HostShutdown)]
    [InlineData(CloseKind.TransportError)]
    [InlineData(CloseKind.SuspendedTimeout)]
    public async Task Every_CloseKind_propagates(CloseKind kind)
    {
        var (listener, serverSide, client) = await ConnectPairAsync();
        try
        {
            var session = NewSession(serverSide);
            session.Close("test", kind);
            Assert.Equal(kind, session.LastCloseKind);
        }
        finally
        {
            client.Close();
            listener.Stop();
        }
    }
}
