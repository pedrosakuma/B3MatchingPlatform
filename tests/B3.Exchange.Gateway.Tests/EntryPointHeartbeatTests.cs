using System.Buffers.Binary;
using B3.Exchange.Contracts;
using B3.EntryPoint.Wire;
using System.Net;
using System.Net.Sockets;
using B3.Exchange.Gateway;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Integration coverage for issue #9: server-side heartbeat + idle timeout.
/// Uses a real loopback TCP connection driven by <see cref="EntryPointListener"/>
/// with sub-second intervals so the suite stays fast.
/// </summary>
public class EntryPointHeartbeatTests
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

    private static byte[] BuildSequenceFrame(uint nextSeq)
    {
        const int total = EntryPointFrameReader.WireHeaderSize + 4;
        var f = new byte[total];
        EntryPointFrameReader.WriteHeader(f, messageLength: (ushort)total,
            blockLength: 4, templateId: EntryPointFrameReader.TidSequence, version: 0);
        BitConverter.GetBytes(nextSeq).CopyTo(f, EntryPointFrameReader.WireHeaderSize);
        return f;
    }

    [Fact]
    public async Task IdleSession_IsTornDown_AfterTimeout()
    {
        var closures = new List<string>();
        var sem = new SemaphoreSlim(0, 1);
        var options = new FixpSessionOptions
        {
            HeartbeatIntervalMs = 10_000, // not relevant for this test
            IdleTimeoutMs = 200,
            TestRequestGraceMs = 200,
        };
        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            NullLoggerFactory.Instance,
            sessionOptions: options,
            onSessionClosed: (s, reason) => { lock (closures) { closures.Add(reason); } sem.Release(); });
        listener.Start();
        var ep = listener.LocalEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);

        // Don't send anything. Server should: send a probe at ~200ms, then
        // close at ~400ms. Allow generous slack so the test isn't flaky.
        bool fired = await sem.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fired, "session was never closed");
        lock (closures) Assert.Contains("idle-timeout", closures);

        // Read side of the client should observe EOF after server close.
        var ns = client.GetStream();
        byte[] buf = new byte[64];
        int total = 0;
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (true)
            {
                int n = await ns.ReadAsync(buf.AsMemory(total, buf.Length - total), readCts.Token);
                if (n == 0) break;
                total += n;
                if (total == buf.Length) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        // We should receive the Sequence probe and a protocol Terminate before EOF.
        const int expectedFrameLen = EntryPointFrameReader.WireHeaderSize + 4
            + SessionRejectEncoder.TerminateTotal;
        Assert.Equal(expectedFrameLen, total);
        // Header sanity: templateId == 9 at offset SOFH+2.
        ushort tid = BitConverter.ToUInt16(buf, EntryPointFrameReader.SofhSize + 2);
        Assert.Equal((ushort)EntryPointFrameReader.TidSequence, tid);
        ushort terminateTid = BitConverter.ToUInt16(buf,
            EntryPointFrameReader.WireHeaderSize + 4 + EntryPointFrameReader.SofhSize + 2);
        Assert.Equal((ushort)EntryPointFrameReader.TidTerminate, terminateTid);
        Assert.Equal(SessionRejectEncoder.TerminationCode.KeepaliveIntervalLapsed,
            buf[EntryPointFrameReader.WireHeaderSize + 4 + EntryPointFrameReader.WireHeaderSize + 12]);
    }

    [Fact]
    public async Task ActivelyHeartbeatingSession_StaysAlive()
    {
        var closures = new List<string>();
        var options = new FixpSessionOptions
        {
            HeartbeatIntervalMs = 10_000, // ignore server-emitted heartbeats here
            IdleTimeoutMs = 200,
            TestRequestGraceMs = 200,
        };
        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            NullLoggerFactory.Instance,
            sessionOptions: options,
            onSessionClosed: (s, reason) => { lock (closures) { closures.Add(reason); } });
        listener.Start();
        var ep = listener.LocalEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var ns = client.GetStream();

        // Total runtime: 2 s. Heartbeat every 100 ms = 20 cycles, well past
        // multiple idle-timeout windows (200 ms) and grace windows (200 ms).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        uint seq = 1;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var frame = BuildSequenceFrame(seq++);
                await ns.WriteAsync(frame, cts.Token);
                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException) { }

        // Session must not have been closed during the run.
        lock (closures) Assert.Empty(closures);
    }

    [Fact]
    public async Task EstablishedIdleSession_EmitsKeepaliveLapsedTerminateBeforeClose()
    {
        var sem = new SemaphoreSlim(0, 1);
        FixpSession? closedSession = null;
        string? closeReason = null;
        var options = new FixpSessionOptions
        {
            HeartbeatIntervalMs = 10_000,
            IdleTimeoutMs = 10_000,
            TestRequestGraceMs = 100,
        };
        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            NullLoggerFactory.Instance,
            sessionOptions: options,
            onSessionClosed: (s, reason) =>
            {
                closedSession = s;
                closeReason = reason;
                sem.Release();
            });
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(listener.LocalEndpoint!.Address, listener.LocalEndpoint.Port);
        var ns = client.GetStream();

        const uint sessionId = 10101;
        const ulong sessionVerId = 77;
        var handshake = new byte[256];
        int negotiateLen = EntryPointFixpFrameCodec.EncodeNegotiate(handshake,
            sessionId, sessionVerId, timestampNanos: 0, enteringFirm: 42,
            onBehalfFirm: null,
            credentials: ReadOnlySpan<byte>.Empty,
            clientIp: ReadOnlySpan<byte>.Empty,
            clientAppName: ReadOnlySpan<byte>.Empty,
            clientAppVersion: ReadOnlySpan<byte>.Empty);
        await ns.WriteAsync(handshake.AsMemory(0, negotiateLen));
        var negotiateResponse = await ReadFrameAsync(ns, TimeSpan.FromSeconds(3));
        Assert.Equal((ushort)EntryPointFrameReader.TidNegotiateResponse,
            BinaryPrimitives.ReadUInt16LittleEndian(negotiateResponse.AsSpan(EntryPointFrameReader.SofhSize + 2, 2)));

        int establishLen = EntryPointFixpFrameCodec.EncodeEstablish(handshake,
            sessionId, sessionVerId, timestampNanos: 0, keepAliveIntervalMillis: 100,
            nextSeqNo: 1, cancelOnDisconnectType: 0, codTimeoutWindowMillis: 0,
            credentials: ReadOnlySpan<byte>.Empty);
        await ns.WriteAsync(handshake.AsMemory(0, establishLen));
        var establishAck = await ReadFrameAsync(ns, TimeSpan.FromSeconds(3));
        Assert.Equal((ushort)EntryPointFrameReader.TidEstablishAck,
            BinaryPrimitives.ReadUInt16LittleEndian(establishAck.AsSpan(EntryPointFrameReader.SofhSize + 2, 2)));

        byte[]? terminate = null;
        while (terminate is null)
        {
            var frame = await ReadFrameAsync(ns, TimeSpan.FromSeconds(5));
            ushort frameTid = BinaryPrimitives.ReadUInt16LittleEndian(
                frame.AsSpan(EntryPointFrameReader.SofhSize + 2, 2));
            if (frameTid == EntryPointFrameReader.TidTerminate)
                terminate = frame;
        }

        Assert.True(EntryPointFixpFrameCodec.TryDecodeTerminate(
            terminate.AsSpan(EntryPointFrameReader.WireHeaderSize), out var decoded));
        Assert.Equal(sessionId, decoded.SessionId);
        Assert.Equal(sessionVerId, decoded.SessionVerId);
        Assert.Equal(SessionRejectEncoder.TerminationCode.KeepaliveIntervalLapsed,
            decoded.TerminationCode);

        Assert.True(await sem.WaitAsync(TimeSpan.FromSeconds(3)), "session did not close after Terminate");
        Assert.Equal("idle-timeout", closeReason);
        Assert.Equal(CloseKind.KeepaliveLapsed, closedSession?.LastCloseKind);
    }

    [Fact]
    public async Task ServerEmitsHeartbeat_WhenOutboundIdle()
    {
        var options = new FixpSessionOptions
        {
            HeartbeatIntervalMs = 150,
            IdleTimeoutMs = 10_000,        // disable idle teardown for this test
            TestRequestGraceMs = 10_000,
        };
        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            NullLoggerFactory.Instance,
            sessionOptions: options);
        listener.Start();
        var ep = listener.LocalEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var ns = client.GetStream();

        // Keep inbound alive with one Sequence frame so idle never trips —
        // but with idle/grace at 10s the test would not run long enough to
        // notice anyway. Just read whatever the server sends.
        const int expectedFrameLen = EntryPointFrameReader.WireHeaderSize + 4;
        byte[] buf = new byte[expectedFrameLen];
        int total = 0;
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (total < expectedFrameLen)
        {
            int n = await ns.ReadAsync(buf.AsMemory(total, expectedFrameLen - total), readCts.Token);
            if (n == 0) break;
            total += n;
        }
        Assert.Equal(expectedFrameLen, total);
        Assert.Equal((ushort)EntryPointFrameReader.TidSequence, BitConverter.ToUInt16(buf, EntryPointFrameReader.SofhSize + 2));
        Assert.Equal((ushort)4, BitConverter.ToUInt16(buf, EntryPointFrameReader.SofhSize)); // BlockLength
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var prefix = new byte[EntryPointFrameReader.SofhSize];
        await ReadExactAsync(stream, prefix, cts.Token);
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(prefix);
        var frame = new byte[len];
        prefix.CopyTo(frame, 0);
        await ReadExactAsync(stream, frame.AsMemory(prefix.Length), cts.Token);
        return frame;
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        while (!buffer.IsEmpty)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0) throw new EndOfStreamException();
            buffer = buffer.Slice(n);
        }
    }
}
