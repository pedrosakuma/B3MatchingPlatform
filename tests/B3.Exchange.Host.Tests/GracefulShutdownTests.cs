using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using B3.EntryPoint.Wire;
using B3.Exchange.Core;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

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
    private const long Petr = 900_000_000_001L;

    private sealed class NullSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    private static HostConfig BuildConfig()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
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

    [Fact]
    public async Task StopAsync_WithRetransmitPersistenceDir_PreservesFixpStateAndJournal_ForEstablishResume()
    {
        const uint sessionId = 613;
        const ulong sessionVerId = 9;
        const uint enteringFirm = 7;

        var root = CreateRepoTestDir("graceful-shutdown-fixp-");
        try
        {
            var cfg = BuildConfig();
            cfg.Auth = new AuthConfig { DevMode = true, RequireFixpHandshake = true };
            cfg.Tcp.RetransmitPersistenceDir = root;
            cfg.Tcp.HeartbeatIntervalMs = 60_000;
            cfg.Tcp.IdleTimeoutMs = 60_000;
            cfg.Tcp.TestRequestGraceMs = 60_000;
            cfg.Firms.Add(new FirmConfig
            {
                Id = "firm-613",
                Name = "Issue 613 regression",
                EnteringFirmCode = enteringFirm,
            });
            cfg.Sessions.Add(new SessionConfig
            {
                SessionId = sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FirmId = "firm-613",
            });

            await using (var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink()))
            {
                await host.StartAsync();
                var ep = host.TcpEndpoint!;

                using var client = new TcpClient();
                await client.ConnectAsync(ep.Address, ep.Port);
                var stream = client.GetStream();

                await WriteNegotiateAsync(stream, sessionId, sessionVerId, enteringFirm);
                Assert.Equal(EntryPointFrameReader.TidNegotiateResponse,
                    (await ReadFrameAsync(stream, TimeSpan.FromSeconds(5))).TemplateId);

                await WriteEstablishAsync(stream, sessionId, sessionVerId, nextSeqNo: 1);
                var establishAck = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Equal(EntryPointFrameReader.TidEstablishAck, establishAck.TemplateId);
                Assert.True(EntryPointFixpFrameCodec.TryDecodeEstablishAck(establishAck.Body, out var ack));
                Assert.Equal(1u, ack.NextSeqNo);

                await stream.WriteAsync(BuildSimpleNewOrder(
                    clOrdId: 61301,
                    secId: Petr,
                    side: '1',
                    ordType: '2',
                    tif: '0',
                    qty: 100,
                    priceMantissa: 123_400,
                    sessionId: sessionId,
                    msgSeqNum: 1));

                var er = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er.TemplateId);

                await host.StopAsync();
            }

            using (var persister = new FileFixpSessionStatePersister(
                root,
                NullLogger<FileFixpSessionStatePersister>.Instance))
            {
                var snapshot = persister.Load(sessionId);
                Assert.NotNull(snapshot);
                Assert.Equal(sessionVerId, snapshot.Value.SessionVerId);
                Assert.Equal(1u, snapshot.Value.OutboundMsgSeqNum);
                Assert.Equal(1u, snapshot.Value.LastIncomingSeqNo);
            }

            using (var journal = new FileFixpOutboundJournal(
                root,
                NullLogger<FileFixpOutboundJournal>.Instance))
            {
                Assert.Equal(1u, journal.MaxSeq(sessionId));
                Assert.Single(journal.ReadRange(sessionId, fromSeq: 1, count: 10));
            }

            await using (var restarted = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink()))
            {
                await restarted.StartAsync();
                var ep = restarted.TcpEndpoint!;

                using var client = new TcpClient();
                await client.ConnectAsync(ep.Address, ep.Port);
                var stream = client.GetStream();

                await WriteEstablishAsync(stream, sessionId, sessionVerId, nextSeqNo: 2);
                var resumedAckFrame = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
                Assert.Equal(EntryPointFrameReader.TidEstablishAck, resumedAckFrame.TemplateId);
                Assert.True(EntryPointFixpFrameCodec.TryDecodeEstablishAck(resumedAckFrame.Body, out var resumedAck));
                Assert.Equal(sessionVerId, resumedAck.SessionVerId);
                Assert.Equal(2u, resumedAck.NextSeqNo);

                await restarted.StopAsync();
            }
        }
        finally
        {
            TryDelete(root);
        }
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

    private static async Task WriteNegotiateAsync(NetworkStream stream, uint sessionId, ulong sessionVerId, uint enteringFirm)
    {
        var credentials = Encoding.ASCII.GetBytes($"{{\"auth_type\":\"basic\",\"username\":\"{sessionId}\",\"access_key\":\"\"}}");
        var buffer = new byte[EntryPointFrameReader.MaxInboundMessageLength];
        int length = EntryPointFixpFrameCodec.EncodeNegotiate(
            buffer,
            sessionId,
            sessionVerId,
            timestampNanos: 0,
            enteringFirm,
            onBehalfFirm: null,
            credentials,
            clientIp: "127.0.0.1"u8,
            clientAppName: "test"u8,
            clientAppVersion: "1"u8);
        await stream.WriteAsync(buffer.AsMemory(0, length));
    }

    private static async Task WriteEstablishAsync(NetworkStream stream, uint sessionId, ulong sessionVerId, uint nextSeqNo)
    {
        var buffer = new byte[EntryPointFrameReader.MaxInboundMessageLength];
        int length = EntryPointFixpFrameCodec.EncodeEstablish(
            buffer,
            sessionId,
            sessionVerId,
            timestampNanos: 0,
            keepAliveIntervalMillis: 60_000,
            nextSeqNo,
            cancelOnDisconnectType: 0,
            codTimeoutWindowMillis: 0,
            credentials: ReadOnlySpan<byte>.Empty);
        await stream.WriteAsync(buffer.AsMemory(0, length));
    }

    private static byte[] BuildSimpleNewOrder(ulong clOrdId, long secId, char side, char ordType,
        char tif, long qty, long priceMantissa, uint sessionId, uint msgSeqNum)
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 82];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 82,
            templateId: EntryPointFrameReader.TidSimpleNewOrder,
            version: 2);

        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4, 4), msgSeqNum);
        ulong sendingTimeNanos = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(8, 8), sendingTimeNanos);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(48, 8), secId);
        body[56] = (byte)side;
        body[57] = (byte)ordType;
        body[58] = (byte)tif;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(60, 8), qty);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(68, 8), priceMantissa);
        return frame;
    }

    private static string CreateRepoTestDir(string prefix)
    {
        var config = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var repoRoot = Directory.GetParent(Path.GetDirectoryName(config)!)!.FullName;
        var dir = Path.Combine(repoRoot, "artifacts", "test-dirs", prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }
}
