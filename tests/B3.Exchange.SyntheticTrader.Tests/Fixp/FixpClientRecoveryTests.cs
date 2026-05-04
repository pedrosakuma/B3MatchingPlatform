using System.Buffers.Binary;
using System.Net.Sockets;
using B3.EntryPoint.Wire;
using B3.Exchange.SyntheticTrader.Fixp;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.SyntheticTrader.Tests.Fixp;

/// <summary>
/// Recovery / failure-path tests for <see cref="FixpClient"/>
/// (issue #179, GAP-02). These exercise the post-handshake state
/// machine using <see cref="FakeFixpServer"/> to drive realistic
/// wire scenarios:
///
/// <list type="bullet">
///   <item>Inbound gap on the recoverable stream (<c>Sequence</c> with
///   higher peerNextSeq) → optional <c>RetransmitRequest</c>.</item>
///   <item>Inbound gap on a business message → <c>RetransmitRequest</c>
///   when enabled, silent advance otherwise.</item>
///   <item>Duplicate inbound business <c>msgSeqNum</c> → dropped, no
///   counter advance.</item>
///   <item>Server-initiated <c>Terminate</c> → client transitions to
///   <see cref="FixpClientState.Closed"/>.</item>
///   <item>Peer-stale detection after no inbound traffic for
///   &gt; 1.5 × keepAlive.</item>
///   <item>Re-bind after dispose: a second <c>FixpClient</c> with the
///   same SessionId picks a fresh <c>SessionVerId</c>.</item>
///   <item>Negotiate reject → <see cref="FixpClientState.Failed"/>.</item>
/// </list>
/// </summary>
public class FixpClientRecoveryTests
{
    private const string SessionId = "100";
    private const uint SessionIdNum = 100;

    private static FixpClientOptions BuildOptions(int keepAliveMs = 2000, bool retransmit = false) => new()
    {
        SessionId = SessionId,
        EnteringFirm = 7,
        KeepAliveIntervalMillis = (uint)keepAliveMs,
        CancelOnDisconnect = false,
        RetransmitOnGap = retransmit,
        HandshakeTimeout = TimeSpan.FromSeconds(5),
    };

    private static async Task<(TcpClient tcp, NetworkStream stream, FixpClient client)>
        ConnectAndHandshakeAsync(FakeFixpServer server, FixpClientOptions opts,
            uint serverNextSeqNo = 1, uint lastIncomingSeqNo = 0,
            CancellationToken ct = default)
    {
        var serverHandshake = server.RunHandshakeAsync(serverNextSeqNo, lastIncomingSeqNo, ct);
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync("127.0.0.1", server.Port, ct);
        var stream = tcp.GetStream();
        var client = new FixpClient(stream, opts);

        // FixpClient.NegotiateAndEstablishAsync awaits its TCS; nothing
        // pumps inbound for it. We pump manually here by reading the
        // two server response frames and dispatching them.
        var negotiateTask = client.NegotiateAndEstablishAsync(ct);
        await PumpOneInboundAsync(stream, client, ct); // NegotiateResponse
        await PumpOneInboundAsync(stream, client, ct); // EstablishAck
        await negotiateTask;
        await serverHandshake;
        return (tcp, stream, client);
    }

    private static async Task PumpOneInboundAsync(NetworkStream stream, FixpClient client, CancellationToken ct)
    {
        var hdr = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, hdr, ct);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(0, 2));
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        int bodyLen = messageLength - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLen];
        await ReadExactAsync(stream, body, ct);
        client.HandleInboundFrame(templateId, body);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read), ct);
            if (n <= 0) throw new EndOfStreamException("connection closed");
            read += n;
        }
    }

    private static byte[] EncodeSequence(uint nextSeqNo)
    {
        var buf = new byte[EntryPointFixpFrameCodec.SequenceBlock + EntryPointFrameReader.WireHeaderSize];
        EntryPointFixpFrameCodec.EncodeSequence(buf, nextSeqNo);
        return buf;
    }

    private static byte[] EncodeTerminate(uint sessionId, ulong sessionVerId, byte code)
    {
        var buf = new byte[EntryPointFixpFrameCodec.TerminateBlock + EntryPointFrameReader.WireHeaderSize];
        EntryPointFixpFrameCodec.EncodeTerminate(buf, sessionId, sessionVerId, code);
        return buf;
    }

    /// <summary>Splits a fully-framed FIXP message into (templateId, body).</summary>
    private static (ushort tid, byte[] body) Split(byte[] frame)
    {
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0, 2));
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
        int bodyLen = messageLength - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLen];
        Array.Copy(frame, EntryPointFrameReader.WireHeaderSize, body, 0, bodyLen);
        return (templateId, body);
    }

    [Fact]
    public async Task Handshake_PutsClientInEstablished()
    {
        await using var server = new FakeFixpServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (tcp, _, client) = await ConnectAndHandshakeAsync(server, BuildOptions(), ct: cts.Token);
        try
        {
            Assert.Equal(FixpClientState.Established, client.State);
            Assert.Equal(SessionIdNum, client.SessionIdNumeric);
            Assert.Equal(1u, client.NextOutboundSeqNo);
            Assert.Equal(1u, client.ExpectedInboundSeqNo);
        }
        finally { tcp.Dispose(); }
    }

    [Fact]
    public async Task NegotiateRejected_ClientTransitionsToFailed()
    {
        await using var server = new FakeFixpServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = server.RunRejectingHandshakeAsync(
            FixpSbe.NegotiationRejectCode.CREDENTIALS, cts.Token);
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync("127.0.0.1", server.Port, cts.Token);
        var stream = tcp.GetStream();
        var client = new FixpClient(stream, BuildOptions());
        try
        {
            var negotiateTask = client.NegotiateAndEstablishAsync(cts.Token);
            // Pump the NegotiateReject the server sends back.
            await PumpOneInboundAsync(stream, client, cts.Token);
            await Assert.ThrowsAsync<FixpHandshakeRejectedException>(() => negotiateTask);
            Assert.Equal(FixpClientState.Failed, client.State);
            await serverTask;
        }
        finally { tcp.Dispose(); }
    }

    [Fact]
    public async Task ServerSendsTerminate_ClientTransitionsToClosed()
    {
        await using var server = new FakeFixpServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (tcp, _, client) = await ConnectAndHandshakeAsync(server, BuildOptions(), ct: cts.Token);
        try
        {
            // Simulate the server pushing a Terminate. The embedding
            // read loop would call HandleInboundFrame(TidTerminate, ...).
            var term = EncodeTerminate(SessionIdNum, client.SessionVerId,
                (byte)FixpSbe.TerminationCode.UNSPECIFIED);
            var (tid, body) = Split(term);
            var disp = client.HandleInboundFrame(tid, body);

            Assert.Equal(InboundDisposition.PeerTerminated, disp);
            Assert.Equal(FixpClientState.Closed, client.State);
        }
        finally { tcp.Dispose(); }
    }

    [Fact]
    public async Task DuplicateInboundBusinessSeq_IsDroppedWithoutAdvancingExpected()
    {
        await using var server = new FakeFixpServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (tcp, _, client) = await ConnectAndHandshakeAsync(server, BuildOptions(), ct: cts.Token);
        try
        {
            // First message in-order.
            Assert.True(client.TrackInboundBusinessSeq(1));
            Assert.Equal(2u, client.ExpectedInboundSeqNo);

            // Replay of seq=1 → duplicate, drop, do not advance.
            Assert.False(client.TrackInboundBusinessSeq(1));
            Assert.Equal(2u, client.ExpectedInboundSeqNo);

            // Next in-order continues to advance.
            Assert.True(client.TrackInboundBusinessSeq(2));
            Assert.Equal(3u, client.ExpectedInboundSeqNo);
        }
        finally { tcp.Dispose(); }
    }

    [Fact]
    public async Task SequenceWithGap_FiresRetransmitRequest_WhenEnabled()
    {
        await using var server = new FakeFixpServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (tcp, _, client) = await ConnectAndHandshakeAsync(server,
            BuildOptions(retransmit: true), ct: cts.Token);
        try
        {
            // Server signals it's about to send seq 5 but we only have
            // expected=1. HandleInboundFrame should NOT advance expected
            // (gap fill via retransmission) and should fire a
            // RetransmitRequest async — which the server-side socket will
            // then receive.
            var seq = EncodeSequence(nextSeqNo: 5);
            var (tid, body) = Split(seq);
            client.HandleInboundFrame(tid, body);

            // Expect RetransmitRequest from client on the wire.
            var rr = await server.ReadUntilAsync(EntryPointFrameReader.TidRetransmitRequest,
                TimeSpan.FromSeconds(3));
            Assert.Equal(EntryPointFrameReader.TidRetransmitRequest, rr.TemplateId);
            // Body layout: sessionID(4) | requestTimestamp(8) | fromSeqNo(4) | count(4)
            uint fromSeqNo = BinaryPrimitives.ReadUInt32LittleEndian(rr.Body.AsSpan(12, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(rr.Body.AsSpan(16, 4));
            Assert.Equal(1u, fromSeqNo);
            Assert.Equal(4u, count);

            // Expected stays at 1 (gap not yet filled).
            Assert.Equal(1u, client.ExpectedInboundSeqNo);
        }
        finally { tcp.Dispose(); }
    }

    [Fact]
    public async Task SequenceWithGap_DoesNotFireRetransmit_WhenDisabled()
    {
        await using var server = new FakeFixpServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (tcp, _, client) = await ConnectAndHandshakeAsync(server,
            BuildOptions(retransmit: false), ct: cts.Token);
        try
        {
            var seq = EncodeSequence(nextSeqNo: 5);
            var (tid, body) = Split(seq);
            client.HandleInboundFrame(tid, body);

            // Wait briefly to ensure no RetransmitRequest gets written.
            using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => server.ReadFrameAsync(readCts.Token));

            // Expected stays at 1; the client logged-and-ignored the gap.
            Assert.Equal(1u, client.ExpectedInboundSeqNo);
        }
        finally { tcp.Dispose(); }
    }

    [Fact]
    public async Task BusinessSeqGap_FiresRetransmitRequest_WhenEnabled()
    {
        await using var server = new FakeFixpServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (tcp, _, client) = await ConnectAndHandshakeAsync(server,
            BuildOptions(retransmit: true), ct: cts.Token);
        try
        {
            // First in-order business message.
            Assert.True(client.TrackInboundBusinessSeq(1));
            // Now jump from expected=2 to seq=10 (gap of 8).
            Assert.True(client.TrackInboundBusinessSeq(10));

            var rr = await server.ReadUntilAsync(EntryPointFrameReader.TidRetransmitRequest,
                TimeSpan.FromSeconds(3));
            uint fromSeqNo = BinaryPrimitives.ReadUInt32LittleEndian(rr.Body.AsSpan(12, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(rr.Body.AsSpan(16, 4));
            Assert.Equal(2u, fromSeqNo);
            Assert.Equal(8u, count);

            // Expected was advanced past the seen seq (gap message itself
            // was accepted as in-order-ish so retransmissions fill 2..9).
            Assert.Equal(11u, client.ExpectedInboundSeqNo);
        }
        finally { tcp.Dispose(); }
    }

    [Fact]
    public async Task PeerStale_DetectedAfterInactivity()
    {
        await using var server = new FakeFixpServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Tiny keep-alive so the test is fast; threshold is 1.5×.
        var (tcp, _, client) = await ConnectAndHandshakeAsync(server,
            BuildOptions(keepAliveMs: 100), ct: cts.Token);
        try
        {
            Assert.False(client.IsPeerStale());
            // Wait > 1.5 × keepAlive without any inbound from server.
            await Task.Delay(250, cts.Token);
            Assert.True(client.IsPeerStale(),
                $"expected peer stale (LastInbound={client.LastInboundUtc:HH:mm:ss.fff}, now={DateTime.UtcNow:HH:mm:ss.fff})");
        }
        finally { tcp.Dispose(); }
    }

    [Fact]
    public async Task PeerStale_ResetByInboundFrame()
    {
        await using var server = new FakeFixpServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (tcp, _, client) = await ConnectAndHandshakeAsync(server,
            BuildOptions(keepAliveMs: 100), ct: cts.Token);
        try
        {
            await Task.Delay(250, cts.Token);
            Assert.True(client.IsPeerStale());

            // Any inbound frame should refresh the watchdog.
            var seq = EncodeSequence(nextSeqNo: 1);
            var (tid, body) = Split(seq);
            client.HandleInboundFrame(tid, body);
            Assert.False(client.IsPeerStale());
        }
        finally { tcp.Dispose(); }
    }

    [Fact]
    public async Task Rebind_PicksFreshSessionVerIdPerConnection()
    {
        await using var server1 = new FakeFixpServer();
        await using var server2 = new FakeFixpServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (tcp1, _, client1) = await ConnectAndHandshakeAsync(server1, BuildOptions(), ct: cts.Token);
        ulong ver1;
        try
        {
            ver1 = client1.SessionVerId;
            Assert.True(ver1 > 0);
        }
        finally { tcp1.Dispose(); }

        // Allow at least 1ms so the millisecond-resolution sessionVerId
        // is guaranteed to be distinct on the second connection.
        await Task.Delay(5, cts.Token);

        var (tcp2, _, client2) = await ConnectAndHandshakeAsync(server2, BuildOptions(), ct: cts.Token);
        try
        {
            Assert.NotEqual(ver1, client2.SessionVerId);
            Assert.Equal(SessionIdNum, client2.SessionIdNumeric);
            Assert.Equal(FixpClientState.Established, client2.State);
        }
        finally { tcp2.Dispose(); }
    }
}
