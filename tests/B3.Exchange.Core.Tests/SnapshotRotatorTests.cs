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
        public Dictionary<(long, Side), List<RestingOrderView>> Books { get; } = new();

        /// <summary>Default OPEN=17; tests override per issue #583 scenarios.</summary>
        public byte SecurityTradingStatus { get; set; } = 17;

        public uint GetCurrentRptSeq(long securityId)
            => RptSeqBySecurity.TryGetValue(securityId, out uint rptSeq) ? rptSeq : 0;

        public byte GetSecurityTradingStatus(long securityId) => SecurityTradingStatus;

        public IEnumerable<RestingOrderView> EnumerateBook(long securityId, Side side)
            => Books.TryGetValue((securityId, side), out var l) ? l : Enumerable.Empty<RestingOrderView>();
    }

    private sealed class CapturingSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Packets.Add(packet.ToArray());
    }

    private static RestingOrderView Order(long oid, Side side, long px, long qty, ulong nanos = 1_000UL, uint firm = 7)
        => new(oid, side, px, qty, firm, nanos);

    [Fact]
    public void EmptyBook_NoPriorIncrementals_EmitsIlliquidHeaderOnly()
    {
        var src = new FakeSource { SecurityIds = new[] { 42L } };
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 84, source: src, sink: sink, timeSource: new FakeNanosTimeSource(1234UL));

        int packets = rot.PublishNext(incrementalSequenceVersion: 3);

        Assert.Equal(2, packets); // header (illiquid) + trailing SecurityStatus_3 (issue #583)
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

        // Issue #583: trailing packet reports the current SecurityTradingStatus.
        int statusOffset = FrameOffset + WireOffsets.SecurityStatusBodySecurityTradingStatusOffset;
        Assert.Equal(src.SecurityTradingStatus, sink.Packets[1][statusOffset]);
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
        Assert.Equal(2, packets); // header+orders packet + trailing status packet
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
    }

    [Fact]
    public void HaltedInstrument_TrailingStatusPacket_ReportsForbidden()
    {
        // Issue #583: a late subscriber joining while an instrument is halted
        // must learn the FORBIDDEN status from the snapshot's trailing
        // SecurityStatus_3 packet, not just from a future incremental event.
        const byte securityTradingStatusForbidden = 18;
        var src = new FakeSource
        {
            SecurityIds = new[] { 42L },
            SecurityTradingStatus = securityTradingStatusForbidden,
        };
        src.RptSeqBySecurity[42L] = 9;
        src.Books[(42L, Side.Buy)] = new() { Order(1, Side.Buy, 100_0000, 500) };

        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 84, source: src, sink: sink);

        int packets = rot.PublishNext(incrementalSequenceVersion: 1);

        Assert.Equal(2, packets);
        Assert.Equal(2, sink.Packets.Count);

        int statusOffset = FrameOffset + WireOffsets.SecurityStatusBodySecurityTradingStatusOffset;
        Assert.Equal(securityTradingStatusForbidden, sink.Packets[1][statusOffset]);
    }

    [Fact]
    public void RoundRobinsThroughInstruments_AndAdvancesSnapSequence()
    {
        var src = new FakeSource { SecurityIds = new[] { 11L, 22L, 33L } };
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 5, source: src, sink: sink);

        for (int i = 0; i < 4; i++) rot.PublishNext(incrementalSequenceVersion: 1); // wraps after 3

        // Each tick → 2 packets (empty-book header + trailing status), issue #583.
        Assert.Equal(8, sink.Packets.Count);
        Assert.Equal(8u, rot.SequenceNumber);

        // SecurityIDs read out of each header packet must follow the rotation
        // order 11, 22, 33, 11. Header packets are the first of each 2-packet
        // publish, at indices 0, 2, 4, 6.
        long[] expected = new[] { 11L, 22L, 33L, 11L };
        for (int i = 0; i < expected.Length; i++)
        {
            int headerIdx = i * 2;
            Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
                sink.Packets[headerIdx].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var hdr));
            Assert.Equal(expected[i], (long)(ulong)hdr.Data.SecurityID);
            ref readonly var packetHdr = ref MemoryMarshal.AsRef<PacketHeader>(sink.Packets[headerIdx].AsSpan(0, PacketHeaderSize));
            Assert.Equal((uint)(headerIdx + 1), packetHdr.SequenceNumber);
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

        // Each PublishFor emits 2 packets (empty-book header + trailing
        // status, issue #583); header packets sit at indices 0, 2, 4.
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

        int packets = rot.PublishNext(incrementalSequenceVersion: 17);
        Assert.True(packets >= 2, $"expected multi-packet snapshot, got {packets}");
        Assert.Equal((uint)packets, rot.SequenceNumber);

        for (int i = 0; i < packets; i++)
        {
            ref readonly var pkthdr = ref MemoryMarshal.AsRef<PacketHeader>(sink.Packets[i].AsSpan(0, PacketHeaderSize));
            Assert.Equal((uint)(i + 1), pkthdr.SequenceNumber);
            Assert.Equal((ushort)1, pkthdr.SequenceVersion);
            Assert.Equal(123_456UL, pkthdr.SendingTime);
            Assert.InRange(sink.Packets[i].Length, 1, SnapshotPacketBuilder.DefaultPacketBufferSize);
        }

        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            sink.Packets[0].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var header));
        Assert.Equal(25_000u, header.Data.TotNumReports);
        Assert.Equal(20_000u, header.Data.TotNumBids);
        Assert.Equal(5_000u, header.Data.TotNumOffers);
        Assert.Equal(99u, header.Data.LastRptSeq);
        Assert.Equal((ushort)17, header.Data.LastSequenceVersion);

        int totalEntries = 0;
        for (int i = 0; i < packets - 1; i++)
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
        int requiredPackets = SnapshotPacketBuilder.GetPacketCount(
            SnapshotPacketBuilder.DefaultPacketBufferSize, 700, 300);
        Assert.True(requiredPackets > 1);
        rot.CreateTestProbe().SetSequence(
            version: 7,
            number: uint.MaxValue - (uint)requiredPackets);

        int firstPublishPackets = rot.PublishNext(incrementalSequenceVersion: 55);

        Assert.Equal(requiredPackets, firstPublishPackets);
        Assert.Equal((ushort)7, rot.SequenceVersion);
        Assert.Equal(uint.MaxValue, rot.SequenceNumber);
        for (int i = 0; i < requiredPackets; i++)
        {
            ref readonly var header = ref MemoryMarshal.AsRef<PacketHeader>(
                sink.Packets[i].AsSpan(0, PacketHeaderSize));
            Assert.Equal((ushort)7, header.SequenceVersion);
            Assert.Equal(uint.MaxValue - (uint)requiredPackets + (uint)i + 1, header.SequenceNumber);
            Assert.NotEqual(0u, header.SequenceNumber);
        }

        int secondPublishPackets = rot.PublishNext(incrementalSequenceVersion: 55);

        Assert.Equal(requiredPackets, secondPublishPackets);
        Assert.Equal((ushort)8, rot.SequenceVersion);
        Assert.Equal((uint)requiredPackets, rot.SequenceNumber);
        for (int i = 0; i < requiredPackets; i++)
        {
            ref readonly var header = ref MemoryMarshal.AsRef<PacketHeader>(
                sink.Packets[requiredPackets + i].AsSpan(0, PacketHeaderSize));
            Assert.Equal((ushort)8, header.SequenceVersion);
            Assert.Equal((uint)(i + 1), header.SequenceNumber);
            Assert.NotEqual(0u, header.SequenceNumber);
        }
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
        // Each publish → 2 packets (empty-book header + trailing status).
        Assert.Equal(4u, rot.SequenceNumber);
        Assert.Equal((ushort)1, rot.SequenceVersion);

        rot.BumpSequenceVersion();

        Assert.Equal((ushort)2, rot.SequenceVersion);
        Assert.Equal(0u, rot.SequenceNumber);

        rot.PublishNext(incrementalSequenceVersion: 9);
        // Next publish: SequenceVersion=2, header packet SequenceNumber=1,
        // trailing status packet SequenceNumber=2.
        var headerPkt = sink.Packets[^2];
        ref readonly var hdr = ref MemoryMarshal.AsRef<PacketHeader>(headerPkt.AsSpan(0, PacketHeaderSize));
        Assert.Equal((ushort)2, hdr.SequenceVersion);
        Assert.Equal(1u, hdr.SequenceNumber);
        Assert.True(SnapshotFullRefresh_Header_30Data.TryParse(
            headerPkt.AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var snapHdr));
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

        Assert.Equal(2, snapSink.Packets.Count);  // header + trailing status (issue #583)
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

        Assert.Equal(2, snapSink.Packets.Count); // header(+orders) + trailing status (issue #583)
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

        Assert.Equal(2, snapSink.Packets.Count); // header(+orders) + trailing status (issue #583)
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

        Assert.Equal(2, snapSink.Packets.Count); // header(+orders) + trailing status (issue #583)
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
