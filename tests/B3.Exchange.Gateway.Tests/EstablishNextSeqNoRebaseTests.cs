using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// #239 (sub-issue 2): the gateway used to ignore peer-declared
/// <c>Establish.NextSeqNo</c>, treating every fresh session as
/// "expecting MsgSeqNum=1". B3.EntryPoint.Client 0.8.0 carries an
/// SDK-internal seq counter across reconnects (e.g. NextSeqNo=15
/// on the first business send), which made the gateway emit a
/// <c>NotApplied(1, 14)</c> followed by — historically — closing
/// the session.
///
/// FIXP §4.5.3: <c>Establish.NextSeqNo</c> is the next inbound
/// business <c>MsgSeqNum</c> the peer will send. After acceptance,
/// the gateway MUST rebase its expected-next counter so the very
/// first business message at <c>NextSeqNo</c> is in-order.
/// </summary>
public class EstablishNextSeqNoRebaseTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue) => true;
        public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) => true;
        public bool EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm) => true;
        public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm) => true;
        public void OnDecodeError(SessionId session, string error) { }
        public void OnSessionClosed(SessionId session) { }
    }

    private static async Task<(NetworkStream serverSide, TcpClient client)> ConnectPairAsync()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var client = new TcpClient();
        var connectTask = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)tcp.LocalEndpoint).Port);
        var serverSock = await tcp.AcceptSocketAsync();
        await connectTask;
        tcp.Stop();
        return (new NetworkStream(serverSock, ownsSocket: true), client);
    }

    private static byte[] BuildBody(uint headerSessionId, uint headerMsgSeqNum, int totalLen)
    {
        var body = new byte[totalLen];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), headerSessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), headerMsgSeqNum);
        return body;
    }

    private static FixpSession NewStartedSession(NetworkStream server)
    {
        var session = new FixpSession(
            connectionId: 1, enteringFirm: 7, sessionId: 100,
            stream: server, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance);
        session.Start();
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        return session;
    }

    private static async Task<byte[]?> TryReadFrameAsync(NetworkStream ns, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var head = new byte[EntryPointFrameReader.WireHeaderSize];
        try
        {
            int read = 0;
            while (read < head.Length)
            {
                int n = await ns.ReadAsync(head.AsMemory(read), cts.Token);
                if (n <= 0) return null;
                read += n;
            }
            ushort msgLen = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0, 2));
            int bodyLen = msgLen - EntryPointFrameReader.WireHeaderSize;
            var body = new byte[bodyLen];
            read = 0;
            while (read < bodyLen)
            {
                int n = await ns.ReadAsync(body.AsMemory(read), cts.Token);
                if (n <= 0) return null;
                read += n;
            }
            var full = new byte[msgLen];
            head.CopyTo(full, 0);
            body.CopyTo(full, head.Length);
            return full;
        }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>
    /// After Establish rebases <c>LastIncomingSeqNo</c> to 14, an
    /// inbound message at MsgSeqNum=15 is in-order: no NotApplied
    /// is emitted and the session advances cleanly.
    /// </summary>
    [Fact]
    public async Task FirstBusinessMessage_AtRebasedNextSeqNo_IsAcceptedWithoutNotApplied()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewStartedSession(server);
            // Mimic ProcessEstablish: peer signaled NextSeqNo=15 →
            // gateway rebases LastIncomingSeqNo = 14.
            session.LastIncomingSeqNo = 14;

            var body = BuildBody(100, headerMsgSeqNum: 15, totalLen: 82);
            Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(body));
            Assert.Equal(15u, session.LastIncomingSeqNo);

            var frame = await TryReadFrameAsync(client.GetStream(), TimeSpan.FromMilliseconds(150));
            Assert.Null(frame); // no NotApplied — message was in-order
            session.Close("test");
        }
        finally
        {
            client.Dispose();
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// Without rebase, the same inbound at MsgSeqNum=15 would
    /// trigger NotApplied(1, 14) — pin the legacy behavior so a
    /// future regression that drops the rebase is caught loudly.
    /// </summary>
    [Fact]
    public async Task FirstBusinessMessage_WithoutRebase_StillEmitsNotApplied()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewStartedSession(server);
            // No rebase: LastIncomingSeqNo defaults to 0.

            var body = BuildBody(100, headerMsgSeqNum: 15, totalLen: 82);
            Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(body));

            var frame = await TryReadFrameAsync(client.GetStream(), TimeSpan.FromMilliseconds(250));
            Assert.NotNull(frame); // NotApplied was emitted
            session.Close("test");
        }
        finally
        {
            client.Dispose();
            await server.DisposeAsync();
        }
    }
}
