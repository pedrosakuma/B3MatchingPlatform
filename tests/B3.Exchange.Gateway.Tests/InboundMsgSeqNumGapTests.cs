using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #45 (#GAP-07) — inbound MsgSeqNum tracking + NotApplied gap
/// signaling. Spec §4.5.5 / §4.6.2: the inbound flow is idempotent —
/// on a gap (received &gt; expected) the gateway accepts the new
/// message AND emits one <c>NotApplied(fromSeqNo=expected, count=N)</c>.
/// Duplicates (received &lt;= LastIncomingSeqNo) are dropped silently.
/// </summary>
public class InboundMsgSeqNumGapTests
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

    [Fact]
    public async Task FirstMessage_with_seq_1_is_accepted_no_NotApplied()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewStartedSession(server);

            var body = BuildBody(100, headerMsgSeqNum: 1, totalLen: 82);
            Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(body));
            Assert.Equal(1u, session.LastIncomingSeqNo);

            var frame = await TryReadFrameAsync(client.GetStream(), TimeSpan.FromMilliseconds(150));
            Assert.Null(frame); // no session frame should be emitted
            session.Close("test");
        }
        finally { client.Close(); server.Dispose(); }
    }

    [Fact]
    public async Task Sequential_messages_advance_LastIncomingSeqNo_and_emit_nothing()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewStartedSession(server);

            for (uint i = 1; i <= 5; i++)
                Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, i, 82)));
            Assert.Equal(5u, session.LastIncomingSeqNo);

            Assert.Null(await TryReadFrameAsync(client.GetStream(), TimeSpan.FromMilliseconds(150)));
            session.Close("test");
        }
        finally { client.Close(); server.Dispose(); }
    }

    [Fact]
    public async Task Gap_emits_one_NotApplied_with_correct_FromSeqNo_and_Count_and_advances_past_gap()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewStartedSession(server);

            // Receive 1, 2, then jump to 7 (gap of 4: missing 3,4,5,6).
            Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 1, 82)));
            Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 2, 82)));
            Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 7, 82)));
            Assert.Equal(7u, session.LastIncomingSeqNo);

            var frame = await TryReadFrameAsync(client.GetStream(), TimeSpan.FromSeconds(2));
            Assert.NotNull(frame);
            ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
            Assert.Equal(EntryPointFrameReader.TidNotApplied, tid);
            uint fromSeqNo = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(EntryPointFrameReader.WireHeaderSize, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(EntryPointFrameReader.WireHeaderSize + 4, 4));
            Assert.Equal(3u, fromSeqNo);
            Assert.Equal(4u, count);
            session.Close("test");
        }
        finally { client.Close(); server.Dispose(); }
    }

    [Fact]
    public async Task Duplicate_message_below_high_water_is_dropped_silently()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewStartedSession(server);

            Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 1, 82)));
            Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 2, 82)));
            // Replay of 1 and 2 — must be dropped silently (return false,
            // no NotApplied) and LastIncomingSeqNo must not regress.
            Assert.False(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 1, 82)));
            Assert.False(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 2, 82)));
            Assert.Equal(2u, session.LastIncomingSeqNo);

            Assert.Null(await TryReadFrameAsync(client.GetStream(), TimeSpan.FromMilliseconds(150)));
            session.Close("test");
        }
        finally { client.Close(); server.Dispose(); }
    }

    [Fact]
    public async Task Zero_msgSeqNum_is_dropped_silently_and_does_not_regress_high_water()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewStartedSession(server);

            Assert.False(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 0, 82)));
            Assert.Equal(0u, session.LastIncomingSeqNo);

            Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 1, 82)));
            Assert.False(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 0, 82)));
            Assert.Equal(1u, session.LastIncomingSeqNo);

            Assert.Null(await TryReadFrameAsync(client.GetStream(), TimeSpan.FromMilliseconds(150)));
            session.Close("test");
        }
        finally { client.Close(); server.Dispose(); }
    }

    [Fact]
    public async Task Gap_of_one_emits_NotApplied_count_1()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewStartedSession(server);

            Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 1, 82)));
            Assert.True(session.TryAcceptBusinessHeaderMsgSeqNum(BuildBody(100, 3, 82)));
            Assert.Equal(3u, session.LastIncomingSeqNo);

            var frame = await TryReadFrameAsync(client.GetStream(), TimeSpan.FromSeconds(2));
            Assert.NotNull(frame);
            uint fromSeqNo = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(EntryPointFrameReader.WireHeaderSize, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(EntryPointFrameReader.WireHeaderSize + 4, 4));
            Assert.Equal(2u, fromSeqNo);
            Assert.Equal(1u, count);
            session.Close("test");
        }
        finally { client.Close(); server.Dispose(); }
    }
}
