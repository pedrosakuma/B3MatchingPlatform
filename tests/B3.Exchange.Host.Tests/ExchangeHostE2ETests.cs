using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using B3.Exchange.EntryPoint;
using B3.Exchange.Integration;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// End-to-end happy-path: spin up <see cref="ExchangeHost"/> on loopback,
/// open a TCP client to the EntryPoint listener, send a SimpleNewOrderV2
/// frame, and read the ExecutionReport_New back. Then send a crossing
/// counter-order and read both ExecutionReport_New and ExecutionReport_Trade
/// for the aggressor side.
///
/// This is the only test that exercises:
///   TCP socket → EntryPointFrameReader → InboundMessageDecoder →
///   HostRouter → ChannelDispatcher → MatchingEngine → ExecutionReport
///   encode → TCP socket
///
/// Multicast publishing is left to <c>ChannelDispatcherTests</c> with a
/// recording packet sink — sniffing real multicast in CI is too fragile.
/// </summary>
public class ExchangeHostE2ETests
{
    private const long Petr = 900_000_000_001L;

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            lock (Packets) Packets.Add(packet.ToArray());
        }
    }

    private static (HostConfig cfg, RecordingPacketSink sink) BuildConfig()
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
        var cfg = new HostConfig
        {
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 84,
                    IncrementalGroup = "239.255.42.84",
                    IncrementalPort = 30184,
                    Ttl = 0,
                    InstrumentsFile = instrumentsPath,
                },
            },
        };
        return (cfg, new RecordingPacketSink());
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
    public async Task NewOrder_RoundTripsExecutionReportNew_ThenCrossingOrderProducesTrade()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        // First order: BUY PETR4 100 @ 12.34
        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        var newOrder = BuildSimpleNewOrder(clOrdId: 1001, secId: Petr,
            side: '1', ordType: '2', tif: '0', qty: 100, priceMantissa: 123_400);
        await stream.WriteAsync(newOrder);

        var er1 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er1.TemplateId);
        Assert.Equal(2, er1.Version);

        // Second order on the SAME session: SELL PETR4 100 @ 12.34 → fully
        // crosses the resting BUY. Aggressor session sees ER_Trade (no New —
        // fully filled). Passive owner is the same session, so it also sees
        // a Trade ER (i.e. two ER_Trade frames total).
        var sellOrder = BuildSimpleNewOrder(clOrdId: 2001, secId: Petr,
            side: '2', ordType: '2', tif: '0', qty: 100, priceMantissa: 123_400);
        await stream.WriteAsync(sellOrder);

        var er2 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.True(
            er2.TemplateId == EntryPointFrameReader.TidExecutionReportTrade,
            $"expected ER_Trade(203) but got tid={er2.TemplateId}");

        var er3 = await ReadFrameAsync(stream, TimeSpan.FromSeconds(5));
        Assert.True(
            er3.TemplateId == EntryPointFrameReader.TidExecutionReportTrade,
            $"expected second ER_Trade(203) but got tid={er3.TemplateId}");

        // Multicast side: at least 2 packets emitted (BUY add + SELL cross
        // batch). Each packet starts with the 16-byte UMDF PacketHeader.
        // Poll briefly: FlushPacket runs on the dispatcher thread after the
        // ER frames are queued, so it may lag the client's ER read slightly.
        var sinkDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (sink.Packets.Count < 2 && DateTime.UtcNow < sinkDeadline)
            await Task.Delay(20);
        Assert.True(sink.Packets.Count >= 2, $"expected >= 2 multicast packets, got {sink.Packets.Count}");
    }

    [Fact]
    public async Task UnknownInstrument_ProducesRejectExecutionReport()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        var bogus = BuildSimpleNewOrder(clOrdId: 9001, secId: 999_999_999_999L,
            side: '1', ordType: '2', tif: '0', qty: 100, priceMantissa: 100_000);
        await stream.WriteAsync(bogus);

        var er = await ReadFrameAsync(stream, TimeSpan.FromSeconds(2));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportReject, er.TemplateId);
    }

    [Fact]
    public async Task MalformedFrame_UnknownTemplateId_ProducesSessionRejectAndClosesConnection()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        // Send a header with a templateId the server does not recognise. No
        // body is sent — the server rejects the header and tears down before
        // attempting to read the body.
        var hdr = new byte[8];
        EntryPointFrameReader.WriteHeader(hdr.AsSpan(0, 8),
            blockLength: 0, templateId: 0xABCD, version: 0);
        await stream.WriteAsync(hdr);

        var rej = await ReadFrameAsync(stream, TimeSpan.FromSeconds(2));
        Assert.Equal(EntryPointFrameReader.TidTerminate, rej.TemplateId);
        Assert.Equal((ushort)0, rej.Version);
        Assert.Equal(SessionRejectEncoder.TerminationCode.UnrecognizedMessage, rej.Body[12]);

        // Connection MUST be closed by the server after the Terminate is
        // flushed. ReadAsync returns 0 on a clean half-close.
        var trailer = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        int read = await stream.ReadAsync(trailer.AsMemory(0, 1), cts.Token);
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task WellFramedButInvalidBody_ProducesBusinessRejectAndKeepsConnectionOpen()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        // Well-framed SimpleNewOrder but with an invalid Side byte ('X').
        // Decode succeeds at the framing layer and fails inside
        // InboundMessageDecoder.TryDecodeNewOrder, which is the
        // BusinessMessageReject path.
        var bad = BuildSimpleNewOrder(clOrdId: 7777, secId: Petr,
            side: 'X', ordType: '2', tif: '0', qty: 100, priceMantissa: 123_400);
        await stream.WriteAsync(bad);

        var rej = await ReadFrameAsync(stream, TimeSpan.FromSeconds(2));
        Assert.Equal(EntryPointFrameReader.TidBusinessMessageReject, rej.TemplateId);
        // RefSeqNum lives at body offset 20 and echoes the inbound MsgSeqNum
        // we wrote at offset 4 of the SimpleNewOrder body (== 0 in the
        // builder, since we don't populate that field). What we really care
        // about is BusinessRejectRefID = ClOrdID (7777) at offset 24.
        Assert.Equal(7777UL,
            BinaryPrimitives.ReadUInt64LittleEndian(rej.Body.AsSpan(24, 8)));

        // Critically: the session must remain open. Send a *valid* order
        // and expect ER_New to come back on the same connection.
        var ok = BuildSimpleNewOrder(clOrdId: 8888, secId: Petr,
            side: '1', ordType: '2', tif: '0', qty: 100, priceMantissa: 123_400);
        await stream.WriteAsync(ok);

        var er = await ReadFrameAsync(stream, TimeSpan.FromSeconds(2));
        Assert.Equal(EntryPointFrameReader.TidExecutionReportNew, er.TemplateId);
    }

    private static byte[] BuildSimpleNewOrder(ulong clOrdId, long secId, char side, char ordType,
        char tif, long qty, long priceMantissa)
    {
        // 8-byte SBE MessageHeader + 82-byte SimpleNewOrderV2 body.
        var frame = new byte[8 + 82];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, 8),
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);

        var body = frame.AsSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(48, 8), secId);
        body[56] = (byte)side;
        body[57] = (byte)ordType;
        body[58] = (byte)tif;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(60, 8), qty);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(68, 8), priceMantissa);
        return frame;
    }

    private readonly record struct ReadFrame(ushort TemplateId, ushort Version, byte[] Body);

    private static async Task<ReadFrame> ReadFrameAsync(NetworkStream stream, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromMilliseconds(1);
        using var cts = new CancellationTokenSource(timeout);
        var headerBuf = new byte[8];
        await ReadExactAsync(stream, headerBuf, cts.Token);
        ushort blockLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(0, 2));
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(2, 2));
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(6, 2));
        var body = new byte[blockLength];
        await ReadExactAsync(stream, body, cts.Token);
        // BusinessMessageReject carries two trailing varData segments
        // (memo + text). Drain them so the next ReadFrameAsync starts on a
        // fresh frame header.
        if (templateId == EntryPointFrameReader.TidBusinessMessageReject)
        {
            var len = new byte[1];
            await ReadExactAsync(stream, len, cts.Token);
            int memoLen = len[0];
            if (memoLen > 0) await ReadExactAsync(stream, new byte[memoLen], cts.Token);
            await ReadExactAsync(stream, len, cts.Token);
            int textLen = len[0];
            if (textLen > 0) await ReadExactAsync(stream, new byte[textLen], cts.Token);
        }
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
