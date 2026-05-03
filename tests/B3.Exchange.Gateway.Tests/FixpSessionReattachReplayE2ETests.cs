using B3.EntryPoint.Wire;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Core;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #69 acceptance test. Exercises the full
/// <c>Established → Suspended → re-attached → replay</c> flow end-to-end
/// over a real loopback socket pair:
///
/// <list type="number">
/// <item>Establish session, push N ER_New frames so the gateway both
/// emits them on the wire and stores them in the retx buffer.</item>
/// <item>Client reads only the first K (K&lt;N) ER frames and then
/// closes the TCP connection. The receive watchdog observes the EOF and
/// demotes the FixpSession to <see cref="FixpState.Suspended"/>; the
/// retx buffer and order-ownership state survive.</item>
/// <item>A new socket pair is reattached via
/// <see cref="FixpSession.TryReattach"/> and a fresh
/// <c>Establish</c> drives the state machine
/// <see cref="FixpState.Suspended"/> → <see cref="FixpState.Established"/>.</item>
/// <item>The reconnected client requests retransmission for the gap and
/// receives, in order: a <c>Retransmission</c> header, the missing ER
/// frames replayed with <c>EventIndicator.PossResend = 1</c>, and a
/// trailing <c>Sequence</c> frame whose <c>nextSeqNo</c> matches the
/// next live business seq.</item>
/// </list>
/// </summary>
public class FixpSessionReattachReplayE2ETests
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

    private static OrderAcceptedEvent NewER(uint rptSeq, long orderId)
        => new(SecurityId: 1234, OrderId: orderId, ClOrdId: orderId.ToString(),
            Side: Side.Buy, PriceMantissa: 100_0000, RemainingQuantity: 10,
            EnteringFirm: 7, InsertTimestampNanos: 1_000_000UL * rptSeq, RptSeq: rptSeq);

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

    [Fact]
    public async Task FullDropAndReconnect_DeliversAllPostDropEr_WithPossResendOnReplays()
    {
        // Phase 1: Establish, send 3 ERs (seqs 1..3), drain only the first.
        var (serverSide1, client1) = await ConnectPairAsync();
        var session = new FixpSession(
            connectionId: 1, enteringFirm: 7, sessionId: 100,
            stream: serverSide1, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance);
        try
        {
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);

            session.WriteExecutionReportNew(NewER(1, 1001));
            session.WriteExecutionReportNew(NewER(2, 1002));
            session.WriteExecutionReportNew(NewER(3, 1003));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var clientStream1 = client1.GetStream();

            var (tid0, frame0) = await ReadOneFrameAsync(clientStream1, cts.Token);
            Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, tid0);
            Assert.Equal(0, frame0[RetransmitBuffer.EventIndicatorAbsoluteOffset] & RetransmitBuffer.PossResendBit);

            // Phase 2: client drops the connection. Watchdog observes EOF
            // and SuspendLocked demotes Established → Suspended; retx
            // buffer is preserved.
            client1.Close();
            await TestUtil.WaitUntilAsync(
                () => session.State == FixpState.Suspended && session.SuspendedSinceMs is not null,
                TimeSpan.FromSeconds(5));
            Assert.Equal(FixpState.Suspended, session.State);
            Assert.Equal(3, session.RetxBufferDepth);

            // Phase 3: client reconnects on a fresh socket; gateway
            // re-attaches and the state machine flips back to Established.
            var (serverSide2, client2) = await ConnectPairAsync();
            try
            {
                Assert.True(session.TryReattach(serverSide2));
                session.ApplyTransition(FixpEvent.Establish);
                Assert.Equal(FixpState.Established, session.State);

                // Phase 4: client requests the missing range (seqs 2..3).
                // Gateway should reply with Retransmission header, the 2
                // ER clones with PossResend = 1, and a trailing Sequence
                // (nextSeqNo = 4 — next live business seq).
                var clientStream2 = client2.GetStream();
                var req = BuildRetransmitRequest(sessionId: 100, timestampNanos: 99UL,
                    fromSeqNo: 2, count: 2);
                await clientStream2.WriteAsync(req, cts.Token);

                var (tidR, retxHdr) = await ReadOneFrameAsync(clientStream2, cts.Token);
                Assert.Equal(EntryPointFrameReader.TidRetransmission, tidR);
                Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(retxHdr.AsSpan(24, 4)));
                Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(retxHdr.AsSpan(28, 4)));

                for (int i = 0; i < 2; i++)
                {
                    var (tidC, clone) = await ReadOneFrameAsync(clientStream2, cts.Token);
                    Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, tidC);
                    Assert.Equal(RetransmitBuffer.PossResendBit,
                        clone[RetransmitBuffer.EventIndicatorAbsoluteOffset] & RetransmitBuffer.PossResendBit);
                }

                var (tidS, seqFrame) = await ReadOneFrameAsync(clientStream2, cts.Token);
                Assert.Equal(EntryPointFrameReader.TidSequence, tidS);
                Assert.Equal(4u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    seqFrame.AsSpan(EntryPointFrameReader.WireHeaderSize, 4)));

                // A new live ER after the replay must NOT carry PossResend
                // and must continue from seq 4.
                session.WriteExecutionReportNew(NewER(4, 1004));
                var (tidL, live) = await ReadOneFrameAsync(clientStream2, cts.Token);
                Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, tidL);
                Assert.Equal(0, live[RetransmitBuffer.EventIndicatorAbsoluteOffset] & RetransmitBuffer.PossResendBit);
            }
            finally
            {
                client2.Close();
            }
        }
        finally
        {
            session.Close("test-cleanup");
            await session.DisposeAsync();
        }
    }
}
