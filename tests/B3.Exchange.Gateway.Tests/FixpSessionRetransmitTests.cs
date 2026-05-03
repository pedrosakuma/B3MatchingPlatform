using B3.EntryPoint.Wire;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Core;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// End-to-end-ish tests for issue #46: the FixpSession buffers business
/// frames it sends and serves <c>RetransmitRequest</c>s with
/// <c>Retransmission</c> + replays-with-PossResend + a trailing
/// <c>Sequence</c>, or a <c>RetransmitReject</c> on validation failure.
/// </summary>
public class FixpSessionRetransmitTests
{
    private sealed class NoOpEngineSink : IInboundCommandSink
    {
        public void EnqueueNewOrder(in NewOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue) { }
        public void EnqueueCancel(in CancelOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { }
        public void EnqueueReplace(in ReplaceOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, ulong origClOrdIdValue) { }
        public void EnqueueCross(in CrossOrderCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { }
        public void EnqueueMassCancel(in MassCancelCommand cmd, B3.Exchange.Contracts.SessionId session, uint enteringFirm) { }
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

    private static byte[] BuildRetransmitRequest(uint sessionId, ulong timestampNanos, uint fromSeqNo, uint count)
    {
        const int total = EntryPointFrameReader.WireHeaderSize + 20;
        var f = new byte[total];
        EntryPointFrameReader.WriteHeader(f, messageLength: (ushort)total,
            blockLength: 20, templateId: EntryPointFrameReader.TidRetransmitRequest, version: 0);
        var body = f.AsSpan(EntryPointFrameReader.WireHeaderSize, 20);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), timestampNanos);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(12, 4), fromSeqNo);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(16, 4), count);
        return f;
    }

    private static OrderAcceptedEvent NewER(uint rptSeq, long orderId)
        => new(SecurityId: 1234, OrderId: orderId, ClOrdId: orderId.ToString(),
            Side: Side.Buy, PriceMantissa: 100_0000, RemainingQuantity: 10,
            EnteringFirm: 7, InsertTimestampNanos: 1_000_000UL * rptSeq, RptSeq: rptSeq);

    private static async Task<(ushort templateId, byte[] frame)> ReadOneFrameAsync(NetworkStream s, CancellationToken ct)
    {
        var hdr = new byte[EntryPointFrameReader.WireHeaderSize];
        int total = 0;
        while (total < hdr.Length)
        {
            int n = await s.ReadAsync(hdr.AsMemory(total), ct);
            if (n == 0) throw new EndOfStreamException();
            total += n;
        }
        if (!EntryPointFrameReader.TryParseInboundHeader(hdr, out var info, out _, out _))
        {
            // Outbound templates aren't recognized inbound — parse manually.
            ushort messageLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(0, 2));
            ushort tid = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(6, 2));
            int bodyLen = messageLen - EntryPointFrameReader.WireHeaderSize;
            var body = new byte[bodyLen];
            int btotal = 0;
            while (btotal < bodyLen)
            {
                int n = await s.ReadAsync(body.AsMemory(btotal), ct);
                if (n == 0) throw new EndOfStreamException();
                btotal += n;
            }
            var full = new byte[messageLen];
            Buffer.BlockCopy(hdr, 0, full, 0, hdr.Length);
            Buffer.BlockCopy(body, 0, full, hdr.Length, body.Length);
            return (tid, full);
        }
        var bbody = new byte[info.BodyLength];
        int t2 = 0;
        while (t2 < info.BodyLength)
        {
            int n = await s.ReadAsync(bbody.AsMemory(t2), ct);
            if (n == 0) throw new EndOfStreamException();
            t2 += n;
        }
        var fl = new byte[info.MessageLength];
        Buffer.BlockCopy(hdr, 0, fl, 0, hdr.Length);
        Buffer.BlockCopy(bbody, 0, fl, hdr.Length, bbody.Length);
        return (info.TemplateId, fl);
    }

    private static FixpSession NewEstablishedSession(NetworkStream serverSide, uint sessionId = 100)
    {
        var session = new FixpSession(
            connectionId: 1, enteringFirm: 7, sessionId: sessionId,
            stream: serverSide, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance);
        session.Start();
        session.ApplyTransition(FixpEvent.Negotiate);
        session.ApplyTransition(FixpEvent.Establish);
        return session;
    }

    [Fact]
    public async Task Replay_returns_Retransmission_then_clones_with_PossResend_then_Sequence()
    {
        var (serverSide, client) = await ConnectPairAsync();
        try
        {
            var session = NewEstablishedSession(serverSide);
            // Produce 3 ER_New frames → seqs 1,2,3.
            session.WriteExecutionReportNew(NewER(1, 1001));
            session.WriteExecutionReportNew(NewER(2, 1002));
            session.WriteExecutionReportNew(NewER(3, 1003));

            var clientStream = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Drain the 3 live ER frames.
            for (int i = 0; i < 3; i++)
            {
                var (tid, frame) = await ReadOneFrameAsync(clientStream, cts.Token);
                Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, tid);
                // Live frames must NOT have PossResend.
                Assert.Equal(0, frame[RetransmitBuffer.EventIndicatorAbsoluteOffset] & RetransmitBuffer.PossResendBit);
            }

            // Send RetransmitRequest(fromSeq=2, count=2).
            var req = BuildRetransmitRequest(sessionId: 100, timestampNanos: 12345UL,
                fromSeqNo: 2, count: 2);
            await clientStream.WriteAsync(req, cts.Token);

            // Expect: Retransmission header → 2 clones → trailing Sequence.
            var (tid1, retxHdr) = await ReadOneFrameAsync(clientStream, cts.Token);
            Assert.Equal(EntryPointFrameReader.TidRetransmission, tid1);
            // nextSeqNo (offset 24) == 2; count (offset 28) == 2.
            Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(retxHdr.AsSpan(24, 4)));
            Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(retxHdr.AsSpan(28, 4)));

            for (int i = 0; i < 2; i++)
            {
                var (tid2, clone) = await ReadOneFrameAsync(clientStream, cts.Token);
                Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, tid2);
                Assert.Equal(RetransmitBuffer.PossResendBit,
                    clone[RetransmitBuffer.EventIndicatorAbsoluteOffset] & RetransmitBuffer.PossResendBit);
            }

            var (tid3, seqFrame) = await ReadOneFrameAsync(clientStream, cts.Token);
            Assert.Equal(EntryPointFrameReader.TidSequence, tid3);
            // Sequence.nextSeqNo == 4 (next live seq we'll allocate).
            Assert.Equal(4u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(seqFrame.AsSpan(EntryPointFrameReader.WireHeaderSize, 4)));

            session.Close("test");
        }
        finally
        {
            client.Close();
            serverSide.Dispose();
        }
    }

    [Fact]
    public async Task RetransmitReject_when_fromSeqNo_above_window()
    {
        var (serverSide, client) = await ConnectPairAsync();
        try
        {
            var session = NewEstablishedSession(serverSide);
            session.WriteExecutionReportNew(NewER(1, 1001));

            var clientStream = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Drain the 1 live ER.
            await ReadOneFrameAsync(clientStream, cts.Token);

            var req = BuildRetransmitRequest(sessionId: 100, timestampNanos: 1UL,
                fromSeqNo: 99, count: 1);
            await clientStream.WriteAsync(req, cts.Token);

            var (tid, frame) = await ReadOneFrameAsync(clientStream, cts.Token);
            Assert.Equal(EntryPointFrameReader.TidRetransmitReject, tid);
            Assert.Equal((byte)B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_FROMSEQNO, frame[24]);

            session.Close("test");
        }
        finally
        {
            client.Close();
            serverSide.Dispose();
        }
    }

    [Fact]
    public async Task RetransmitReject_when_session_id_mismatch_returns_INVALID_SESSION()
    {
        var (serverSide, client) = await ConnectPairAsync();
        try
        {
            var session = NewEstablishedSession(serverSide, sessionId: 100);
            session.WriteExecutionReportNew(NewER(1, 1001));

            var clientStream = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ReadOneFrameAsync(clientStream, cts.Token);

            // Wrong sessionId in request body.
            var req = BuildRetransmitRequest(sessionId: 999, timestampNanos: 1UL,
                fromSeqNo: 1, count: 1);
            await clientStream.WriteAsync(req, cts.Token);

            var (tid, frame) = await ReadOneFrameAsync(clientStream, cts.Token);
            Assert.Equal(EntryPointFrameReader.TidRetransmitReject, tid);
            Assert.Equal((byte)B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_SESSION, frame[24]);

            session.Close("test");
        }
        finally
        {
            client.Close();
            serverSide.Dispose();
        }
    }

    [Fact]
    public async Task RetransmitReject_when_timestamp_zero_returns_INVALID_TIMESTAMP()
    {
        var (serverSide, client) = await ConnectPairAsync();
        try
        {
            var session = NewEstablishedSession(serverSide);
            session.WriteExecutionReportNew(NewER(1, 1001));

            var clientStream = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ReadOneFrameAsync(clientStream, cts.Token);

            var req = BuildRetransmitRequest(sessionId: 100, timestampNanos: 0UL,
                fromSeqNo: 1, count: 1);
            await clientStream.WriteAsync(req, cts.Token);

            var (tid, frame) = await ReadOneFrameAsync(clientStream, cts.Token);
            Assert.Equal(EntryPointFrameReader.TidRetransmitReject, tid);
            Assert.Equal((byte)B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_TIMESTAMP, frame[24]);

            session.Close("test");
        }
        finally
        {
            client.Close();
            serverSide.Dispose();
        }
    }

    [Fact]
    public async Task RetransmitReject_when_count_zero_returns_INVALID_COUNT()
    {
        var (serverSide, client) = await ConnectPairAsync();
        try
        {
            var session = NewEstablishedSession(serverSide);
            session.WriteExecutionReportNew(NewER(1, 1001));

            var clientStream = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ReadOneFrameAsync(clientStream, cts.Token);

            var req = BuildRetransmitRequest(sessionId: 100, timestampNanos: 1UL,
                fromSeqNo: 1, count: 0);
            await clientStream.WriteAsync(req, cts.Token);

            var (tid, frame) = await ReadOneFrameAsync(clientStream, cts.Token);
            Assert.Equal(EntryPointFrameReader.TidRetransmitReject, tid);
            Assert.Equal((byte)B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_COUNT, frame[24]);

            session.Close("test");
        }
        finally
        {
            client.Close();
            serverSide.Dispose();
        }
    }

    [Fact]
    public async Task RetransmitReject_when_count_exceeds_max_returns_REQUEST_LIMIT_EXCEEDED()
    {
        var (serverSide, client) = await ConnectPairAsync();
        try
        {
            var session = NewEstablishedSession(serverSide);
            session.WriteExecutionReportNew(NewER(1, 1001));

            var clientStream = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ReadOneFrameAsync(clientStream, cts.Token);

            var req = BuildRetransmitRequest(sessionId: 100, timestampNanos: 1UL,
                fromSeqNo: 1, count: 1001);
            await clientStream.WriteAsync(req, cts.Token);

            var (tid, frame) = await ReadOneFrameAsync(clientStream, cts.Token);
            Assert.Equal(EntryPointFrameReader.TidRetransmitReject, tid);
            Assert.Equal((byte)B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.REQUEST_LIMIT_EXCEEDED, frame[24]);

            session.Close("test");
        }
        finally
        {
            client.Close();
            serverSide.Dispose();
        }
    }

    [Fact]
    public async Task RetransmitReject_when_send_queue_too_small_returns_SYSTEM_BUSY()
    {
        var (serverSide, client) = await ConnectPairAsync();
        try
        {
            // Tiny send queue so a 3-frame replay block (header + 1 + trailer)
            // can't fit if even one slot is occupied; we don't drain the
            // 3 ER frames before the request, so depth >= 3 > capacity.
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 7, sessionId: 100,
                stream: serverSide, sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance,
                sendQueueCapacity: 4);
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);

            // Pre-fill the send queue without draining (don't read clientStream
            // yet — backpressure pins frames in the queue).
            session.WriteExecutionReportNew(NewER(1, 1001));
            session.WriteExecutionReportNew(NewER(2, 1002));
            session.WriteExecutionReportNew(NewER(3, 1003));
            session.WriteExecutionReportNew(NewER(4, 1004));

            var clientStream = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Request a 3-message replay; with depth 4 + needed (2+3=5) > capacity 4
            // the gateway must respond SYSTEM_BUSY before any block is enqueued.
            var req = BuildRetransmitRequest(sessionId: 100, timestampNanos: 1UL,
                fromSeqNo: 1, count: 3);
            await clientStream.WriteAsync(req, cts.Token);

            // Drain enough frames until we see the RetransmitReject.
            for (int i = 0; i < 10; i++)
            {
                var (tid, frame) = await ReadOneFrameAsync(clientStream, cts.Token);
                if (tid == EntryPointFrameReader.TidRetransmitReject)
                {
                    Assert.Equal((byte)B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.SYSTEM_BUSY, frame[24]);
                    session.Close("test");
                    return;
                }
            }
            Assert.Fail("did not observe RetransmitReject(SYSTEM_BUSY)");
        }
        finally
        {
            client.Close();
            serverSide.Dispose();
        }
    }
}
