using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using B3.Exchange.EntryPoint;
using B3.Exchange.Integration;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// End-to-end test for the snapshot publisher: bring up <see cref="ExchangeHost"/>
/// with a snapshot channel configured, route a few orders into the book over
/// the EntryPoint TCP listener, force a snapshot tick by enqueuing one
/// directly, and assert the recorded snap-sink packet decodes to the
/// expected book state with a non-null <c>LastRptSeq</c>.
/// </summary>
public class ExchangeHostSnapshotE2ETests
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
    public async Task SnapshotTick_AfterRestingOrders_PublishesBookStateOnSnapSink()
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
                    // Cadence is huge so the timer never fires in test wall-clock —
                    // we trigger snapshots manually via EnqueueSnapshotTick().
                    Snapshot = new SnapshotChannelConfig
                    {
                        Group = "239.255.42.85",
                        Port = 30185,
                        Ttl = 0,
                        CadenceMs = 60_000,
                        MaxEntriesPerChunk = 30,
                    },
                },
            },
        };
        var incSink = new RecordingPacketSink();
        var snapSink = new RecordingPacketSink();
        await using var host = new ExchangeHost(cfg,
            packetSinkFactory: _ => incSink,
            snapshotSinkFactory: (_, _) => snapSink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        // Submit two resting BUYs at different prices.
        using var client = new TcpClient();
        await client.ConnectAsync(ep.Address, ep.Port);
        var stream = client.GetStream();

        await stream.WriteAsync(BuildSimpleNewOrder(1001, Petr, '1', '2', '0', 100, 12_3400));
        await ReadFrameAsync(stream, TimeSpan.FromSeconds(5)); // ER_New
        await stream.WriteAsync(BuildSimpleNewOrder(1002, Petr, '1', '2', '0', 200, 12_0000));
        await ReadFrameAsync(stream, TimeSpan.FromSeconds(5)); // ER_New

        // Force a snapshot tick on the dispatcher and wait briefly for the
        // dispatcher thread to drain it onto snapSink.
        Assert.Single(host.Dispatchers);
        Assert.True(host.Dispatchers[0].EnqueueSnapshotTick());

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        byte[]? pkt = null;
        while (pkt is null && DateTime.UtcNow < deadline)
        {
            lock (snapSink.Packets)
            {
                if (snapSink.Packets.Count > 0)
                    pkt = (byte[])snapSink.Packets[0].Clone();
            }

            if (pkt is null)
                await Task.Delay(20);
        }

        Assert.True(pkt is not null, "snapshot packet not received");

        // PacketHeader sanity.
        ref readonly var hdr = ref MemoryMarshal.AsRef<B3.Umdf.Mbo.Sbe.V16.PacketHeader>(pkt.AsSpan(0, WireOffsets.PacketHeaderSize));
        Assert.Equal((byte)84, hdr.ChannelNumber);
        Assert.Equal((ushort)1, hdr.SequenceVersion);
        Assert.Equal(1u, hdr.SequenceNumber); // first snap packet on this channel

        // Decode the SnapshotFullRefresh_Header_30 — expect 2 bids, 0 offers,
        // and a non-null LastRptSeq (because the matching engine has emitted
        // 2 OrderAccepted incremental events → RptSeq ≥ 2).
        int frameOff = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
        Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.SnapshotFullRefresh_Header_30Data.TryParse(
            pkt.AsSpan(frameOff, WireOffsets.SnapHeaderBlockLength), out var snapHdr));
        Assert.Equal(Petr, (long)(ulong)snapHdr.Data.SecurityID);
        Assert.Equal(2u, snapHdr.Data.TotNumReports);
        Assert.Equal(2u, snapHdr.Data.TotNumBids);
        Assert.Equal(0u, snapHdr.Data.TotNumOffers);
        Assert.NotNull(snapHdr.Data.LastRptSeq);
        Assert.True(snapHdr.Data.LastRptSeq >= 2u);

        // Same packet must contain an Orders_71 frame with NumInGroup == 2.
        int after = frameOff + WireOffsets.SnapHeaderBlockLength;
        int groupNumInGroupOff = after
            + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SnapOrdersHeaderBlockLength + 2;
        Assert.Equal((byte)2, pkt[groupNumInGroupOff]);
    }

    [Fact]
    public async Task EmptyBook_PublishesIlliquidHeader()
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
                    IncrementalPort = 30186,
                    Ttl = 0,
                    InstrumentsFile = instrumentsPath,
                    Snapshot = new SnapshotChannelConfig
                    {
                        Group = "239.255.42.85",
                        Port = 30187,
                        Ttl = 0,
                        CadenceMs = 60_000,
                    },
                },
            },
        };
        var incSink = new RecordingPacketSink();
        var snapSink = new RecordingPacketSink();
        await using var host = new ExchangeHost(cfg,
            packetSinkFactory: _ => incSink,
            snapshotSinkFactory: (_, _) => snapSink);
        await host.StartAsync();

        Assert.True(host.Dispatchers[0].EnqueueSnapshotTick());

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        byte[]? pkt = null;
        while (pkt is null && DateTime.UtcNow < deadline)
        {
            lock (snapSink.Packets)
            {
                if (snapSink.Packets.Count > 0)
                    pkt = (byte[])snapSink.Packets[0].Clone();
            }

            if (pkt is null)
                await Task.Delay(20);
        }

        Assert.True(pkt is not null, "snapshot packet not received");
        int frameOff = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
        Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.SnapshotFullRefresh_Header_30Data.TryParse(
            pkt.AsSpan(frameOff, WireOffsets.SnapHeaderBlockLength), out var snapHdr));
        Assert.Equal(0u, snapHdr.Data.TotNumReports);
        Assert.Null(snapHdr.Data.LastRptSeq); // illiquid per B3 §7.4
    }

    private static byte[] BuildSimpleNewOrder(ulong clOrdId, long secId, char side, char ordType,
        char tif, long qty, long priceMantissa)
    {
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
