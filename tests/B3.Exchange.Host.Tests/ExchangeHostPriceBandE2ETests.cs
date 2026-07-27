using System.Runtime.InteropServices;
using B3.Exchange.Core;
using B3.Exchange.TestSupport;
using B3.Umdf.Mbo.Sbe.V16;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Host.Tests;

public class ExchangeHostPriceBandE2ETests
{
    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            lock (Packets) Packets.Add(packet.ToArray());
        }
    }

    [Fact]
    public async Task PriceBandTick_WithConfiguredBands_PublishesPriceBandOnIncrementalFeed()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("tests/B3.Exchange.Host.Tests/Data/instruments-priceband.json");
        var cfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
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
                    PriceBandPublishIntervalMs = 60_000,
                },
            },
        };
        var incSink = new RecordingPacketSink();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => incSink);
        await host.StartAsync();

        Assert.Single(host.Dispatchers);
        Assert.NotNull(host.Dispatchers[0].PriceBandPublisher);
        Assert.True(host.Dispatchers[0].EnqueuePriceBandTick());

        byte[]? pkt = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (pkt is null && DateTime.UtcNow < deadline)
        {
            lock (incSink.Packets)
            {
                if (incSink.Packets.Count > 0)
                    pkt = (byte[])incSink.Packets[0].Clone();
            }

            if (pkt is null)
                await Task.Delay(20);
        }

        Assert.True(pkt is not null, "price-band packet not received");
        ref readonly var hdr = ref MemoryMarshal.AsRef<PacketHeader>(pkt.AsSpan(0, WireOffsets.PacketHeaderSize));
        Assert.Equal((byte)84, hdr.ChannelNumber);
        Assert.Equal((ushort)1, hdr.SequenceVersion);
        Assert.Equal(1u, hdr.SequenceNumber);

        int frameOff = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
        Assert.True(PriceBand_22Data.TryParse(
            pkt.AsSpan(frameOff, WireOffsets.PriceBandBlockLength), out var rdr));
        Assert.Equal(900000000001L, (long)(ulong)rdr.Data.SecurityID.Value);
        Assert.Equal(115_000L, rdr.Data.LowLimitPrice.Mantissa);
        Assert.Equal(182_500L, rdr.Data.HighLimitPrice.Mantissa);
        Assert.Equal(1u, rdr.Data.RptSeq);
    }

    [Fact]
    public async Task PriceBandTick_WithoutConfiguredBands_IsSkipped()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var cfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
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
                    PriceBandPublishIntervalMs = 60_000,
                },
            },
        };
        var incSink = new RecordingPacketSink();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => incSink);
        await host.StartAsync();

        Assert.Single(host.Dispatchers);
        Assert.Null(host.Dispatchers[0].PriceBandPublisher);
        Assert.False(host.Dispatchers[0].EnqueuePriceBandTick());
        await Task.Delay(100);
        lock (incSink.Packets)
        {
            Assert.Empty(incSink.Packets);
        }
    }
}
