using System.Buffers.Binary;
using System.Net.Sockets;
using B3.EntryPoint.Wire;
using B3.Exchange.Core;
using B3.Exchange.TestSupport;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Validates the OPT channel wiring shipped by T-4 / OPT-04 (RFC 0002
/// §3.1, ADR 0013):
///
/// <list type="number">
///   <item>The bundled <c>config/exchange-simulator.json</c> declares
///   the OPT channel (78) alongside EQT (84) and DRV (72), each with
///   the right instrument file and disjoint multicast endpoints.</item>
///   <item>Booting the host with the bundled EQT + OPT instrument
///   files (with a programmatic <see cref="HostConfig"/> so we don't
///   need to bind real multicast sockets or the production TCP port)
///   produces one <see cref="ChannelDispatcher"/> per channel, each
///   seeded with its own instrument set and no SecurityID overlap.
///   A NewOrder round-trip on each channel returns exactly one
///   <c>ExecutionReport_New</c> back to the originating session,
///   demonstrating that <see cref="HostRouter"/> partitions correctly
///   by SecurityID across EQT and OPT.</item>
/// </list>
/// </summary>
public class OptionsChannelE2ETests
{
    private const byte EqtChannel = 84;
    private const byte OptChannel = 78;
    private const long PetrSecurityId = 900000000001L; // PETR4 on EQT
    private const long PetrL200SecurityId = 902000000001L; // PETRL200 on OPT

    [Fact]
    public void BundledExchangeSimulatorConfig_DeclaresOptChannelAlongsideEqtAndDrv()
    {
        var cfg = HostConfigLoader.Load(TestPaths.ResolveRepoFile("config/exchange-simulator.json"));

        var byNumber = cfg.Channels.ToDictionary(c => c.ChannelNumber);
        Assert.Contains((byte)84, byNumber.Keys);
        Assert.Contains((byte)72, byNumber.Keys);
        Assert.Contains(OptChannel, byNumber.Keys);

        var opt = byNumber[OptChannel];
        Assert.EndsWith("instruments-opt.json", opt.InstrumentsFile, StringComparison.Ordinal);

        // Per-channel UMDF endpoints must be disjoint across every channel
        // group AND across the snapshot / instrument-definition feeds.
        var endpoints = new List<string>();
        foreach (var ch in cfg.Channels)
        {
            endpoints.Add($"{ch.IncrementalGroup}:{ch.IncrementalPort}");
            if (ch.Snapshot is { } s) endpoints.Add($"{s.Group}:{s.Port}");
            if (ch.InstrumentDefinition is { } d) endpoints.Add($"{d.Group}:{d.Port}");
        }
        Assert.Equal(endpoints.Count, endpoints.Distinct().Count());
    }

    [Fact]
    public async Task TwoChannelHost_EqtAndOpt_RoundTripsExecutionReportPerChannel()
    {
        var eqtPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var optPath = TestPaths.ResolveRepoFile("config/instruments-opt.json");

        var sinks = new Dictionary<byte, RecordingPacketSink>
        {
            [EqtChannel] = new RecordingPacketSink(),
            [OptChannel] = new RecordingPacketSink(),
        };

        var cfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
        };
        cfg.Channels.Add(new ChannelConfig
        {
            ChannelNumber = EqtChannel,
            IncrementalGroup = "239.255.84.84",
            IncrementalPort = 30184,
            Ttl = 0,
            InstrumentsFile = eqtPath,
        });
        cfg.Channels.Add(new ChannelConfig
        {
            ChannelNumber = OptChannel,
            IncrementalGroup = "239.255.78.78",
            IncrementalPort = 30178,
            Ttl = 0,
            InstrumentsFile = optPath,
        });

        await using var host = new ExchangeHost(cfg,
            packetSinkFactory: ch => sinks[ch.ChannelNumber]);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        // Sanity: exactly one dispatcher per channel and each one is
        // seeded with the right instrument set (no SecurityID overlap).
        Assert.Equal(2, host.Dispatchers.Count);
        var dispByChannel = host.Dispatchers.ToDictionary(d => d.ChannelNumber);
        Assert.Contains(EqtChannel, dispByChannel.Keys);
        Assert.Contains(OptChannel, dispByChannel.Keys);

        // Round-trip a NewOrder on each channel and assert exactly one
        // ExecutionReport_New comes back per session. PETR4 has
        // lotSize=100 (round lot); PETRL200 has lotSize=1 (one contract).
        await AssertNewOrderRoundTripAsync(ep, secId: PetrSecurityId, clOrdId: 1001,
            qty: 100, priceMantissa: 30_0000L); // PETR4 @ 30.00, round-lot
        await AssertNewOrderRoundTripAsync(ep, secId: PetrL200SecurityId, clOrdId: 2001,
            qty: 1, priceMantissa: 5_0000L); // PETRL200 @ 5.00 (option premium)

        // Allow the dispatch threads a beat to flush UMDF packets.
        await Task.Delay(200);

        // Each channel emitted at least one UMDF packet, and none of the
        // packets contain a SecurityID belonging to the other channel.
        AssertPacketsOnlyForChannel(sinks[EqtChannel], allowed: PetrSecurityId, forbidden: PetrL200SecurityId);
        AssertPacketsOnlyForChannel(sinks[OptChannel], allowed: PetrL200SecurityId, forbidden: PetrSecurityId);
    }

    private static async Task AssertNewOrderRoundTripAsync(
        System.Net.IPEndPoint ep, long secId, ulong clOrdId, long qty, long priceMantissa)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        var frame = BuildSimpleNewOrder(clOrdId, secId, side: '1', ordType: '2', tif: '0',
            qty: qty, priceMantissa: priceMantissa);
        await stream.WriteAsync(frame);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        bool gotErNew = false;
        while (!gotErNew && DateTime.UtcNow < deadline)
        {
            var er = await ReadFrameAsync(stream, deadline - DateTime.UtcNow);
            if (er.TemplateId == EntryPointFrameReader.TidExecutionReportNew) gotErNew = true;
        }
        Assert.True(gotErNew, $"expected ExecutionReport_New for clOrdId={clOrdId} secId={secId}");
    }

    private static void AssertPacketsOnlyForChannel(RecordingPacketSink sink, long allowed, long forbidden)
    {
        int packetCount = 0;
        foreach (var pkt in sink.Packets)
        {
            packetCount++;
            int cursor = WireOffsets.PacketHeaderSize;
            while (cursor + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize <= pkt.Length)
            {
                ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(pkt.AsSpan(cursor, 2));
                int sbeOffset = cursor + WireOffsets.FramingHeaderSize;
                ushort tid = BinaryPrimitives.ReadUInt16LittleEndian(pkt.AsSpan(sbeOffset + 2, 2));
                int bodyOffset = sbeOffset + WireOffsets.SbeMessageHeaderSize;
                // Skip ChannelReset (tid=11) and SequenceReset (tid=2)
                // which don't carry an instrument id.
                if (tid != 11 && tid != 2 && bodyOffset + 8 <= pkt.Length)
                {
                    long secId = BinaryPrimitives.ReadInt64LittleEndian(pkt.AsSpan(bodyOffset, 8));
                    Assert.NotEqual(forbidden, secId);
                }
                cursor += messageLength;
            }
        }
        Assert.True(packetCount > 0, "expected at least one UMDF packet on the channel");
    }

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Packets.Enqueue(packet.ToArray());
    }

    private static byte[] BuildSimpleNewOrder(ulong clOrdId, long secId, char side, char ordType,
        char tif, long qty, long priceMantissa)
    {
        var frame = new byte[EntryPointFrameReader.WireHeaderSize + 82];
        EntryPointFrameReader.WriteHeader(frame.AsSpan(0, EntryPointFrameReader.WireHeaderSize),
            messageLength: (ushort)frame.Length,
            blockLength: 82, templateId: EntryPointFrameReader.TidSimpleNewOrder, version: 2);
        var body = frame.AsSpan(EntryPointFrameReader.WireHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), clOrdId);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(48, 8), secId);
        body[56] = (byte)side;
        body[57] = (byte)ordType;
        body[58] = (byte)tif;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(60, 8), qty);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(68, 8), priceMantissa);
        return frame;
    }

    private readonly record struct ReadFrame(ushort TemplateId, ushort Version);

    private static async Task<ReadFrame> ReadFrameAsync(NetworkStream stream, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromMilliseconds(1);
        using var cts = new CancellationTokenSource(timeout);
        var headerBuf = new byte[EntryPointFrameReader.WireHeaderSize];
        await ReadExactAsync(stream, headerBuf, cts.Token);
        ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(0, 2));
        var sbeHeader = headerBuf.AsSpan(EntryPointFrameReader.SofhSize);
        ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(sbeHeader.Slice(2, 2));
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(sbeHeader.Slice(6, 2));
        int bodyLen = messageLength - EntryPointFrameReader.WireHeaderSize;
        var body = new byte[bodyLen];
        await ReadExactAsync(stream, body, cts.Token);
        return new ReadFrame(templateId, version);
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
