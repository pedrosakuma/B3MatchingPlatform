using System.Runtime.InteropServices;
using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Umdf.WireEncoder;
using Microsoft.Extensions.Logging.Abstractions;
using Side = B3.Exchange.Matching.Side;
using OrderType = B3.Exchange.Matching.OrderType;
using TimeInForce = B3.Exchange.Matching.TimeInForce;

namespace B3.Exchange.Core.Tests;

/// <summary>
/// Issue #216 (Onda L · L3a) integration tests: verify that the
/// <see cref="ChannelDispatcher"/> appends each published incremental
/// packet into its <see cref="UmdfPacketRetransmitBuffer"/>, that
/// retrieved bytes byte-equal what the packet sink received, and that a
/// <c>SequenceVersion</c> bump wipes the ring before continuing.
/// </summary>
public class ChannelDispatcherRetxBufferTests
{
    private const long Petr = 900_000_000_001L;
    private static Instrument Petr4 => new()
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
    private static long Px(decimal p) => (long)(p * 10_000m);

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Packets.Add(packet.ToArray());
    }

    private sealed class FakeSession
    {
        private static int _nextId;
        public SessionId Id { get; } = new("rb" + System.Threading.Interlocked.Increment(ref _nextId));
        public uint EnteringFirm => 7;
    }

    private sealed class NoopOutbound : ICoreOutbound
    {
        public bool WriteExecutionReportNew(SessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportTrade(SessionId session, in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId ownerSession, ulong ownerClOrdId, long restingOrderId, in TradeEvent e, long leavesQty, long cumQty) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId ownerSession, ulong ownerClOrdId, long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportModify(SessionId session, long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq, ulong receivedTimeNanos = ulong.MaxValue) => true;
        public bool WriteExecutionReportReject(SessionId session, in B3.Exchange.Matching.RejectEvent e, ulong clOrdIdValue) => true;
    }

    private static (ChannelDispatcher disp, RecordingPacketSink pkt, UmdfPacketRetransmitBuffer ring) NewDispatcherWithRing(int capacity = 16)
    {
        var pkt = new RecordingPacketSink();
        var ring = new UmdfPacketRetransmitBuffer(capacity);
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance),
            packetSink: pkt,
            outbound: new NoopOutbound(),
            logger: NullLogger<ChannelDispatcher>.Instance,
            nowNanos: () => 1_000_000_000UL,
            tradeDate: 19_000,
            retxBuffer: ring);
        return (disp, pkt, ring);
    }

    private static void DrainInbound(ChannelDispatcher disp) => disp.CreateTestProbe().DrainInbound();

    [Fact]
    public void Constructor_NoBuffer_RetransmitBufferIsNull()
    {
        var pkt = new RecordingPacketSink();
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance),
            packetSink: pkt,
            outbound: new NoopOutbound(),
            logger: NullLogger<ChannelDispatcher>.Instance);
        Assert.Null(disp.RetransmitBuffer);
    }

    [Fact]
    public void FlushPacket_AppendsExactBytesToRingKeyedBySequenceNumber()
    {
        var (disp, pkt, ring) = NewDispatcherWithRing();
        var s = new FakeSession();

        disp.EnqueueNewOrder(
            new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, s.EnteringFirm, 1_000UL),
            s.Id, s.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Same(ring, disp.RetransmitBuffer);
        var sentPacket = Assert.Single(pkt.Packets);
        Assert.Equal(1, ring.Count);
        Assert.Equal(1u, ring.FirstAvailableSeqOrZero);
        Assert.Equal(1u, ring.LastSeqOrZero);
        Assert.True(ring.TryGet(1u, out var stored));
        Assert.Equal(sentPacket, stored);
    }

    [Fact]
    public void FlushPacket_MultiplePackets_RetainedInOrderUntilEviction()
    {
        var (disp, pkt, ring) = NewDispatcherWithRing(capacity: 3);
        var s = new FakeSession();

        for (int i = 0; i < 5; i++)
        {
            disp.EnqueueNewOrder(
                new NewOrderCommand("o" + i, Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m + i * 0.01m), 100, s.EnteringFirm, (ulong)(1_000 + i)),
                s.Id, s.EnteringFirm, clOrdIdValue: (ulong)(1 + i));
            DrainInbound(disp);
        }

        Assert.Equal(5, pkt.Packets.Count);
        Assert.Equal(3, ring.Count);
        Assert.Equal(2L, ring.Evictions);
        Assert.Equal(3u, ring.FirstAvailableSeqOrZero);
        Assert.Equal(5u, ring.LastSeqOrZero);

        for (uint seq = 3; seq <= 5; seq++)
        {
            Assert.True(ring.TryGet(seq, out var stored));
            Assert.Equal(pkt.Packets[(int)(seq - 1)], stored);
        }
        Assert.False(ring.TryGet(1u, out _));
        Assert.False(ring.TryGet(2u, out _));
    }

    [Fact]
    public void FlushPacket_SequenceVersionRollover_ResetsRingBeforeAppendingNewVersionPacket()
    {
        var (disp, pkt, ring) = NewDispatcherWithRing();
        var s = new FakeSession();

        // Pre-populate the ring on version 1 with one real packet.
        disp.EnqueueNewOrder(
            new NewOrderCommand("pre", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, s.EnteringFirm, 1_000UL),
            s.Id, s.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        Assert.Equal(1, ring.Count);
        long baselineEvictions = ring.Evictions;

        // Force the next FlushPacket to roll the SequenceVersion.
        disp.TestSetSequenceNumber(uint.MaxValue);

        disp.EnqueueNewOrder(
            new NewOrderCommand("post", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(11m), 100, s.EnteringFirm, 2_000UL),
            s.Id, s.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);

        // After rollover: ring was wiped, then the new packet was
        // appended under the new (version, seq) tuple.
        Assert.Equal((ushort)2, disp.SequenceVersion);
        Assert.Equal(1u, disp.SequenceNumber);
        Assert.Equal(1, ring.Count);
        Assert.Equal(1u, ring.FirstAvailableSeqOrZero);
        Assert.Equal(1u, ring.LastSeqOrZero);
        // Pre-rollover packet is no longer addressable.
        Assert.True(ring.TryGet(1u, out var stored));
        var lastSent = pkt.Packets[^1];
        Assert.Equal(lastSent, stored);
        // SequenceVersion in the wire packet header now reads 2.
        Assert.Equal((ushort)2, MemoryMarshal.Read<ushort>(stored.AsSpan(2, 2)));
        // Reset preserves the lifetime evictions counter.
        Assert.Equal(baselineEvictions, ring.Evictions);
    }
}
