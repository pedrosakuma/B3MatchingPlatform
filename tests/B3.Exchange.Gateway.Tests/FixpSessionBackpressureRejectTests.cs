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
/// Issue #153 — when the per-channel ChannelDispatcher inbound queue is
/// full, <c>IInboundCommandSink.Enqueue*</c> returns <c>false</c> and the
/// gateway must surface that backpressure to the offending peer as a
/// <c>BusinessMessageReject(reason=SystemBusy=8)</c>, NOT silently drop
/// the message and NOT terminate the session. Counters
/// (<c>exch_dispatch_queue_full_total</c>) are bumped by the dispatcher
/// itself; this test only locks in the wire-level behaviour.
/// </summary>
public class FixpSessionBackpressureRejectTests
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
    /// Build a stand-in fixed block whose first 20 bytes are a well-formed
    /// InboundBusinessHeader (sessionID@0, msgSeqNum@4). The
    /// WriteSystemBusyReject helper only reads msgSeqNum@4 to populate
    /// refSeqNum, so the rest of the body is irrelevant.
    /// </summary>
    private static byte[] BuildFixedBlock(uint sessionId, uint msgSeqNum)
    {
        var fb = new byte[82];
        BinaryPrimitives.WriteUInt32LittleEndian(fb.AsSpan(0, 4), sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(fb.AsSpan(4, 4), msgSeqNum);
        return fb;
    }

    [Fact]
    public async Task Dispatcher_backpressure_emits_BusinessMessageReject_SystemBusy_8_and_keeps_session_open()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = new FixpSession(
                connectionId: 1, enteringFirm: 7, sessionId: 100,
                stream: server, sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance);
            session.Start();
            session.ApplyTransition(FixpEvent.Negotiate);
            session.ApplyTransition(FixpEvent.Establish);

            var fb = BuildFixedBlock(sessionId: 100, msgSeqNum: 77);
            session.WriteSystemBusyReject(
                templateId: EntryPointFrameReader.TidSimpleNewOrder,
                fixedBlock: fb,
                clOrdId: 4242UL,
                workKindLabel: "NewOrder");

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
            // OutboundBusinessHeader: sessionID@0..4 + msgSeqNum@4..4 +
            // sendingTime@8..8 + eventIndicator@16 + marketSegmentID@17.
            // BMR payload at body[18]: refMsgType(byte) + padding +
            // refSeqNum(uint32 @20) + businessRejectRefId(ulong @24) +
            // businessRejectReason(uint32 @32).
            byte refMsgType = bodyBuf[18];
            uint refSeqNum = BinaryPrimitives.ReadUInt32LittleEndian(bodyBuf.AsSpan(20, 4));
            ulong businessRejectRefId = BinaryPrimitives.ReadUInt64LittleEndian(bodyBuf.AsSpan(24, 8));
            uint businessRejectReason = BinaryPrimitives.ReadUInt32LittleEndian(bodyBuf.AsSpan(32, 4));
            // SimpleNewOrder schema MessageType byte is 15.
            Assert.Equal((byte)15, refMsgType);
            Assert.Equal(77u, refSeqNum);
            Assert.Equal(4242UL, businessRejectRefId);
            // Reason 8 = SystemBusy (FIX 4.4 "Application not available").
            Assert.Equal(8u, businessRejectReason);

            // Session must remain OPEN — backpressure is a transient
            // business-layer condition, never grounds for Terminate.
            Assert.True(session.IsOpen);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }
}
