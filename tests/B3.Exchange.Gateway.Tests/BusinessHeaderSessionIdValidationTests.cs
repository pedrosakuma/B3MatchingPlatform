using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #48 (#GAP-10) — businessHeader.sessionID validation. Every inbound
/// application message must carry the negotiated SessionId; mismatches
/// must be answered with <c>BusinessMessageReject(reason=33003)</c> and
/// the offending message dropped (the session itself stays open per spec
/// §4.6.3.1).
/// </summary>
public class BusinessHeaderSessionIdValidationTests
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

    /// <summary>
    /// Build a stand-in business message body whose first 20 bytes are a
    /// well-formed <c>InboundBusinessHeader</c>. The exact template body
    /// content beyond the header is irrelevant — sessionID validation
    /// runs BEFORE the per-template decoder.
    /// </summary>
    private static byte[] BuildHeaderOnlyBody(uint headerSessionId, uint headerMsgSeqNum, ulong clOrdId, int totalLen)
    {
        var body = new byte[totalLen];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), headerSessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4, 4), headerMsgSeqNum);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8, 8), 0UL); // sendingTime
        // body[16] = eventIndicator; body[17] = marketSegmentID; both default 0.
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(20, 8), clOrdId);
        return body;
    }

    [Fact]
    public async Task Matching_sessionID_passes_validation()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 7, sessionId: 100,
                stream: server, sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance);
            // Drive to Established so the session is "open" for writes.
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);

            var body = BuildHeaderOnlyBody(headerSessionId: 100, headerMsgSeqNum: 7, clOrdId: 42, totalLen: 82);

            bool accepted = session.TryAcceptBusinessHeaderSessionId(
                EntryPointFrameReader.TidSimpleNewOrder, body);
            Assert.True(accepted);

            // No BMR should have been sent — the client side stream stays empty.
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
    public async Task Mismatched_sessionID_emits_BusinessMessageReject_33003_and_drops_frame()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 7, sessionId: /* negotiated */ 100,
                stream: server, sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance);
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);

            var body = BuildHeaderOnlyBody(headerSessionId: /* WRONG */ 999, headerMsgSeqNum: 13, clOrdId: 4242, totalLen: 82);

            bool accepted = session.TryAcceptBusinessHeaderSessionId(
                EntryPointFrameReader.TidSimpleNewOrder, body);
            Assert.False(accepted);

            // Drain the BMR frame off the client side. Layout:
            //   SOFH(4) + SBE header(8) + body(BlockLength + varData)
            //   businessHeader: sessionID@0 + msgSeqNum@4 + sendingTime@8
            //                   + eventIndicator@16 + marketSegmentID@17
            //   then BMR payload starting at body[20]:
            //     refMsgType(byte @20), refSeqNum(uint32 @22),
            //     businessRejectRefId(ulong @26), businessRejectReason(uint32 @34)
            // We only assert template-id and businessRejectReason here.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var ns = client.GetStream();
            var head = new byte[EntryPointFrameReader.WireHeaderSize];
            int read = 0;
            while (read < head.Length)
            {
                int n = await ns.ReadAsync(head.AsMemory(read), cts.Token);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
            ushort msgLen = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0, 2));
            ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
            Assert.Equal(EntryPointFrameReader.TidBusinessMessageReject, tid);

            int bodyLen = msgLen - EntryPointFrameReader.WireHeaderSize;
            var bodyBuf = new byte[bodyLen];
            read = 0;
            while (read < bodyLen)
            {
                int n = await ns.ReadAsync(bodyBuf.AsMemory(read), cts.Token);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
            // OutboundBusinessHeader: sessionID(@0,4) + msgSeqNum(@4,4) +
            // sendingTime(@8,8) + eventIndicator(@16) + marketSegmentID(@17)
            // BMR payload: refMsgType(byte @18) + padding(@19) +
            // refSeqNum(uint32 @20) + businessRejectRefId(ulong @24) +
            // businessRejectReason(uint32 @32).
            byte refMsgType = bodyBuf[18];
            uint refSeqNum = BinaryPrimitives.ReadUInt32LittleEndian(bodyBuf.AsSpan(20, 4));
            ulong businessRejectRefId = BinaryPrimitives.ReadUInt64LittleEndian(bodyBuf.AsSpan(24, 8));
            uint businessRejectReason = BinaryPrimitives.ReadUInt32LittleEndian(bodyBuf.AsSpan(32, 4));
            // GAP-14 fix: refMsgType MUST be the schema MessageType enum
            // byte (15 = SimpleNewOrder), NOT the raw templateId byte
            // (102). Locking this in so a regression to (byte)templateId
            // is caught.
            Assert.Equal((byte)15, refMsgType);
            Assert.Equal(13u, refSeqNum);
            Assert.Equal(4242UL, businessRejectRefId);
            Assert.Equal(33003u, businessRejectReason);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }
}
