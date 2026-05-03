using System.Buffers.Binary;
using System.Net.Sockets;
using B3.EntryPoint.Wire;
using B3.Exchange.Core;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Issue #171 (A7): graceful shutdown E2E. Verifies the
/// <see cref="ExchangeHost.StopAsync"/> contract:
///   • readiness probe flips NOT_READY immediately,
///   • live FIXP sessions receive a <c>Terminate(Finished)</c> frame
///     before the TCP socket closes (so peers see an orderly drain
///     instead of an RST/timeout),
///   • the call is idempotent.
/// </summary>
public class GracefulShutdownTests
{
    private sealed class NullSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    private static HostConfig BuildConfig()
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
        return new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Shutdown = new ShutdownConfig { DrainGraceMs = 200, DrainPollMs = 10 },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 91,
                    IncrementalGroup = "239.255.91.1",
                    IncrementalPort = 30191,
                    Ttl = 0,
                    InstrumentsFile = instrumentsPath,
                },
            },
        };
    }

    private static string ResolveRepoFile(string relPath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, relPath);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"could not locate {relPath} from {AppContext.BaseDirectory}");
    }

    [Fact]
    public async Task StopAsync_BroadcastsTerminateFinished_BeforeTcpClose()
    {
        var cfg = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        // Wait briefly so the listener has registered the FixpSession in
        // its _sessions list before we start shutdown (accept-loop runs
        // the registration asynchronously).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && CountReadyForShutdown(host) == 0)
            await Task.Delay(20);
        Assert.True(CountReadyForShutdown(host) > 0, "expected listener to have registered the connected client");

        // Trigger the graceful shutdown on a background task; we then
        // read from the client to prove the Terminate frame arrives.
        var stopTask = host.StopAsync();

        var frame = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidTerminate, frame.TemplateId);
        Assert.Equal(SessionRejectEncoder.TerminationCode.Finished, frame.Body[12]);

        await stopTask;
    }

    private static int CountReadyForShutdown(ExchangeHost host)
    {
        // Probe the listener through a short reflection-free contract:
        // count active sessions via the ChannelDispatcher list is not
        // useful here, so we just attempt to read via a private getter.
        // Simpler: rely on time-based wait — but we want a deterministic
        // signal, so peek the listener's ActiveSessions count. The
        // EntryPointListener is exposed through a friend assembly via
        // InternalsVisibleTo("B3.Exchange.Host.Tests"). Use it.
        var listener = typeof(ExchangeHost)
            .GetField("_listener", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(host) as B3.Exchange.Gateway.EntryPointListener;
        return listener?.ActiveSessions.Count ?? 0;
    }

    [Fact]
    public async Task StopAsync_FlipsReadinessProbeImmediately()
    {
        var cfg = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
        await host.StartAsync();

        var probe = GetShutdownProbe(host);
        Assert.True(probe.IsReady, "shutdown probe must start ready");

        await host.StopAsync();

        Assert.False(probe.IsReady, "shutdown probe must flip NOT_READY after StopAsync");
    }

    [Fact]
    public async Task StopAsync_IsIdempotent()
    {
        var cfg = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
        await host.StartAsync();

        await host.StopAsync();
        // Second call must not throw and must return promptly.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await host.StopAsync();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"second StopAsync should short-circuit, took {sw.ElapsedMilliseconds}ms");
    }

    private static ShutdownReadinessProbe GetShutdownProbe(ExchangeHost host)
    {
        var f = typeof(ExchangeHost)
            .GetField("_shutdownProbe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (ShutdownReadinessProbe)f.GetValue(host)!;
    }

    private readonly record struct ReadFrame(ushort TemplateId, ushort Version, byte[] Body);

    private static async Task<ReadFrame> ReadFrameAsync(NetworkStream stream, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromMilliseconds(1);
        using var cts = new CancellationTokenSource(timeout);
        var headerBuf = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, headerBuf, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(0, 2));
        ushort encodingType = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(2, 2));
        if (encodingType != EntryPointFrameReader.SofhEncodingType)
            throw new InvalidOperationException($"unexpected SOFH encoding type 0x{encodingType:X4}");
        var sbeHeader = headerBuf.AsSpan(EntryPointFrameReader.SofhSize);
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(sbeHeader.Slice(2, 2));
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(sbeHeader.Slice(6, 2));
        int bodyLen = messageLength - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLen];
        await ReadExactAsync(stream, body, cts.Token);
        return new ReadFrame(templateId, version, body);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n <= 0) throw new EndOfStreamException("connection closed");
            read += n;
        }
    }
}
