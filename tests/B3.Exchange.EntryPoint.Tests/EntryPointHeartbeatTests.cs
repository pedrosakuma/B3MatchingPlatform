using System.Net;
using System.Net.Sockets;
using B3.Exchange.EntryPoint;
using B3.Exchange.Matching;

namespace B3.Exchange.EntryPoint.Tests;

/// <summary>
/// Integration coverage for issue #9: server-side heartbeat + idle timeout.
/// Uses a real loopback TCP connection driven by <see cref="EntryPointListener"/>
/// with sub-second intervals so the suite stays fast.
/// </summary>
public class EntryPointHeartbeatTests
{
    private sealed class NoOpEngineSink : IEntryPointEngineSink
    {
        public void EnqueueNewOrder(in NewOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue) { }
        public void EnqueueCancel(in CancelOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue, ulong origClOrdIdValue) { }
        public void EnqueueReplace(in ReplaceOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue, ulong origClOrdIdValue) { }
        public void OnDecodeError(IEntryPointResponseChannel reply, string error) { }
    }

    private static byte[] BuildSequenceFrame(uint nextSeq)
    {
        var f = new byte[12];
        EntryPointFrameReader.WriteHeader(f, blockLength: 4, templateId: EntryPointFrameReader.TidSequence, version: 0);
        BitConverter.GetBytes(nextSeq).CopyTo(f, 8);
        return f;
    }

    [Fact]
    public async Task IdleSession_IsTornDown_AfterTimeout()
    {
        var closures = new List<string>();
        var sem = new SemaphoreSlim(0, 1);
        var options = new EntryPointSessionOptions
        {
            HeartbeatIntervalMs = 10_000, // not relevant for this test
            IdleTimeoutMs = 200,
            TestRequestGraceMs = 200,
        };
        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
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
        // We should have received exactly one Sequence probe (12 bytes) before EOF.
        Assert.Equal(12, total);
        // Header sanity: templateId == 9 at offset 2.
        ushort tid = BitConverter.ToUInt16(buf, 2);
        Assert.Equal((ushort)EntryPointFrameReader.TidSequence, tid);
    }

    [Fact]
    public async Task ActivelyHeartbeatingSession_StaysAlive()
    {
        var closures = new List<string>();
        var options = new EntryPointSessionOptions
        {
            HeartbeatIntervalMs = 10_000, // ignore server-emitted heartbeats here
            IdleTimeoutMs = 200,
            TestRequestGraceMs = 200,
        };
        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
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
    public async Task ServerEmitsHeartbeat_WhenOutboundIdle()
    {
        var options = new EntryPointSessionOptions
        {
            HeartbeatIntervalMs = 150,
            IdleTimeoutMs = 10_000,        // disable idle teardown for this test
            TestRequestGraceMs = 10_000,
        };
        await using var listener = new EntryPointListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            new NoOpEngineSink(),
            sessionOptions: options);
        listener.Start();
        var ep = listener.LocalEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var ns = client.GetStream();

        // Keep inbound alive with one Sequence frame so idle never trips —
        // but with idle/grace at 10s the test would not run long enough to
        // notice anyway. Just read whatever the server sends.
        byte[] buf = new byte[12];
        int total = 0;
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (total < 12)
        {
            int n = await ns.ReadAsync(buf.AsMemory(total, 12 - total), readCts.Token);
            if (n == 0) break;
            total += n;
        }
        Assert.Equal(12, total);
        Assert.Equal((ushort)EntryPointFrameReader.TidSequence, BitConverter.ToUInt16(buf, 2));
        Assert.Equal((ushort)4, BitConverter.ToUInt16(buf, 0)); // BlockLength
    }
}
