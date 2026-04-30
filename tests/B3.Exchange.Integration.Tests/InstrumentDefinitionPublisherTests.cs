using System.Runtime.InteropServices;
using B3.Exchange.Instruments;
using B3.Exchange.Integration;
using B3.Umdf.Mbo.Sbe.V16;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Integration.Tests;

/// <summary>
/// Verifies that <see cref="InstrumentDefinitionPublisher"/> emits one
/// SecurityDefinition_12 frame per configured instrument, packs them into
/// ≤1400-byte packets, and uses monotonic SequenceNumbers across cycles.
/// </summary>
public class InstrumentDefinitionPublisherTests
{
    private const int FrameOffset = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
    private const int FrameSize = FrameOffset + WireOffsets.SecDefBodyTotal;

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            lock (Packets) Packets.Add(packet.ToArray());
        }
    }

    private static Instrument Inst(string sym, long secId, string isin, string type = "CS") => new()
    {
        Symbol = sym,
        SecurityId = secId,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000m,
        Currency = "BRL",
        Isin = isin,
        SecurityType = type,
    };

    [Fact]
    public void PublishOnce_EmitsOneFramePerInstrument_AcrossPackets()
    {
        var instruments = new List<Instrument>
        {
            Inst("PETR4", 900_000_000_001L, "BRPETRACNPR6"),
            Inst("VALE3", 900_000_000_002L, "BRVALEACNOR0"),
            Inst("ITUB4", 900_000_000_003L, "BRITUBACNPR1"),
        };
        var sink = new RecordingPacketSink();
        var pub = new InstrumentDefinitionPublisher(
            channelNumber: 184, instruments, sink,
            cadence: TimeSpan.FromSeconds(60), nowNanos: () => 12345UL);

        pub.PublishOnce();

        Assert.NotEmpty(sink.Packets);

        var seenSecIds = new List<long>();
        var seenSymbols = new List<string>();
        uint expectedSeq = 1;

        foreach (var packet in sink.Packets)
        {
            Assert.True(packet.Length <= InstrumentDefinitionPublisher.MaxPacketBytes);

            // PacketHeader: channel & sequence checks.
            Assert.Equal((byte)184, packet[WireOffsets.PacketHeaderChannelOffset]);
            uint seq = MemoryMarshal.Read<uint>(
                packet.AsSpan(WireOffsets.PacketHeaderSequenceNumberOffset, 4));
            Assert.Equal(expectedSeq++, seq);

            int p = WireOffsets.PacketHeaderSize;
            while (p + FrameSize <= packet.Length)
            {
                var bodySpan = packet.AsSpan(p + FrameOffset, WireOffsets.SecDefBlockLength);
                long secId = MemoryMarshal.Read<long>(bodySpan.Slice(WireOffsets.SecDefSecurityIdOffset, 8));
                seenSecIds.Add(secId);
                var symBytes = bodySpan.Slice(WireOffsets.SecDefSymbolOffset, 20);
                int n = symBytes.IndexOf((byte)0);
                if (n < 0) n = symBytes.Length;
                seenSymbols.Add(System.Text.Encoding.ASCII.GetString(symBytes.Slice(0, n)));
                p += FrameSize;
            }
        }

        Assert.Equal(new[] { 900_000_000_001L, 900_000_000_002L, 900_000_000_003L }, seenSecIds);
        Assert.Equal(new[] { "PETR4", "VALE3", "ITUB4" }, seenSymbols);
    }

    [Fact]
    public void PublishOnce_FramesAreSbeReadable()
    {
        var instruments = new List<Instrument> { Inst("PETR4", 900_000_000_001L, "BRPETRACNPR6") };
        var sink = new RecordingPacketSink();
        var pub = new InstrumentDefinitionPublisher(
            channelNumber: 200, instruments, sink, TimeSpan.FromMinutes(1), () => 1UL);

        pub.PublishOnce();

        Assert.Single(sink.Packets);
        var packet = sink.Packets[0];
        var bodySpan = packet.AsSpan(WireOffsets.PacketHeaderSize + FrameOffset, WireOffsets.SecDefBlockLength);

        Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.SecurityDefinition_12Data.TryParse(bodySpan, out var rdr));
        Assert.Equal(900_000_000_001L, (long)rdr.Data.SecurityID.Value);
    }

    [Fact]
    public void PublishOnce_SequenceNumbersAreMonotonicAcrossCycles()
    {
        var instruments = new List<Instrument>
        {
            Inst("PETR4", 1L, "BRPETRACNPR6"),
        };
        var sink = new RecordingPacketSink();
        var pub = new InstrumentDefinitionPublisher(
            channelNumber: 99, instruments, sink, TimeSpan.FromMinutes(1), () => 0UL);

        pub.PublishOnce();
        pub.PublishOnce();
        pub.PublishOnce();

        Assert.Equal(3, sink.Packets.Count);
        for (int i = 0; i < 3; i++)
        {
            uint seq = MemoryMarshal.Read<uint>(
                sink.Packets[i].AsSpan(WireOffsets.PacketHeaderSequenceNumberOffset, 4));
            Assert.Equal((uint)(i + 1), seq);
        }
        Assert.Equal(3u, pub.SequenceNumber);
    }

    [Fact]
    public void PublishOnce_PacksManyInstrumentsIntoMultiplePackets()
    {
        // ~5 SecDef frames per packet (251 bytes each + 16-byte header < 1400);
        // 12 instruments → at least 3 packets.
        var instruments = new List<Instrument>();
        for (int i = 0; i < 12; i++)
        {
            instruments.Add(Inst($"SYM{i:00}", 1_000_000L + i, "BR" + i.ToString("D10")));
        }
        var sink = new RecordingPacketSink();
        var pub = new InstrumentDefinitionPublisher(
            channelNumber: 7, instruments, sink, TimeSpan.FromMinutes(1), () => 0UL);

        pub.PublishOnce();

        Assert.True(sink.Packets.Count >= 3, $"expected ≥3 packets, got {sink.Packets.Count}");
        int totalFrames = 0;
        foreach (var p in sink.Packets)
        {
            int frames = (p.Length - WireOffsets.PacketHeaderSize) / FrameSize;
            totalFrames += frames;
        }
        Assert.Equal(12, totalFrames);
    }

    [Fact]
    public async Task Start_FiresImmediatelyAndPeriodically()
    {
        var instruments = new List<Instrument> { Inst("PETR4", 1L, "BRPETRACNPR6") };
        var sink = new RecordingPacketSink();
        await using var pub = new InstrumentDefinitionPublisher(
            channelNumber: 1, instruments, sink, TimeSpan.FromMilliseconds(50), () => 0UL);

        pub.Start();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (sink.Packets)
            {
                if (sink.Packets.Count >= 2) break;
            }
            await Task.Delay(20);
        }
        lock (sink.Packets)
        {
            Assert.True(sink.Packets.Count >= 2,
                $"expected ≥2 packets within 2s, got {sink.Packets.Count}");
        }
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCadence()
    {
        var instruments = new List<Instrument> { Inst("X", 1L, "ISIN0001") };
        var sink = new RecordingPacketSink();
        Assert.Throws<ArgumentOutOfRangeException>(() => new InstrumentDefinitionPublisher(
            1, instruments, sink, TimeSpan.Zero));
    }
}
