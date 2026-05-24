using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using B3.Exchange.Matching;
using B3.Exchange.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace B3.Exchange.Gateway.Tests;

public class BusinessHeaderSendingTimeValidationTests
{
    private const ulong NowNanos = 1_700_000_000_000_000_000UL;
    private const ulong ToleranceNanos = 5UL * 1_000_000_000UL;

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

    private static async Task<(NetworkStream ServerSide, TcpClient Client)> ConnectPairAsync()
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

    private static byte[] BuildBody(ulong sendingTime, uint msgSeqNum = 7, ulong clOrdId = 42)
    {
        var body = new byte[82];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), msgSeqNum);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8, 8), sendingTime);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(20, 8), clOrdId);
        return body;
    }

    private static FixpSession NewSession(NetworkStream server)
        => new(
            connectionId: 1,
            enteringFirm: 7,
            sessionId: 100,
            stream: server,
            sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance,
            timeSource: new FakeNanosTimeSource(NowNanos),
            options: new FixpSessionOptions { SendingTimeSkewToleranceNs = ToleranceNanos });

    private static async Task<(byte RefMsgType, uint RefSeqNum, ulong RefId, uint Reason)> ReadBusinessRejectAsync(TcpClient client)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var ns = client.GetStream();
        var head = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(ns, head, cts.Token);

        ushort msgLen = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0, 2));
        ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        Assert.Equal(EntryPointFrameReader.TidBusinessMessageReject, tid);

        var body = new byte[msgLen - EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(ns, body, cts.Token);

        return (
            body[18],
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20, 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(24, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(32, 4)));
    }

    private static async Task ReadExactAsync(NetworkStream ns, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            int n = await ns.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    [Fact]
    public async Task SendingTime_within_tolerance_is_accepted()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewSession(server);
            var body = BuildBody(NowNanos + ToleranceNanos);

            Assert.True(session.TryAcceptBusinessHeaderSendingTime(EntryPointFrameReader.TidSimpleNewOrder, body));
            Assert.Equal(0, client.Available);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }

    [Fact]
    public async Task Future_sendingTime_outside_tolerance_emits_BusinessMessageReject()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewSession(server);
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);
            var body = BuildBody(NowNanos + ToleranceNanos + 1, msgSeqNum: 13, clOrdId: 4242);

            Assert.False(session.TryAcceptBusinessHeaderSendingTime(EntryPointFrameReader.TidSimpleNewOrder, body));

            var bmr = await ReadBusinessRejectAsync(client);
            Assert.Equal((byte)15, bmr.RefMsgType);
            Assert.Equal(13u, bmr.RefSeqNum);
            Assert.Equal(4242UL, bmr.RefId);
            Assert.Equal(BusinessMessageRejectEncoder.Reason.Other, bmr.Reason);
            Assert.Equal(0u, session.LastIncomingSeqNo);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }

    [Fact]
    public async Task Past_sendingTime_outside_tolerance_emits_BusinessMessageReject()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewSession(server);
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);
            var body = BuildBody(NowNanos - ToleranceNanos - 1, msgSeqNum: 14, clOrdId: 4343);

            Assert.False(session.TryAcceptBusinessHeaderSendingTime(EntryPointFrameReader.TidSimpleNewOrder, body));

            var bmr = await ReadBusinessRejectAsync(client);
            Assert.Equal((byte)15, bmr.RefMsgType);
            Assert.Equal(14u, bmr.RefSeqNum);
            Assert.Equal(4343UL, bmr.RefId);
            Assert.Equal(BusinessMessageRejectEncoder.Reason.Other, bmr.Reason);
            Assert.Equal(0u, session.LastIncomingSeqNo);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }
}
