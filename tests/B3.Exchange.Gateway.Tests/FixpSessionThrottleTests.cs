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
/// Issue #56 / GAP-20 — per-session inbound sliding-window throttle
/// (guidelines §4.9). Two parameters: <c>timeWindowMs</c> and
/// <c>maxMessages</c>. On violation the session emits
/// <c>BusinessMessageReject(text="Throttle limit exceeded")</c> and stays
/// open. FIXP session-layer messages (Sequence, Negotiate, Establish,
/// RetransmitRequest) bypass the throttle.
/// </summary>
public class FixpSessionThrottleTests
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

    [Fact]
    public void Throttle_ringbuffer_accepts_within_cap_and_rejects_burst()
    {
        long now = 1_000_000;
        var t = new InboundThrottle(maxMessages: 5, timeWindowMs: 1_000, nowMs: () => now);

        for (int i = 0; i < 5; i++)
            Assert.True(t.TryAccept(), $"msg {i} should be accepted within cap");
        Assert.False(t.TryAccept());
        Assert.False(t.TryAccept());
        Assert.Equal(5, t.Accepted);
        Assert.Equal(2, t.Rejected);
    }

    [Fact]
    public void Throttle_ringbuffer_window_slides_forward()
    {
        long now = 0;
        var t = new InboundThrottle(maxMessages: 3, timeWindowMs: 1_000, nowMs: () => now);

        Assert.True(t.TryAccept());
        Assert.True(t.TryAccept());
        Assert.True(t.TryAccept());
        Assert.False(t.TryAccept());

        now = 1_500;
        Assert.True(t.TryAccept());
        Assert.True(t.TryAccept());
        Assert.True(t.TryAccept());
        Assert.False(t.TryAccept());
    }

    [Fact]
    public void Throttle_reject_does_not_consume_a_slot()
    {
        long now = 0;
        var t = new InboundThrottle(maxMessages: 2, timeWindowMs: 1_000, nowMs: () => now);

        Assert.True(t.TryAccept());
        Assert.True(t.TryAccept());
        now = 500;
        Assert.False(t.TryAccept());
        Assert.False(t.TryAccept());
        now = 1_000;
        Assert.True(t.TryAccept());
    }

    [Fact]
    public void Throttle_reset_clears_window_but_preserves_lifetime_counters()
    {
        long now = 0;
        var t = new InboundThrottle(maxMessages: 2, timeWindowMs: 1_000, nowMs: () => now);
        Assert.True(t.TryAccept());
        Assert.True(t.TryAccept());
        Assert.False(t.TryAccept());
        Assert.Equal(2, t.Accepted);
        Assert.Equal(1, t.Rejected);

        t.Reset();
        Assert.True(t.TryAccept());
        Assert.True(t.TryAccept());
        Assert.False(t.TryAccept());
        Assert.Equal(4, t.Accepted);
        Assert.Equal(2, t.Rejected);
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

    private static byte[] BuildFixedBlock(uint sessionId, uint msgSeqNum, ulong clOrdId)
    {
        var fb = new byte[82];
        BinaryPrimitives.WriteUInt32LittleEndian(fb.AsSpan(0, 4), sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(fb.AsSpan(4, 4), msgSeqNum);
        BinaryPrimitives.WriteUInt64LittleEndian(fb.AsSpan(20, 8), clOrdId);
        return fb;
    }

    private static async Task<(ushort tid, byte[] body)> ReadOneFrameAsync(NetworkStream ns, CancellationToken ct)
    {
        var head = new byte[EntryPointFrameReader.WireHeaderSize];
        int read = 0;
        while (read < head.Length)
        {
            int n = await ns.ReadAsync(head.AsMemory(read), ct);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        ushort msgLen = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0, 2));
        ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(
            head.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        int bodyLen = msgLen - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLen];
        read = 0;
        while (read < bodyLen)
        {
            int n = await ns.ReadAsync(body.AsMemory(read), ct);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return (tid, body);
    }

    private static FixpSession NewSession(NetworkStream server, FixpSessionOptions options, Func<long> nowMs)
    {
        var s = new FixpSession(
            connectionId: 1, enteringFirm: 7, sessionId: 100,
            stream: server, sink: new NoOpEngineSink(),
            logger: NullLogger<FixpSession>.Instance,
            options: options,
            nowMs: nowMs);
        s.Start();
        s.ApplyTransition(FixpEvent.Negotiate);
        s.ApplyTransition(FixpEvent.Establish);
        return s;
    }

    [Fact]
    public async Task SmokeTest_WriteBmr_directly()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            var session = NewSession(server,
                new FixpSessionOptions { ThrottleTimeWindowMs = 1_000, ThrottleMaxMessages = 2 },
                () => 1_000_000L);
            bool ok = session.WriteBusinessMessageReject(15, 99, 12345, 0, "Throttle limit exceeded");
            Assert.True(ok);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ns = client.GetStream();
            var (tid, body) = await ReadOneFrameAsync(ns, cts.Token);
            Assert.Equal(EntryPointFrameReader.TidBusinessMessageReject, tid);
            byte refMsgType = body[18];
            uint refSeqNum = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20, 4));
            ulong refId = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(24, 8));
            uint reason = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(32, 4));
            Assert.Equal((byte)15, refMsgType);
            Assert.Equal(99u, refSeqNum);
            Assert.Equal(12345UL, refId);
            Assert.Equal(0u, reason);
            int trailerStart = 36;
            byte memoLen = body[trailerStart];
            byte textLen = body[trailerStart + 1];
            Assert.Equal(0, memoLen);
            string text = System.Text.Encoding.ASCII.GetString(body, trailerStart + 2, textLen);
            Assert.Equal("Throttle limit exceeded", text);
            session.Close("test");
        }
        finally { client.Close(); server.Dispose(); }
    }

    [Fact]
    public async Task Burst_within_cap_no_BMR_emitted()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            long now = 1_000_000;
            var metrics = new B3.Exchange.Contracts.ThrottleMetrics();
            var session = NewSession(server,
                new FixpSessionOptions { ThrottleTimeWindowMs = 1_000, ThrottleMaxMessages = 3, ThrottleMetrics = metrics },
                () => now);

            for (int i = 0; i < 3; i++)
            {
                var fb = BuildFixedBlock(100, (uint)(i + 1), (ulong)(100 + i));
                Assert.True(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder, fb));
            }
            Assert.Equal(3, metrics.Accepted);
            Assert.Equal(0, metrics.Rejected);
            Assert.True(session.IsOpen);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }

    [Fact]
    public async Task Burst_over_cap_rejects_and_session_stays_open()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            long now = 1_000_000;
            var metrics = new B3.Exchange.Contracts.ThrottleMetrics();
            var session = NewSession(server,
                new FixpSessionOptions { ThrottleTimeWindowMs = 1_000, ThrottleMaxMessages = 2, ThrottleMetrics = metrics },
                () => now);

            Assert.True(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(100, 1, 7000)));
            Assert.True(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(100, 2, 7001)));
            Assert.False(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(100, 3, 7002)));
            Assert.False(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(100, 4, 7003)));

            Assert.Equal(2, metrics.Accepted);
            Assert.Equal(2, metrics.Rejected);
            Assert.True(session.IsOpen);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }

    [Fact]
    public async Task Max_order_rate_plus_one_within_one_second_rejects_exactly_one_with_BMR_other()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            long now = 1_000_000;
            var metrics = new B3.Exchange.Contracts.ThrottleMetrics();
            var session = NewSession(server,
                new FixpSessionOptions { MaxOrderRatePerSecond = 2, ThrottleMetrics = metrics },
                () => now);

            Assert.True(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidNewOrderSingle,
                BuildFixedBlock(100, 1, 9000)));
            Assert.True(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidOrderCancelReplaceRequest,
                BuildFixedBlock(100, 2, 9001)));
            Assert.False(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidOrderCancelRequest,
                BuildFixedBlock(100, 3, 9002)));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var (tid, body) = await ReadOneFrameAsync(client.GetStream(), cts.Token);
            Assert.Equal(EntryPointFrameReader.TidBusinessMessageReject, tid);
            Assert.Equal((byte)19, body[18]);
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20, 4)));
            Assert.Equal(9002UL, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(24, 8)));
            Assert.Equal(BusinessMessageRejectEncoder.Reason.Other,
                BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(32, 4)));
            Assert.Equal(2, metrics.Accepted);
            Assert.Equal(1, metrics.Rejected);
            Assert.True(session.IsOpen);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }

    [Fact]
    public async Task Window_slides_forward_so_more_messages_admitted_after_time_passes()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            long now = 1_000_000;
            var metrics = new B3.Exchange.Contracts.ThrottleMetrics();
            var session = NewSession(server,
                new FixpSessionOptions { ThrottleTimeWindowMs = 1_000, ThrottleMaxMessages = 2, ThrottleMetrics = metrics },
                () => now);

            Assert.True(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(100, 1, 1000)));
            Assert.True(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(100, 2, 1001)));
            Assert.False(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(100, 3, 1002)));

            now = 1_002_000;
            Assert.True(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(100, 4, 2000)));

            Assert.Equal(3, metrics.Accepted);
            Assert.Equal(1, metrics.Rejected);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }

    [Fact]
    public async Task FIXP_session_messages_bypass_throttle()
    {
        var (server, client) = await ConnectPairAsync();
        try
        {
            long now = 1_000_000;
            var metrics = new B3.Exchange.Contracts.ThrottleMetrics();
            var session = NewSession(server,
                new FixpSessionOptions { ThrottleTimeWindowMs = 1_000, ThrottleMaxMessages = 1, ThrottleMetrics = metrics },
                () => now);

            for (int i = 0; i < 10; i++)
            {
                Assert.True(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidSequence,
                    BuildFixedBlock(100, (uint)(i + 1), 0)));
                Assert.True(session.TryAcceptInboundThrottle(EntryPointFrameReader.TidNotApplied,
                    BuildFixedBlock(100, (uint)(i + 100), 0)));
            }
            // No application templates were sent, so neither counter moves.
            Assert.Equal(0, metrics.Accepted);
            Assert.Equal(0, metrics.Rejected);
            session.Close("test");
        }
        finally
        {
            client.Close();
            server.Dispose();
        }
    }

    [Fact]
    public async Task Rate_limit_is_isolated_per_session()
    {
        var (server1, client1) = await ConnectPairAsync();
        var (server2, client2) = await ConnectPairAsync();
        try
        {
            long now = 1_000_000;
            var session1 = NewSession(server1,
                new FixpSessionOptions { MaxOrderRatePerSecond = 1 },
                () => now);
            var session2 = new FixpSession(
                connectionId: 2, enteringFirm: 7, sessionId: 200,
                stream: server2, sink: new NoOpEngineSink(),
                logger: NullLogger<FixpSession>.Instance,
                options: new FixpSessionOptions { MaxOrderRatePerSecond = 1 },
                nowMs: () => now);
            session2.Start();
            session2.ApplyTransition(FixpEvent.Negotiate);
            session2.ApplyTransition(FixpEvent.Establish);

            Assert.True(session1.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(100, 1, 1)));
            Assert.False(session1.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(100, 2, 2)));
            Assert.True(session2.TryAcceptInboundThrottle(EntryPointFrameReader.TidSimpleNewOrder,
                BuildFixedBlock(200, 1, 3)));

            session1.Close("test");
            session2.Close("test");
        }
        finally
        {
            client1.Close();
            server1.Dispose();
            client2.Close();
            server2.Dispose();
        }
    }
}
