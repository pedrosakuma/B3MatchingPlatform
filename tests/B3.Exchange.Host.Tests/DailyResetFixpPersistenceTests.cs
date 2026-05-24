using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using B3.EntryPoint.Wire;
using B3.Exchange.Core;
using B3.Exchange.Gateway.Persistence;
using B3.Exchange.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Host.Tests;

public sealed class DailyResetFixpPersistenceTests
{
    private sealed class NullSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    [Fact]
    public async Task TriggerDailyReset_DropsPersistedFixpState_AndReconnectStartsFresh()
    {
        const uint sessionId = 12345;
        const uint enteringFirm = 7;
        const ulong previousVerId = 77;
        const ulong freshVerId = 1;

        var root = CreateRepoTestDir("daily-reset-fixp-");
        try
        {
            var persister = new FileFixpSessionStatePersister(root, NullLogger<FileFixpSessionStatePersister>.Instance);
            persister.Save(new FixpSessionStateSnapshot(sessionId, previousVerId, 42, 0, enteringFirm, 0));
            persister.Dispose();

            var cfg = BuildConfig(root, sessionId, enteringFirm);
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();
            var ep = host.TcpEndpoint!;

            using var resumedClient = new TcpClient();
            await resumedClient.ConnectAsync(ep.Address, ep.Port);
            var resumedStream = resumedClient.GetStream();
            await WriteEstablishAsync(resumedStream, sessionId, previousVerId, nextSeqNo: 1);
            var resumedAck = await ReadFrameAsync(resumedStream, TimeSpan.FromSeconds(5));
            Assert.Equal(EntryPointFrameReader.TidEstablishAck, resumedAck.TemplateId);
            Assert.True(EntryPointFixpFrameCodec.TryDecodeEstablishAck(resumedAck.Body, out var oldAck));
            Assert.Equal(previousVerId, oldAck.SessionVerId);
            Assert.Equal(43u, oldAck.NextSeqNo);

            Assert.Equal(1, host.TriggerDailyReset("test-daily-reset"));
            resumedClient.Close();

            using var freshClient = new TcpClient();
            await freshClient.ConnectAsync(ep.Address, ep.Port);
            var freshStream = freshClient.GetStream();
            await WriteNegotiateAsync(freshStream, sessionId, freshVerId, enteringFirm);
            var negotiate = await ReadFrameAsync(freshStream, TimeSpan.FromSeconds(5));
            Assert.Equal(EntryPointFrameReader.TidNegotiateResponse, negotiate.TemplateId);
            Assert.True(EntryPointFixpFrameCodec.TryDecodeNegotiateResponse(negotiate.Body, out var negotiateResponse));
            Assert.Equal(freshVerId, negotiateResponse.SessionVerId);

            await WriteEstablishAsync(freshStream, sessionId, freshVerId, nextSeqNo: 1);
            var freshAckFrame = await ReadFrameAsync(freshStream, TimeSpan.FromSeconds(5));
            Assert.Equal(EntryPointFrameReader.TidEstablishAck, freshAckFrame.TemplateId);
            Assert.True(EntryPointFixpFrameCodec.TryDecodeEstablishAck(freshAckFrame.Body, out var freshAck));
            Assert.Equal(freshVerId, freshAck.SessionVerId);
            Assert.Equal(1u, freshAck.NextSeqNo);

            await host.StopAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static HostConfig BuildConfig(string persistenceDir, uint sessionId, uint enteringFirm)
        => new()
        {
            Auth = new AuthConfig { DevMode = true, RequireFixpHandshake = true },
            Tcp = new TcpConfig
            {
                Listen = "127.0.0.1:0",
                EnteringFirm = enteringFirm,
                RetransmitPersistenceDir = persistenceDir,
                HeartbeatIntervalMs = 60_000,
                IdleTimeoutMs = 60_000,
                TestRequestGraceMs = 60_000,
            },
            Firms = { new FirmConfig { Id = "firm", Name = "firm", EnteringFirmCode = enteringFirm } },
            Sessions = { new SessionConfig { SessionId = sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture), FirmId = "firm" } },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 84,
                    IncrementalGroup = "239.255.42.84",
                    IncrementalPort = 30184,
                    Ttl = 0,
                    InstrumentsFile = TestPaths.ResolveRepoFile("config/instruments-eqt.json"),
                },
            },
        };

    private static async Task WriteNegotiateAsync(NetworkStream stream, uint sessionId, ulong sessionVerId, uint enteringFirm)
    {
        var credentials = Encoding.ASCII.GetBytes($"{{\"auth_type\":\"basic\",\"username\":\"{sessionId}\",\"access_key\":\"\"}}");
        var buffer = new byte[EntryPointFrameReader.MaxInboundMessageLength];
        int length = EntryPointFixpFrameCodec.EncodeNegotiate(buffer, sessionId, sessionVerId, timestampNanos: 0, enteringFirm, onBehalfFirm: null, credentials, clientIp: "127.0.0.1"u8, clientAppName: "test"u8, clientAppVersion: "1"u8);
        await stream.WriteAsync(buffer.AsMemory(0, length));
    }

    private static async Task WriteEstablishAsync(NetworkStream stream, uint sessionId, ulong sessionVerId, uint nextSeqNo)
    {
        var buffer = new byte[EntryPointFrameReader.MaxInboundMessageLength];
        int length = EntryPointFixpFrameCodec.EncodeEstablish(buffer, sessionId, sessionVerId, timestampNanos: 0, keepAliveIntervalMillis: 60_000, nextSeqNo, cancelOnDisconnectType: 0, codTimeoutWindowMillis: 0, credentials: ReadOnlySpan<byte>.Empty);
        await stream.WriteAsync(buffer.AsMemory(0, length));
    }

    private readonly record struct ReadFrame(ushort TemplateId, byte[] Body);

    private static async Task<ReadFrame> ReadFrameAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var header = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, header, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        ushort encodingType = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        if (encodingType != EntryPointFrameReader.SofhEncodingType)
            throw new InvalidOperationException($"unexpected SOFH encoding type 0x{encodingType:X4}");
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        int bodyLength = messageLength - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLength];
        await ReadExactAsync(stream, body, cts.Token);
        return new ReadFrame(templateId, body);
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
