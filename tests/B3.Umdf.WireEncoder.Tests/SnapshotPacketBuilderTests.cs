using System.Runtime.InteropServices;
using B3.Umdf.Mbo.Sbe.V16;
using B3.Umdf.WireEncoder;

namespace B3.Umdf.WireEncoder.Tests;

/// <summary>
/// Tests for <see cref="SnapshotPacketBuilder.WriteSnapshot"/> orchestration:
/// header counts, chunking across packets, sequence-number stepping, and the
/// "buffer too small for a single entry" guard.
/// </summary>
public class SnapshotPacketBuilderTests
{
    private const int FrameOffset = WireOffsets.PacketHeaderSize
                                  + WireOffsets.FramingHeaderSize
                                  + WireOffsets.SbeMessageHeaderSize;

    private static UmdfWireEncoder.SnapshotEntry Bid(long px, long sz, long oid)
        => new(px, sz, (ulong)oid * 1000UL, oid, UmdfWireEncoder.MdEntryTypeBid);

    private static UmdfWireEncoder.SnapshotEntry Ask(long px, long sz, long oid)
        => new(px, sz, (ulong)oid * 1000UL, oid, UmdfWireEncoder.MdEntryTypeOffer);

    private sealed class CapturingHandler
    {
        public readonly List<byte[]> Packets = new();
        public void OnPacket(ReadOnlySpan<byte> p) => Packets.Add(p.ToArray());
    }

    [Fact]
    public void EmptyBook_EmitsSingleHeaderOnlyPacket()
    {
        var sink = new CapturingHandler();
        var buf = new byte[SnapshotPacketBuilder.DefaultPacketBufferSize];

        int packets = SnapshotPacketBuilder.WriteSnapshot(buf,
            channelNumber: 84, snapshotSequenceVersion: 0, firstSequenceNumber: 100,
            sendingTimeNanos: 1_700_000_000_000_000_000UL, securityId: 7L, lastRptSeq: null,
            incrementalSequenceVersion: 7,
            bids: ReadOnlySpan<UmdfWireEncoder.SnapshotEntry>.Empty,
            asks: ReadOnlySpan<UmdfWireEncoder.SnapshotEntry>.Empty,
            onPacket: sink.OnPacket);

        Assert.Equal(1, packets);
        Assert.Single(sink.Packets);

        // PacketHeader.SequenceNumber == 100
        ref readonly var hdr = ref MemoryMarshal.AsRef<PacketHeader>(sink.Packets[0].AsSpan(0, 16));
        Assert.Equal(100u, hdr.SequenceNumber);

        // Snapshot header parses with all-zero counts and null lastRptSeq.
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            sink.Packets[0].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var rdr));
        Assert.Equal(0u, rdr.Data.TotNumReports);
        Assert.Equal(0u, rdr.Data.TotNumBids);
        Assert.Equal(0u, rdr.Data.TotNumOffers);
        Assert.Null(rdr.Data.LastRptSeq);
        Assert.Equal((ushort)7, rdr.Data.LastSequenceVersion);
    }

    [Fact]
    public void SmallBook_SinglePacketCarriesHeaderAndOrders()
    {
        var sink = new CapturingHandler();
        var buf = new byte[SnapshotPacketBuilder.DefaultPacketBufferSize];

        var bids = new[] { Bid(100_0000L, 500L, 1), Bid(99_0000L, 200L, 2) };
        var asks = new[] { Ask(101_0000L, 300L, 3) };

        int packets = SnapshotPacketBuilder.WriteSnapshot(
            buf, 84, 0, 50, 1UL, 7L, lastRptSeq: 99u, incrementalSequenceVersion: 8,
            bids, asks, sink.OnPacket);

        Assert.Equal(1, packets);
        var pkt = sink.Packets[0];

        // Header counts derived from input spans.
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            pkt.AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var hdr));
        Assert.Equal(3u, hdr.Data.TotNumReports);
        Assert.Equal(2u, hdr.Data.TotNumBids);
        Assert.Equal(1u, hdr.Data.TotNumOffers);
        Assert.Equal(99u, hdr.Data.LastRptSeq);
        Assert.Equal((ushort)8, hdr.Data.LastSequenceVersion);

        // After the header frame, the same packet must contain the Orders_71 frame.
        int after = FrameOffset + WireOffsets.SnapHeaderBlockLength;
        ushort msgLen = MemoryMarshal.Read<ushort>(pkt.AsSpan(after, 2));
        int ordersFrameLen = WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
                           + WireOffsets.SnapOrdersHeaderBlockLength
                           + WireOffsets.SnapOrdersGroupSizeEncodingSize
                           + WireOffsets.SnapOrdersEntrySize * 3;
        Assert.Equal((ushort)ordersFrameLen, msgLen);

        // Group has all 3 entries.
        int groupOff = after + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
                     + WireOffsets.SnapOrdersHeaderBlockLength;
        Assert.Equal((byte)3, pkt[groupOff + 2]);
    }

    [Fact]
    public void BookLargerThanPacketBuffer_ChunksAcrossPacketsWithSequentialSeqNums()
    {
        var sink = new CapturingHandler();
        var buf = new byte[SnapshotPacketBuilder.DefaultPacketBufferSize];

        // 1,000 entries regress the old loop that repeatedly shrank a second
        // Orders_71 chunk until it reached one entry, then threw instead of
        // emitting the already-populated packet.
        var bids = new UmdfWireEncoder.SnapshotEntry[700];
        for (int i = 0; i < bids.Length; i++) bids[i] = Bid(100_0000L - i, 100L, i + 1);
        var asks = new UmdfWireEncoder.SnapshotEntry[300];
        for (int i = 0; i < asks.Length; i++) asks[i] = Ask(101_0000L + i, 100L, 10_000 + i);

        int packets = SnapshotPacketBuilder.WriteSnapshot(buf, 84, 0,
            firstSequenceNumber: 1000, sendingTimeNanos: 42UL, securityId: 7L, lastRptSeq: 12345u,
            incrementalSequenceVersion: 11,
            bids, asks, sink.OnPacket);

        Assert.Equal(
            SnapshotPacketBuilder.GetPacketCount(buf.Length, bids.Length, asks.Length),
            packets);
        Assert.True(packets >= 3);
        Assert.Equal(packets, sink.Packets.Count);

        for (int i = 0; i < packets; i++)
        {
            ref readonly var pkthdr = ref MemoryMarshal.AsRef<PacketHeader>(sink.Packets[i].AsSpan(0, 16));
            Assert.Equal((uint)(1000 + i), pkthdr.SequenceNumber);
            Assert.Equal((byte)84, pkthdr.ChannelNumber);
            Assert.Equal(42UL, pkthdr.SendingTime);
        }

        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            sink.Packets[0].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var hdr));
        Assert.Equal(1_000u, hdr.Data.TotNumReports);
        Assert.Equal(700u, hdr.Data.TotNumBids);
        Assert.Equal(300u, hdr.Data.TotNumOffers);

        // Walk both packets summing NumInGroup across every Orders_71 frame.
        int totalEntries = 0;
        for (int i = 0; i < packets; i++)
        {
            var pkt = sink.Packets[i];
            int p = WireOffsets.PacketHeaderSize;
            if (i == 0) p += WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.SnapHeaderBlockLength;
            while (p < pkt.Length)
            {
                ushort frameLen = MemoryMarshal.Read<ushort>(pkt.AsSpan(p, 2));
                if (frameLen == 0) break;
                int groupNumInGroupOff = p + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
                                       + WireOffsets.SnapOrdersHeaderBlockLength + 2;
                totalEntries += pkt[groupNumInGroupOff];
                p += frameLen;
            }
        }
        Assert.Equal(1_000, totalEntries);
        Assert.All(sink.Packets, packet => Assert.InRange(packet.Length, 1, buf.Length));
    }

    [Fact]
    public void BufferTooSmallForSingleEntry_Throws()
    {
        var sink = new CapturingHandler();
        // Buffer must fit packet 1 (PacketHeader 16 + Header_30 frame 46 = 62)
        // but be too small to add a 1-entry Orders_71 frame (16 + 65 = 81).
        // Use 70 bytes so packet 1 carries header only and packet 2 has
        // PacketHeader (16) + 54 bytes free, less than the 65 bytes a single
        // entry requires → builder must throw.
        var buf = new byte[70];

        var bids = new[] { Bid(1L, 1L, 1) };
        Assert.Throws<ArgumentException>(() =>
            SnapshotPacketBuilder.WriteSnapshot(buf, 84, 0, 0, 0, 7L, null, 1,
                bids, ReadOnlySpan<UmdfWireEncoder.SnapshotEntry>.Empty, sink.OnPacket));
    }

    [Fact]
    public void RespectsCustomMaxEntriesPerChunk()
    {
        var sink = new CapturingHandler();
        var buf = new byte[SnapshotPacketBuilder.DefaultPacketBufferSize];

        var bids = new UmdfWireEncoder.SnapshotEntry[10];
        for (int i = 0; i < bids.Length; i++) bids[i] = Bid(100L - i, 1L, i + 1);

        // cap=3 → expect 4 chunks (3,3,3,1). They all fit in one 16 KB buffer
        // alongside the snapshot header → 1 packet.
        int packets = SnapshotPacketBuilder.WriteSnapshot(
            buf, 84, 0, 0, 0, 7L, lastRptSeq: 1u, incrementalSequenceVersion: 1,
            bids, ReadOnlySpan<UmdfWireEncoder.SnapshotEntry>.Empty, sink.OnPacket,
            maxEntriesPerChunk: 3);

        Assert.Equal(1, packets);
        // Walk the packet and count Orders_71 frames + their NumInGroup.
        var pkt = sink.Packets[0];
        int p = WireOffsets.PacketHeaderSize;
        // Skip header frame.
        ushort firstFrameLen = MemoryMarshal.Read<ushort>(pkt.AsSpan(p, 2));
        Assert.Equal((ushort)(WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SnapHeaderBlockLength), firstFrameLen);
        p += firstFrameLen;

        var chunkSizes = new List<int>();
        while (p < pkt.Length)
        {
            ushort frameLen = MemoryMarshal.Read<ushort>(pkt.AsSpan(p, 2));
            if (frameLen == 0) break;
            int groupNumInGroupOff = p + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
                                  + WireOffsets.SnapOrdersHeaderBlockLength + 2;
            chunkSizes.Add(pkt[groupNumInGroupOff]);
            p += frameLen;
        }
        Assert.Equal(new[] { 3, 3, 3, 1 }, chunkSizes);
    }

    [Fact]
    public void RejectsInvalidMaxEntriesPerChunk()
    {
        var sink = new CapturingHandler();
        var buf = new byte[1024];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SnapshotPacketBuilder.WriteSnapshot(buf, 0, 0, 0, 0, 0, null, 1,
                ReadOnlySpan<UmdfWireEncoder.SnapshotEntry>.Empty,
                ReadOnlySpan<UmdfWireEncoder.SnapshotEntry>.Empty,
                sink.OnPacket, maxEntriesPerChunk: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SnapshotPacketBuilder.WriteSnapshot(buf, 0, 0, 0, 0, 0, null, 1,
                ReadOnlySpan<UmdfWireEncoder.SnapshotEntry>.Empty,
                ReadOnlySpan<UmdfWireEncoder.SnapshotEntry>.Empty,
                sink.OnPacket, maxEntriesPerChunk: 256));
    }
}
