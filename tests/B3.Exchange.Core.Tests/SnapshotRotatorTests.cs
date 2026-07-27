using B3.Exchange.Contracts;
using Side = B3.Exchange.Matching.Side;
using RejectEvent = B3.Exchange.Matching.RejectEvent;
using OrderType = B3.Exchange.Matching.OrderType;
using TimeInForce = B3.Exchange.Matching.TimeInForce;
using System.Runtime.InteropServices;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using B3.Exchange.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using B3.Umdf.Mbo.Sbe.V16;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Core.Tests;

/// <summary>
/// Unit tests for <see cref="SnapshotRotator"/> using a hand-rolled
/// <see cref="ISnapshotBookSource"/>. These verify rotation order, packet
/// header fields, the empty-illiquid case, and snap-channel sequence
/// stepping. End-to-end integration with a real <see cref="MatchingEngine"/>
/// is covered by <c>ExchangeHostE2ETests</c>.
/// </summary>
public class SnapshotRotatorTests
{
    private const int PacketHeaderSize = WireOffsets.PacketHeaderSize;
    private const int FrameOffset = PacketHeaderSize
                                    + WireOffsets.FramingHeaderSize
                                    + WireOffsets.SbeMessageHeaderSize;

    private sealed class FakeSource : ISnapshotBookSource
    {
        public IReadOnlyList<long> SecurityIds { get; init; } = Array.Empty<long>();
        public Dictionary<long, uint> RptSeqBySecurity { get; } = new();
        public Dictionary<long, (byte Status, byte Event)> SecurityStatusBySecurity { get; } = new();
        public Dictionary<(long, Side), List<RestingOrderView>> Books { get; } = new();

        public uint GetCurrentRptSeq(long securityId)
            => RptSeqBySecurity.TryGetValue(securityId, out uint rptSeq) ? rptSeq : 0;

        public IEnumerable<RestingOrderView> EnumerateBook(long securityId, Side side)
            => Books.TryGetValue((securityId, side), out var l) ? l : Enumerable.Empty<RestingOrderView>();

        public (byte SecurityTradingStatus, byte SecurityTradingEvent) GetCurrentSecurityStatus(long securityId)
            => SecurityStatusBySecurity.TryGetValue(securityId, out var status)
                ? status
                : ((byte)SecurityTradingStatus.OPEN, byte.MaxValue);
    }

    private sealed class CapturingSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Packets.Add(packet.ToArray());
    }

    private static RestingOrderView Order(long oid, Side side, long px, long qty, ulong nanos = 1_000UL, uint firm = 7)
        => new(oid, side, px, qty, firm, nanos);

    private static SecurityStatus_3DataReader ReadSecurityStatusPacket(byte[] packet)
    {
        Assert.True(SecurityStatus_3Data.TryParse(
            packet.AsSpan(FrameOffset, WireOffsets.SecurityStatusBlockLength),
            out var status));
        return status;
    }

    [Fact]
    public void EmptyBook_NoPriorIncrementals_EmitsIlliquidHeaderOnly()
    {
        var src = new FakeSource { SecurityIds = new[] { 42L } };
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 84, source: src, sink: sink, timeSource: new FakeNanosTimeSource(1234UL));

        int packets = rot.PublishNext(incrementalSequenceVersion: 3);

        Assert.Equal(2, packets);
        Assert.Equal(2, sink.Packets.Count);
        Assert.Equal(2u, rot.SequenceNumber);

        var pkt = sink.Packets[0];
        ref readonly var hdr = ref MemoryMarshal.AsRef<PacketHeader>(pkt.AsSpan(0, PacketHeaderSize));
        Assert.Equal((byte)84, hdr.ChannelNumber);
        Assert.Equal((ushort)1, hdr.SequenceVersion);
        Assert.Equal(1u, hdr.SequenceNumber);
        Assert.Equal(1234UL, hdr.SendingTime);

        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            pkt.AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var snapHdr));
        Assert.Equal(42L, (long)(ulong)snapHdr.Data.SecurityID);
        Assert.Equal(0u, snapHdr.Data.TotNumReports);
        Assert.Null(snapHdr.Data.LastRptSeq); // illiquid: §7.4
        Assert.Equal((ushort)3, snapHdr.Data.LastSequenceVersion);

        var status = ReadSecurityStatusPacket(sink.Packets[1]);
        Assert.Equal(SecurityTradingStatus.OPEN, status.Data.SecurityTradingStatus);
        Assert.Null(status.Data.SecurityTradingEvent);
        Assert.Null(status.Data.RptSeq);
    }

    [Fact]
    public void NonEmptyBook_BuildsHeaderPlusOrdersFrame_WithLastRptSeq()
    {
        var src = new FakeSource { SecurityIds = new[] { 42L } };
        src.RptSeqBySecurity[42L] = 17;
        src.Books[(42L, Side.Buy)] = new()
        {
            Order(1, Side.Buy, 100_0000, 500),
            Order(2, Side.Buy, 99_5000, 200),
        };
        src.Books[(42L, Side.Sell)] = new()
        {
            Order(3, Side.Sell, 101_0000, 300),
        };

        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 84, source: src, sink: sink);

        int packets = rot.PublishNext(incrementalSequenceVersion: 4);
        Assert.Equal(2, packets);
        Assert.Equal(2, sink.Packets.Count);

        var pkt = sink.Packets[0];
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            pkt.AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var snapHdr));
        Assert.Equal(3u, snapHdr.Data.TotNumReports);
        Assert.Equal(2u, snapHdr.Data.TotNumBids);
        Assert.Equal(1u, snapHdr.Data.TotNumOffers);
        Assert.Equal(17u, snapHdr.Data.LastRptSeq);
        Assert.Equal((ushort)4, snapHdr.Data.LastSequenceVersion);

        // Same packet must carry an Orders_71 frame with NumInGroup == 3.
        int after = FrameOffset + WireOffsets.SnapHeaderBlockLength;
        int groupNumInGroupOff = after
            + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SnapOrdersHeaderBlockLength + 2;
        Assert.Equal((byte)3, pkt[groupNumInGroupOff]);

        var status = ReadSecurityStatusPacket(sink.Packets[1]);
        Assert.Equal(SecurityTradingStatus.OPEN, status.Data.SecurityTradingStatus);
        Assert.Null(status.Data.SecurityTradingEvent);
        Assert.Equal(17u, status.Data.RptSeq);
    }

    [Fact]
    public void RoundRobinsThroughInstruments_AndAdvancesSnapSequence()
    {
        var src = new FakeSource { SecurityIds = new[] { 11L, 22L, 33L } };
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 5, source: src, sink: sink);

        for (int i = 0; i < 4; i++) rot.PublishNext(incrementalSequenceVersion: 1); // wraps after 3

        Assert.Equal(8, sink.Packets.Count);
        Assert.Equal(8u, rot.SequenceNumber);

        // SecurityIDs read out of each header packet must follow the rotation
        // order 11, 22, 33, 11.
        long[] expected = new[] { 11L, 22L, 33L, 11L };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
                sink.Packets[i * 2].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var hdr));
            Assert.Equal(expected[i], (long)(ulong)hdr.Data.SecurityID);
            ref readonly var packetHdr = ref MemoryMarshal.AsRef<PacketHeader>(sink.Packets[i * 2].AsSpan(0, PacketHeaderSize));
            Assert.Equal((uint)(2 * i + 1), packetHdr.SequenceNumber);
        }
    }

    [Fact]
    public void PublishFor_UsesExactTargetSecurityRptSeq()
    {
        var src = new FakeSource { SecurityIds = new[] { 11L, 22L, 33L } };
        src.RptSeqBySecurity[11] = 7;
        src.RptSeqBySecurity[22] = 2;
        src.RptSeqBySecurity[33] = 11;
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 5, source: src, sink: sink);

        rot.PublishFor(22, incrementalSequenceVersion: 4);
        rot.PublishFor(11, incrementalSequenceVersion: 4);
        rot.PublishFor(33, incrementalSequenceVersion: 4);

        uint[] expected = [2, 7, 11];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
                sink.Packets[i * 2].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength),
                out var header));
            Assert.Equal(expected[i], header.Data.LastRptSeq);
        }
    }

    [Fact]
    public void PublishFor_EmitsCurrentSecurityStatusPacketWithoutAdvancingRptSeq()
    {
        var src = new FakeSource { SecurityIds = new[] { 42L, 43L } };
        src.RptSeqBySecurity[42L] = 17;
        src.RptSeqBySecurity[43L] = 21;
        src.SecurityStatusBySecurity[42L] = ((byte)SecurityTradingStatus.FORBIDDEN, (byte)SecurityTradingEvent.SECURITY_STATUS_CHANGE);
        src.SecurityStatusBySecurity[43L] = ((byte)SecurityTradingStatus.RESERVED, byte.MaxValue);
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 5, source: src, sink: sink);

        rot.PublishFor(42L, incrementalSequenceVersion: 4);
        rot.PublishFor(43L, incrementalSequenceVersion: 4);

        var halted = ReadSecurityStatusPacket(sink.Packets[1]);
        Assert.Equal(SecurityTradingStatus.FORBIDDEN, halted.Data.SecurityTradingStatus);
        Assert.Equal(SecurityTradingEvent.SECURITY_STATUS_CHANGE, halted.Data.SecurityTradingEvent);
        Assert.Equal(17u, halted.Data.RptSeq);

        var active = ReadSecurityStatusPacket(sink.Packets[3]);
        Assert.Equal(SecurityTradingStatus.RESERVED, active.Data.SecurityTradingStatus);
        Assert.Null(active.Data.SecurityTradingEvent);
        Assert.Equal(21u, active.Data.RptSeq);
    }

    [Fact]
    public void LargeBookAboveLegacyBufferLimit_PublishesCompleteSnapshotAndStepsSequence()
    {
        var src = new FakeSource { SecurityIds = new[] { 7L } };
        src.RptSeqBySecurity[7L] = 99;
        var bids = new List<RestingOrderView>();
        for (int i = 0; i < 20_000; i++) bids.Add(Order(i + 1, Side.Buy, 100_0000 - i, 100));
        var asks = new List<RestingOrderView>();
        for (int i = 0; i < 5_000; i++) asks.Add(Order(100_000 + i, Side.Sell, 101_0000 + i, 100));
        src.Books[(7L, Side.Buy)] = bids;
        src.Books[(7L, Side.Sell)] = asks;

        var sink = new CapturingSink();
        var rot = new SnapshotRotator(
            channelNumber: 84,
            source: src,
            sink: sink,
            timeSource: new FakeNanosTimeSource(123_456UL));

        int snapshotPackets = SnapshotPacketBuilder.GetPacketCount(
            SnapshotPacketBuilder.DefaultPacketBufferSize, 20_000, 5_000);
        int packets = rot.PublishNext(incrementalSequenceVersion: 17);
        Assert.Equal(snapshotPackets + 1, packets);
        Assert.Equal((uint)packets, rot.SequenceNumber);

        for (int i = 0; i < snapshotPackets; i++)
        {
            ref readonly var pkthdr = ref MemoryMarshal.AsRef<PacketHeader>(sink.Packets[i].AsSpan(0, PacketHeaderSize));
            Assert.Equal((uint)(i + 1), pkthdr.SequenceNumber);
            Assert.Equal((ushort)1, pkthdr.SequenceVersion);
            Assert.Equal(123_456UL, pkthdr.SendingTime);
            Assert.InRange(sink.Packets[i].Length, 1, SnapshotPacketBuilder.DefaultPacketBufferSize);
        }

        ref readonly var statusPacketHdr = ref MemoryMarshal.AsRef<PacketHeader>(sink.Packets[^1].AsSpan(0, PacketHeaderSize));
        Assert.Equal((uint)(snapshotPackets + 1), statusPacketHdr.SequenceNumber);

        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            sink.Packets[0].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var header));
        Assert.Equal(25_000u, header.Data.TotNumReports);
        Assert.Equal(20_000u, header.Data.TotNumBids);
        Assert.Equal(5_000u, header.Data.TotNumOffers);
        Assert.Equal(99u, header.Data.LastRptSeq);
        Assert.Equal((ushort)17, header.Data.LastSequenceVersion);

        int totalEntries = 0;
        for (int i = 0; i < snapshotPackets; i++)
        {
            var pkt = sink.Packets[i];
            int p = PacketHeaderSize;
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
        Assert.Equal(25_000, totalEntries);

        var status = ReadSecurityStatusPacket(sink.Packets[^1]);
        Assert.Equal(99u, status.Data.RptSeq);
    }

    [Fact]
    public void MultiPacketAtSequenceBoundary_UsesMaxThenBumpsEpochWithoutWrapping()
    {
        var src = new FakeSource { SecurityIds = new[] { 7L } };
        src.RptSeqBySecurity[7L] = 99;
        src.Books[(7L, Side.Buy)] = Enumerable.Range(1, 700)
            .Select(i => Order(i, Side.Buy, 100_0000 - i, 100))
            .ToList();
        src.Books[(7L, Side.Sell)] = Enumerable.Range(1, 300)
            .Select(i => Order(10_000 + i, Side.Sell, 101_0000 + i, 100))
            .ToList();
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 84, source: src, sink: sink);
        int requiredSnapshotPackets = SnapshotPacketBuilder.GetPacketCount(
            SnapshotPacketBuilder.DefaultPacketBufferSize, 700, 300);
        int requiredPackets = requiredSnapshotPackets + 1;
        Assert.True(requiredSnapshotPackets > 1);
        rot.CreateTestProbe().SetSequence(
            version: 7,
            number: uint.MaxValue - (uint)requiredPackets);

        int firstPublishPackets = rot.PublishNext(incrementalSequenceVersion: 55);

        Assert.Equal(requiredPackets, firstPublishPackets);
        Assert.Equal((ushort)7, rot.SequenceVersion);
        Assert.Equal(uint.MaxValue, rot.SequenceNumber);
        for (int i = 0; i < requiredSnapshotPackets; i++)
        {
            ref readonly var header = ref MemoryMarshal.AsRef<PacketHeader>(
                sink.Packets[i].AsSpan(0, PacketHeaderSize));
            Assert.Equal((ushort)7, header.SequenceVersion);
            Assert.Equal(uint.MaxValue - (uint)requiredPackets + (uint)i + 1, header.SequenceNumber);
            Assert.NotEqual(0u, header.SequenceNumber);
        }
        ref readonly var firstStatusHeader = ref MemoryMarshal.AsRef<PacketHeader>(
            sink.Packets[requiredSnapshotPackets].AsSpan(0, PacketHeaderSize));
        Assert.Equal((ushort)7, firstStatusHeader.SequenceVersion);
        Assert.Equal(uint.MaxValue, firstStatusHeader.SequenceNumber);

        int secondPublishPackets = rot.PublishNext(incrementalSequenceVersion: 55);

        Assert.Equal(requiredPackets, secondPublishPackets);
        Assert.Equal((ushort)8, rot.SequenceVersion);
        Assert.Equal((uint)requiredPackets, rot.SequenceNumber);
        for (int i = 0; i < requiredSnapshotPackets; i++)
        {
            ref readonly var header = ref MemoryMarshal.AsRef<PacketHeader>(
                sink.Packets[requiredPackets + i].AsSpan(0, PacketHeaderSize));
            Assert.Equal((ushort)8, header.SequenceVersion);
            Assert.Equal((uint)(i + 1), header.SequenceNumber);
            Assert.NotEqual(0u, header.SequenceNumber);
        }
        ref readonly var secondStatusHeader = ref MemoryMarshal.AsRef<PacketHeader>(
            sink.Packets[(requiredPackets * 2) - 1].AsSpan(0, PacketHeaderSize));
        Assert.Equal((ushort)8, secondStatusHeader.SequenceVersion);
        Assert.Equal((uint)requiredPackets, secondStatusHeader.SequenceNumber);
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            sink.Packets[requiredPackets].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength),
            out var snapshotHeader));
        Assert.Equal((ushort)55, snapshotHeader.Data.LastSequenceVersion);
    }

    [Fact]
    public void MultiPacketAtTerminalEpoch_FailsBeforePublishingAnyFragment()
    {
        var src = new FakeSource { SecurityIds = new[] { 7L } };
        src.RptSeqBySecurity[7L] = 99;
        src.Books[(7L, Side.Buy)] = Enumerable.Range(1, 1_000)
            .Select(i => Order(i, Side.Buy, 100_0000 - i, 100))
            .ToList();
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 84, source: src, sink: sink);
        rot.CreateTestProbe().SetSequence(ushort.MaxValue, uint.MaxValue - 1);

        var error = Assert.Throws<InvalidOperationException>(
            () => rot.PublishNext(incrementalSequenceVersion: 55));

        Assert.Contains("SequenceVersion space is exhausted", error.Message);
        Assert.Empty(sink.Packets);
        Assert.Equal(ushort.MaxValue, rot.SequenceVersion);
        Assert.Equal(uint.MaxValue - 1, rot.SequenceNumber);
    }

    [Fact]
    public void BumpSequenceVersion_DoesNotChangeIncrementalLastSequenceVersion()
    {
        var src = new FakeSource { SecurityIds = new[] { 1L } };
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 1, source: src, sink: sink);

        rot.PublishNext(incrementalSequenceVersion: 9);
        rot.PublishNext(incrementalSequenceVersion: 9);
        Assert.Equal(4u, rot.SequenceNumber);
        Assert.Equal((ushort)1, rot.SequenceVersion);

        rot.BumpSequenceVersion();

        Assert.Equal((ushort)2, rot.SequenceVersion);
        Assert.Equal(0u, rot.SequenceNumber);

        rot.PublishNext(incrementalSequenceVersion: 9);
        ref readonly var hdr = ref MemoryMarshal.AsRef<PacketHeader>(sink.Packets[^2].AsSpan(0, PacketHeaderSize));
        Assert.Equal((ushort)2, hdr.SequenceVersion);
        Assert.Equal(1u, hdr.SequenceNumber);
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            sink.Packets[^2].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var snapHdr));
        Assert.Equal((ushort)9, snapHdr.Data.LastSequenceVersion);
    }

    [Fact]
    public void BookWithRptSeqButNoOrders_PublishesHeaderWithLastRptSeq()
    {
        // Liquid history but currently empty book (e.g. all orders matched).
        // Per scope: only the no-history case is illiquid; otherwise stamp
        // the live RptSeq.
        var src = new FakeSource { SecurityIds = new[] { 42L } };
        src.RptSeqBySecurity[42L] = 5;
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 1, source: src, sink: sink);

        rot.PublishNext(incrementalSequenceVersion: 1);

        var pkt = sink.Packets[0];
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            pkt.AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var hdr));
        Assert.Equal(0u, hdr.Data.TotNumReports);
        Assert.Equal(5u, hdr.Data.LastRptSeq);
        Assert.Equal((ushort)1, hdr.Data.LastSequenceVersion);

        var status = ReadSecurityStatusPacket(sink.Packets[1]);
        Assert.Equal(5u, status.Data.RptSeq);
    }
}

/// <summary>
/// Verifies that <see cref="ChannelDispatcher.EnqueueSnapshotTick"/> drives
/// the attached <see cref="SnapshotRotator"/> on the dispatcher thread (no
/// concurrency with order processing).
/// </summary>
public class ChannelDispatcherSnapshotTests
{
    private const int PacketHeaderSize = WireOffsets.PacketHeaderSize;
    private const int FrameOffset = PacketHeaderSize
                                    + WireOffsets.FramingHeaderSize
                                    + WireOffsets.SbeMessageHeaderSize;
    private const long Petr = 900_000_000_001L;
    private static B3.Exchange.Instruments.Instrument Petr4 => new()
    {
        Symbol = "PETR4",
        SecurityId = Petr,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000m,
        Currency = "BRL",
        Isin = "BRPETRACNPR6",
        SecurityType = "EQUITY",
    };

    private sealed class CapturingSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Packets.Add(packet.ToArray());
    }

    private static SecurityStatus_3DataReader ReadSecurityStatusPacket(byte[] packet)
    {
        Assert.True(SecurityStatus_3Data.TryParse(
            packet.AsSpan(FrameOffset, WireOffsets.SecurityStatusBlockLength),
            out var status));
        return status;
    }

    [Fact]
    public void SnapshotTick_PublishedOnDispatcherThread_AndIsolatedFromIncrementalSink()
    {
        var incSink = new CapturingSink();
        var snapSink = new CapturingSink();
        MatchingEngine? engine = null;
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: s => { engine = new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance); return engine; },
            options: new ChannelDispatcherOptions
            {
                PacketSink = incSink,
                Outbound = new ChannelDispatcherTests_RecordingOutbound(),
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1UL),
                TradeDate = 1,
            });
        var rotator = new SnapshotRotator(channelNumber: 1,
            source: new MatchingEngineSnapshotSource(engine!, new[] { Petr }),
            sink: snapSink, timeSource: new FakeNanosTimeSource(1UL));
        disp.AttachSnapshotRotator(rotator);

        // Synchronously drive: enqueue tick → drain.
        Assert.True(disp.EnqueueSnapshotTick());
        Drain(disp);

        Assert.Equal(2, snapSink.Packets.Count);  // snapshot header + security status
        Assert.Empty(incSink.Packets);            // incremental sink untouched
        Assert.Equal(2u, rotator.SequenceNumber); // snap-channel seq advanced
        Assert.Equal(0u, disp.SequenceNumber);    // inc-channel seq untouched
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            snapSink.Packets[0].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var snapshotHeader));
        Assert.Equal(disp.SequenceVersion, snapshotHeader.Data.LastSequenceVersion);
    }

    [Fact]
    public void SnapshotTick_ReadsLiveRestingOrders_FromMatchingEngine()
    {
        var incSink = new CapturingSink();
        var snapSink = new CapturingSink();
        MatchingEngine? engine = null;
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: s => { engine = new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance); return engine; },
            options: new ChannelDispatcherOptions
            {
                PacketSink = incSink,
                Outbound = new ChannelDispatcherTests_RecordingOutbound(),
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1UL),
                TradeDate = 1,
            });
        var rotator = new SnapshotRotator(channelNumber: 1,
            source: new MatchingEngineSnapshotSource(engine!, new[] { Petr }),
            sink: snapSink, timeSource: new FakeNanosTimeSource(1UL));
        disp.AttachSnapshotRotator(rotator);

        // Submit a couple of resting orders so the snapshot has content.
        var session = new B3.Exchange.Contracts.SessionId("test");
        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, 10_0000, 100, 7, 1UL),
            session, enteringFirm: 7, clOrdIdValue: 1UL);
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, 9_0000, 200, 7, 2UL),
            session, enteringFirm: 7, clOrdIdValue: 2UL);
        disp.EnqueueNewOrder(new NewOrderCommand("3", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, 11_0000, 300, 7, 3UL),
            session, enteringFirm: 7, clOrdIdValue: 3UL);
        Drain(disp);
        snapSink.Packets.Clear();

        // Now request the snapshot.
        Assert.True(disp.EnqueueSnapshotTick());
        Drain(disp);

        Assert.Equal(2, snapSink.Packets.Count);
        var pkt = snapSink.Packets[0];
        int frameOff = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            pkt.AsSpan(frameOff, WireOffsets.SnapHeaderBlockLength), out var hdr));
        Assert.Equal(3u, hdr.Data.TotNumReports);
        Assert.Equal(2u, hdr.Data.TotNumBids);
        Assert.Equal(1u, hdr.Data.TotNumOffers);
        // After 3 OrderAccepted events the engine's RptSeq is 3.
        Assert.Equal(3u, hdr.Data.LastRptSeq);
        Assert.Equal(disp.SequenceVersion, hdr.Data.LastSequenceVersion);
    }

    [Fact]
    public void SnapshotTick_PublishesCurrentSecurityStatusWithoutAdvancingIncrementalRptSeq()
    {
        var incSink = new CapturingSink();
        var snapSink = new CapturingSink();
        MatchingEngine? engine = null;
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: s => { engine = new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance); return engine; },
            options: new ChannelDispatcherOptions
            {
                PacketSink = incSink,
                Outbound = new ChannelDispatcherTests_RecordingOutbound(),
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1UL),
                TradeDate = 1,
            });
        var rotator = new SnapshotRotator(channelNumber: 1,
            source: new MatchingEngineSnapshotSource(engine!, new[] { Petr }),
            sink: snapSink, timeSource: new FakeNanosTimeSource(1UL));
        disp.AttachSnapshotRotator(rotator);

        Assert.True(disp.EnqueueOperatorSetTradingPhase(Petr, TradingPhase.Reserved));
        Drain(disp);
        uint activeRptSeq = engine!.GetCurrentRptSeq(Petr);
        snapSink.Packets.Clear();

        Assert.True(disp.EnqueueSnapshotTick());
        Drain(disp);

        var activeStatus = ReadSecurityStatusPacket(snapSink.Packets[1]);
        Assert.Equal(SecurityTradingStatus.RESERVED, activeStatus.Data.SecurityTradingStatus);
        Assert.Null(activeStatus.Data.SecurityTradingEvent);
        Assert.Equal(activeRptSeq, activeStatus.Data.RptSeq);
        Assert.Equal(activeRptSeq, engine.GetCurrentRptSeq(Petr));

        Assert.True(disp.EnqueueOperatorHalt(Petr, HaltReason.RegulatoryHalt, null));
        Drain(disp);
        uint haltedRptSeq = engine.GetCurrentRptSeq(Petr);
        snapSink.Packets.Clear();

        Assert.True(disp.EnqueueSnapshotTick());
        Drain(disp);

        var snapshotHeader = snapSink.Packets[0];
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            snapshotHeader.AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var hdr));
        Assert.Equal(haltedRptSeq, hdr.Data.LastRptSeq);

        var haltedStatus = ReadSecurityStatusPacket(snapSink.Packets[1]);
        Assert.Equal(SecurityTradingStatus.FORBIDDEN, haltedStatus.Data.SecurityTradingStatus);
        Assert.Equal(SecurityTradingEvent.SECURITY_STATUS_CHANGE, haltedStatus.Data.SecurityTradingEvent);
        Assert.Equal(haltedRptSeq, haltedStatus.Data.RptSeq);
        Assert.Equal(haltedRptSeq, engine.GetCurrentRptSeq(Petr));
    }

    [Fact]
    public void SnapshotTick_ReflectsIncrementalVersionBump()
    {
        var incSink = new CapturingSink();
        var snapSink = new CapturingSink();
        MatchingEngine? engine = null;
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: s => { engine = new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance); return engine; },
            options: new ChannelDispatcherOptions
            {
                PacketSink = incSink,
                Outbound = new ChannelDispatcherTests_RecordingOutbound(),
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1UL),
                TradeDate = 1,
            });
        var rotator = new SnapshotRotator(channelNumber: 1,
            source: new MatchingEngineSnapshotSource(engine!, new[] { Petr }),
            sink: snapSink, timeSource: new FakeNanosTimeSource(1UL));
        disp.AttachSnapshotRotator(rotator);

        Assert.True(disp.EnqueueOperatorBumpVersion());
        Drain(disp);
        Assert.True(disp.EnqueueSnapshotTick());
        Drain(disp);

        Assert.Equal(2, snapSink.Packets.Count);
        ref readonly var packetHeader = ref MemoryMarshal.AsRef<PacketHeader>(
            snapSink.Packets[0].AsSpan(0, PacketHeaderSize));
        Assert.Equal((ushort)2, packetHeader.SequenceVersion);
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            snapSink.Packets[0].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var snapshotHeader));
        Assert.Equal(disp.SequenceVersion, snapshotHeader.Data.LastSequenceVersion);
        Assert.Equal((ushort)2, snapshotHeader.Data.LastSequenceVersion);
    }

    [Fact]
    public void SnapshotTick_ReflectsRestoredIncrementalVersion()
    {
        var sourceDispatcher = new ChannelDispatcher(channelNumber: 1,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = new CapturingSink(),
                Outbound = new ChannelDispatcherTests_RecordingOutbound(),
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1UL),
                TradeDate = 1,
            });
        var restoredState = sourceDispatcher.CaptureChannelState() with { SequenceVersion = 7 };

        var snapSink = new CapturingSink();
        MatchingEngine? restoredEngine = null;
        var restoredDispatcher = new ChannelDispatcher(channelNumber: 1,
            engineFactory: s =>
            {
                restoredEngine = new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance);
                return restoredEngine;
            },
            options: new ChannelDispatcherOptions
            {
                PacketSink = new CapturingSink(),
                Outbound = new ChannelDispatcherTests_RecordingOutbound(),
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1UL),
                TradeDate = 1,
            });
        var rotator = new SnapshotRotator(channelNumber: 1,
            source: new MatchingEngineSnapshotSource(restoredEngine!, new[] { Petr }),
            sink: snapSink, timeSource: new FakeNanosTimeSource(1UL));
        restoredDispatcher.AttachSnapshotRotator(rotator);
        restoredDispatcher.RestoreChannelState(restoredState);

        Assert.True(restoredDispatcher.EnqueueSnapshotTick());
        Drain(restoredDispatcher);

        Assert.Equal(2, snapSink.Packets.Count);
        ref readonly var packetHeader = ref MemoryMarshal.AsRef<PacketHeader>(
            snapSink.Packets[0].AsSpan(0, PacketHeaderSize));
        Assert.Equal((ushort)1, packetHeader.SequenceVersion);
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            snapSink.Packets[0].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var snapshotHeader));
        Assert.Equal((ushort)7, snapshotHeader.Data.LastSequenceVersion);
        Assert.Equal(restoredDispatcher.SequenceVersion, snapshotHeader.Data.LastSequenceVersion);
    }

    private static void Drain(ChannelDispatcher disp) => disp.CreateTestProbe().DrainInbound();
}

internal sealed class ChannelDispatcherTests_RecordingOutbound : B3.Exchange.Contracts.ICoreOutbound
{
    public bool WriteExecutionReportNew(B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle d = default) => true;
    public bool WriteExecutionReportTrade(B3.Exchange.Contracts.SessionId session, in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty, DurabilityHandle d = default) => true;
    public bool WriteExecutionReportPassiveTrade(SessionId ownerSession, ulong ownerClOrdId, long restingOrderId, in TradeEvent e, long leavesQty, long cumQty, DurabilityHandle d = default) => true;
    public OrderedStreamWriteResult WriteExecutionReportPassiveCancel(SessionId ownerSession, ulong ownerClOrdId, long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle d = default) => OrderedStreamWriteResult.CommittedAndEnqueued;
    public bool WriteExecutionReportModify(B3.Exchange.Contracts.SessionId session, long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle d = default, InvestorId? iv = null) => true;
    public bool WriteExecutionReportReject(B3.Exchange.Contracts.SessionId session, in RejectEvent e, ulong clOrdIdValue, DurabilityHandle d = default) => true;
}
