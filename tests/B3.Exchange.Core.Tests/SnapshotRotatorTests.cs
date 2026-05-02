using System.Runtime.InteropServices;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using B3.Umdf.Mbo.Sbe.V16;
using B3.Umdf.WireEncoder;
using Side = B3.Exchange.Matching.Side;

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
        public uint CurrentRptSeq { get; set; }
        public Dictionary<(long, Side), List<RestingOrderView>> Books { get; } = new();

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
        var rot = new SnapshotRotator(channelNumber: 84, source: src, sink: sink, nowNanos: () => 1234UL);

        int packets = rot.PublishNext();

        Assert.Equal(1, packets);
        Assert.Single(sink.Packets);
        Assert.Equal(1u, rot.SequenceNumber);

        var pkt = sink.Packets[0];
        ref readonly var hdr = ref MemoryMarshal.AsRef<PacketHeader>(pkt.AsSpan(0, PacketHeaderSize));
        Assert.Equal((byte)84, hdr.ChannelNumber);
        Assert.Equal((ushort)1, hdr.SequenceVersion);
        Assert.Equal(1u, hdr.SequenceNumber);
        Assert.Equal(1234UL, hdr.SendingTime);

        Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.SnapshotFullRefresh_Header_30Data.TryParse(
            pkt.AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var snapHdr));
        Assert.Equal(42L, (long)(ulong)snapHdr.Data.SecurityID);
        Assert.Equal(0u, snapHdr.Data.TotNumReports);
        Assert.Null(snapHdr.Data.LastRptSeq); // illiquid: §7.4
    }

    [Fact]
    public void NonEmptyBook_BuildsHeaderPlusOrdersFrame_WithLastRptSeq()
    {
        var src = new FakeSource
        {
            SecurityIds = new[] { 42L },
            CurrentRptSeq = 17,
        };
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

        int packets = rot.PublishNext();
        Assert.Equal(1, packets);
        Assert.Single(sink.Packets);

        var pkt = sink.Packets[0];
        Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.SnapshotFullRefresh_Header_30Data.TryParse(
            pkt.AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var snapHdr));
        Assert.Equal(3u, snapHdr.Data.TotNumReports);
        Assert.Equal(2u, snapHdr.Data.TotNumBids);
        Assert.Equal(1u, snapHdr.Data.TotNumOffers);
        Assert.Equal(17u, snapHdr.Data.LastRptSeq);

        // Same packet must carry an Orders_71 frame with NumInGroup == 3.
        int after = FrameOffset + WireOffsets.SnapHeaderBlockLength;
        int groupNumInGroupOff = after
            + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize
            + WireOffsets.SnapOrdersHeaderBlockLength + 2;
        Assert.Equal((byte)3, pkt[groupNumInGroupOff]);
    }

    [Fact]
    public void RoundRobinsThroughInstruments_AndAdvancesSnapSequence()
    {
        var src = new FakeSource { SecurityIds = new[] { 11L, 22L, 33L } };
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 5, source: src, sink: sink);

        for (int i = 0; i < 4; i++) rot.PublishNext(); // wraps after 3

        Assert.Equal(4, sink.Packets.Count);
        // Each tick → 1 packet (empty book), so SequenceNumber == 4.
        Assert.Equal(4u, rot.SequenceNumber);

        // SecurityIDs read out of each header packet must follow the rotation
        // order 11, 22, 33, 11.
        long[] expected = new[] { 11L, 22L, 33L, 11L };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.SnapshotFullRefresh_Header_30Data.TryParse(
                sink.Packets[i].AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var hdr));
            Assert.Equal(expected[i], (long)(ulong)hdr.Data.SecurityID);
            ref readonly var packetHdr = ref MemoryMarshal.AsRef<PacketHeader>(sink.Packets[i].AsSpan(0, PacketHeaderSize));
            Assert.Equal((uint)(i + 1), packetHdr.SequenceNumber);
        }
    }

    [Fact]
    public void LargeBook_ChunksIntoMultiplePacketsAndStepsSequence()
    {
        var src = new FakeSource { SecurityIds = new[] { 7L }, CurrentRptSeq = 99 };
        var bids = new List<RestingOrderView>();
        for (int i = 0; i < 400; i++) bids.Add(Order(i + 1, Side.Buy, 100_0000 - i, 100));
        var asks = new List<RestingOrderView>();
        for (int i = 0; i < 200; i++) asks.Add(Order(10_000 + i, Side.Sell, 101_0000 + i, 100));
        src.Books[(7L, Side.Buy)] = bids;
        src.Books[(7L, Side.Sell)] = asks;

        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 84, source: src, sink: sink);

        int packets = rot.PublishNext();
        Assert.True(packets >= 2, $"expected multi-packet snapshot, got {packets}");
        Assert.Equal((uint)packets, rot.SequenceNumber);

        // PacketHeader.SequenceNumber stepped 1..N within this snapshot.
        for (int i = 0; i < packets; i++)
        {
            ref readonly var pkthdr = ref MemoryMarshal.AsRef<PacketHeader>(sink.Packets[i].AsSpan(0, PacketHeaderSize));
            Assert.Equal((uint)(i + 1), pkthdr.SequenceNumber);
        }

        // Sum NumInGroup across every Orders_71 frame in every packet → 600.
        int totalEntries = 0;
        for (int i = 0; i < packets; i++)
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
        Assert.Equal(600, totalEntries);
    }

    [Fact]
    public void BumpSequenceVersion_BumpsVersionAndResetsSequenceNumber()
    {
        var src = new FakeSource { SecurityIds = new[] { 1L } };
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 1, source: src, sink: sink);

        rot.PublishNext();
        rot.PublishNext();
        Assert.Equal(2u, rot.SequenceNumber);
        Assert.Equal((ushort)1, rot.SequenceVersion);

        rot.BumpSequenceVersion();

        Assert.Equal((ushort)2, rot.SequenceVersion);
        Assert.Equal(0u, rot.SequenceNumber);

        rot.PublishNext();
        // Next packet: SequenceVersion=2, SequenceNumber=1
        ref readonly var hdr = ref MemoryMarshal.AsRef<PacketHeader>(sink.Packets[^1].AsSpan(0, PacketHeaderSize));
        Assert.Equal((ushort)2, hdr.SequenceVersion);
        Assert.Equal(1u, hdr.SequenceNumber);
    }

    [Fact]
    public void BookWithRptSeqButNoOrders_PublishesHeaderWithLastRptSeq()
    {
        // Liquid history but currently empty book (e.g. all orders matched).
        // Per scope: only the no-history case is illiquid; otherwise stamp
        // the live RptSeq.
        var src = new FakeSource { SecurityIds = new[] { 42L }, CurrentRptSeq = 5 };
        var sink = new CapturingSink();
        var rot = new SnapshotRotator(channelNumber: 1, source: src, sink: sink);

        rot.PublishNext();

        var pkt = sink.Packets[0];
        Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.SnapshotFullRefresh_Header_30Data.TryParse(
            pkt.AsSpan(FrameOffset, WireOffsets.SnapHeaderBlockLength), out var hdr));
        Assert.Equal(0u, hdr.Data.TotNumReports);
        Assert.Equal(5u, hdr.Data.LastRptSeq);
    }
}

/// <summary>
/// Verifies that <see cref="ChannelDispatcher.EnqueueSnapshotTick"/> drives
/// the attached <see cref="SnapshotRotator"/> on the dispatcher thread (no
/// concurrency with order processing).
/// </summary>
public class ChannelDispatcherSnapshotTests
{
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
            packetSink: incSink, outbound: new ChannelDispatcherTests_RecordingOutbound(),
            logger: NullLogger<ChannelDispatcher>.Instance, nowNanos: () => 1UL, tradeDate: 1);
        var rotator = new SnapshotRotator(channelNumber: 1,
            source: new MatchingEngineSnapshotSource(engine!, new[] { Petr }),
            sink: snapSink, nowNanos: () => 1UL);
        disp.AttachSnapshotRotator(rotator);

        // Synchronously drive: enqueue tick → drain.
        Assert.True(disp.EnqueueSnapshotTick());
        Drain(disp);

        Assert.Single(snapSink.Packets);          // snapshot went to snap-sink only
        Assert.Empty(incSink.Packets);            // incremental sink untouched
        Assert.Equal(1u, rotator.SequenceNumber); // snap-channel seq advanced
        Assert.Equal(0u, disp.SequenceNumber);    // inc-channel seq untouched
    }

    [Fact]
    public void SnapshotTick_ReadsLiveRestingOrders_FromMatchingEngine()
    {
        var incSink = new CapturingSink();
        var snapSink = new CapturingSink();
        MatchingEngine? engine = null;
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: s => { engine = new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance); return engine; },
            packetSink: incSink, outbound: new ChannelDispatcherTests_RecordingOutbound(),
            logger: NullLogger<ChannelDispatcher>.Instance, nowNanos: () => 1UL, tradeDate: 1);
        var rotator = new SnapshotRotator(channelNumber: 1,
            source: new MatchingEngineSnapshotSource(engine!, new[] { Petr }),
            sink: snapSink, nowNanos: () => 1UL);
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

        Assert.Single(snapSink.Packets);
        var pkt = snapSink.Packets[0];
        int frameOff = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
        Assert.True(B3.Umdf.Mbo.Sbe.V16.V6.SnapshotFullRefresh_Header_30Data.TryParse(
            pkt.AsSpan(frameOff, WireOffsets.SnapHeaderBlockLength), out var hdr));
        Assert.Equal(3u, hdr.Data.TotNumReports);
        Assert.Equal(2u, hdr.Data.TotNumBids);
        Assert.Equal(1u, hdr.Data.TotNumOffers);
        // After 3 OrderAccepted events the engine's RptSeq is 3.
        Assert.Equal(3u, hdr.Data.LastRptSeq);
    }

    private static void Drain(ChannelDispatcher disp)
    {
        var field = typeof(ChannelDispatcher).GetField("_inbound", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var inbound = field.GetValue(disp)!;
        var readerProp = inbound.GetType().GetProperty("Reader")!;
        var reader = readerProp.GetValue(inbound)!;
        var tryRead = reader.GetType().GetMethod("TryRead")!;
        var args = new object?[] { null };
        var processOne = typeof(ChannelDispatcher).GetMethod("ProcessOne", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        while ((bool)tryRead.Invoke(reader, args)!)
        {
            processOne.Invoke(disp, new[] { args[0] });
        }
    }
}

internal sealed class ChannelDispatcherTests_RecordingOutbound : B3.Exchange.Core.ICoreOutbound
{
    public bool WriteExecutionReportNew(B3.Exchange.Contracts.SessionId session, ulong clOrdIdValue, in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue) => true;
    public bool WriteExecutionReportTrade(B3.Exchange.Contracts.SessionId session, in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty) => true;
    public bool WriteExecutionReportCancel(B3.Exchange.Contracts.SessionId session, in OrderCanceledEvent e, ulong clOrdIdValue, ulong origClOrdIdValue, ulong receivedTimeNanos = ulong.MaxValue) => true;
    public bool WriteExecutionReportModify(B3.Exchange.Contracts.SessionId session, long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq, ulong receivedTimeNanos = ulong.MaxValue) => true;
    public bool WriteExecutionReportReject(B3.Exchange.Contracts.SessionId session, in RejectEvent e, ulong clOrdIdValue) => true;
}
