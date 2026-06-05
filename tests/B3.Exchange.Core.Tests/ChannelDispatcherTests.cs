using B3.Exchange.Contracts;
using Side = B3.Exchange.Matching.Side;
using RejectEvent = B3.Exchange.Matching.RejectEvent;
using OrderType = B3.Exchange.Matching.OrderType;
using TimeInForce = B3.Exchange.Matching.TimeInForce;
using System.Runtime.InteropServices;
using B3.Exchange.Gateway;
using B3.Exchange.Instruments;
using B3.Exchange.Core;
using B3.Exchange.TestSupport;
using B3.Exchange.Matching;
using B3.Umdf.WireEncoder;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Core.Tests;

public partial class ChannelDispatcherTests
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
        public B3.Exchange.Contracts.SessionId Id { get; } = new("s" + System.Threading.Interlocked.Increment(ref _nextId));
        public uint EnteringFirm { get; init; } = 7;
        public List<string> Calls { get; } = new();
        public List<OrderAcceptedEvent> News { get; } = new();
        public List<OrderCanceledEvent> Cancels { get; } = new();
        public List<RejectEvent> Rejects { get; } = new();
        public List<ulong> RejectClOrdIds { get; } = new();
        public List<TradeEvent> Trades { get; } = new();
        public List<(long LeavesQty, long CumQty)> TradeQty { get; } = new();
        public List<OrderRestatedEvent> Restates { get; } = new();
        public bool CaptureCancelIds { get; set; }
        public List<(ulong ClOrdId, ulong OrigClOrdId)> CancelIds { get; } = new();
        public ulong LastReceivedTime { get; set; } = ulong.MaxValue;

        public FakeSession(RecordingOutbound outbound) { outbound.Register(this); }
    }

    private sealed class RecordingOutbound : ICoreOutbound
    {
        private readonly Dictionary<B3.Exchange.Contracts.SessionId, FakeSession> _sessions = new();
        private readonly Dictionary<long, (B3.Exchange.Contracts.SessionId Session, ulong ClOrdId)> _owners = new();
        public void Register(FakeSession s) => _sessions[s.Id] = s;
        private FakeSession? Find(B3.Exchange.Contracts.SessionId id)
            => _sessions.TryGetValue(id, out var s) ? s : null;

        public bool WriteExecutionReportNew(B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle d = default)
        { _owners[e.OrderId] = (session, clOrdIdValue); if (Find(session) is { } s) { s.News.Add(e); s.Calls.Add("New"); s.LastReceivedTime = receivedTimeNanos; } return true; }
        public bool WriteExecutionReportTrade(B3.Exchange.Contracts.SessionId session, in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty, DurabilityHandle d = default)
        { if (Find(session) is { } s) { s.Trades.Add(e); s.TradeQty.Add((leavesQty, cumQty)); s.Calls.Add(isAggressor ? "TradeAgg" : "TradePass"); } return true; }
        public bool WriteExecutionReportPassiveTrade(B3.Exchange.Contracts.SessionId ownerSession, ulong ownerClOrdId, long restingOrderId, in TradeEvent e, long leavesQty, long cumQty, DurabilityHandle d = default)
        { if (Find(ownerSession) is { } s) { s.Trades.Add(e); s.TradeQty.Add((leavesQty, cumQty)); s.Calls.Add("TradePass"); } return true; }
        public bool WriteExecutionReportPassiveCancel(B3.Exchange.Contracts.SessionId ownerSession, ulong ownerClOrdId, long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle d = default)
        {
            if (Find(ownerSession) is { } s)
            {
                s.Cancels.Add(e); s.Calls.Add("Cancel"); s.LastReceivedTime = receivedTimeNanos;
                if (s.CaptureCancelIds) s.CancelIds.Add((requesterClOrdIdOrZero != 0 ? requesterClOrdIdOrZero : ownerClOrdId, ownerClOrdId));
            }
            return true;
        }
        public bool WriteExecutionReportModify(B3.Exchange.Contracts.SessionId session, long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle d = default, InvestorId? iv = null)
        { if (Find(session) is { } s) { s.Calls.Add("Modify"); s.LastReceivedTime = receivedTimeNanos; } return true; }
        public bool WriteExecutionReportReject(B3.Exchange.Contracts.SessionId session, in RejectEvent e, ulong clOrdIdValue, DurabilityHandle d = default)
        { if (Find(session) is { } s) { s.Rejects.Add(e); s.RejectClOrdIds.Add(clOrdIdValue); s.Calls.Add("Reject"); } return true; }
        public bool WriteExecutionReportRestate(B3.Exchange.Contracts.SessionId ownerSession, ulong ownerClOrdId, in OrderRestatedEvent e, DurabilityHandle d = default)
        { if (Find(ownerSession) is { } s) { s.Restates.Add(e); s.Calls.Add("Restate"); } return true; }
    }

    private static (ChannelDispatcher disp, RecordingPacketSink pkt, RecordingOutbound outbound) NewDispatcher(
        int maxOpenOrdersPerFirm = 100_000,
        MetricsRegistry? metrics = null)
    {
        var pkt = new RecordingPacketSink();
        var outbound = new RecordingOutbound();
        var channelMetrics = metrics?.RegisterChannel(1);
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = pkt,
                Outbound = outbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1_000_000_000UL),
                TradeDate = 19_000,
                Metrics = channelMetrics,
                OpenOrders = metrics?.OpenOrders,
                MaxOpenOrdersPerFirm = maxOpenOrdersPerFirm,
            });
        return (disp, pkt, outbound);
    }

    [Fact]
    public void NewOrder_AcceptedRestingOrder_EmitsOrderAddedFrameAndExecReportNew()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);

        // No background loop — drive synchronously by reading the inbound queue.
        DrainInbound(disp);

        Assert.Single(pkt.Packets);
        Assert.Single(reply.News);
        Assert.Equal(1, (int)disp.SequenceNumber);

        var packet = pkt.Packets[0];
        // Packet: 16-byte PacketHeader + framing(4)+sbeHdr(8)+block(56) = 84 bytes
        Assert.Equal(WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize + WireOffsets.OrderBlockLength, packet.Length);
        // ChannelNumber@0
        Assert.Equal((byte)1, packet[0]);
        // SequenceNumber@4
        Assert.Equal((uint)1, MemoryMarshal.Read<uint>(packet.AsSpan(4, 4)));
        // SecurityId in body (after PacketHeader+Framing+SbeHeader)
        int bodyStart = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
        Assert.Equal(Petr, MemoryMarshal.Read<long>(packet.AsSpan(bodyStart + WireOffsets.OrderBodySecurityIdOffset, 8)));
    }

    [Fact]
    public void NewOrder_UnsupportedCharacteristic_EmitsExecutionReportReject_NoUmdfPacket()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        disp.EnqueueNewOrder(new NewOrderCommand("415", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL)
        {
            UnsupportedOrderCharacteristic = true,
        }, reply.Id, reply.EnteringFirm, clOrdIdValue: 415UL);

        DrainInbound(disp);

        Assert.Empty(pkt.Packets);
        Assert.Empty(reply.News);
        var reject = Assert.Single(reply.Rejects);
        Assert.Equal(RejectReason.UnsupportedOrderCharacteristic, reject.Reason);
        Assert.Equal("415", reject.ClOrdId);
        Assert.Equal(415UL, Assert.Single(reply.RejectClOrdIds));
    }

    [Fact]
    public void NewOrder_MaxOpenOrdersPerFirm_RejectsNPlusOneWithOrderExceedsLimit()
    {
        var (disp, _, outbound) = NewDispatcher(maxOpenOrdersPerFirm: 2);
        var reply = new FakeSession(outbound) { EnteringFirm = 7 };

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9m), 100, 7, 2_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL);
        disp.EnqueueNewOrder(new NewOrderCommand("3", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(8m), 100, 7, 3_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 3UL);

        DrainInbound(disp);

        Assert.Equal(2, reply.News.Count);
        var reject = Assert.Single(reply.Rejects);
        Assert.Equal(RejectReason.OrderExceedsLimit, reject.Reason);
        Assert.Equal(3UL, Assert.Single(reply.RejectClOrdIds));
    }

    [Fact]
    public void MaxOpenOrdersPerFirm_CancelDecrementsSoNextNewIsAccepted()
    {
        var (disp, _, outbound) = NewDispatcher(maxOpenOrdersPerFirm: 1);
        var reply = new FakeSession(outbound) { EnteringFirm = 7 };

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        long orderId = Assert.Single(reply.News).OrderId;
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9m), 100, 7, 2_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);
        Assert.Single(reply.Rejects);

        disp.EnqueueCancel(new CancelOrderCommand("3", Petr, orderId, 3_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 3UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("4", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(8m), 100, 7, 4_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 4UL);
        DrainInbound(disp);

        Assert.Equal(2, reply.News.Count);
        Assert.Single(reply.Rejects);
    }

    [Fact]
    public void MaxOpenOrdersPerFirm_FillDecrementsSoNextNewIsAccepted()
    {
        var (disp, _, outbound) = NewDispatcher(maxOpenOrdersPerFirm: 1);
        var maker = new FakeSession(outbound) { EnteringFirm = 7 };
        var taker = new FakeSession(outbound) { EnteringFirm = 8 };

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.IOC, Px(10m), 100, 8, 2_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("3", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(11m), 100, 7, 3_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 3UL);
        DrainInbound(disp);

        Assert.Equal(2, maker.News.Count);
        Assert.Empty(maker.Rejects);
    }

    [Fact]
    public void MaxOpenOrdersPerFirm_ReplaceIsNeutral()
    {
        var (disp, _, outbound) = NewDispatcher(maxOpenOrdersPerFirm: 1);
        var reply = new FakeSession(outbound) { EnteringFirm = 7 };

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        long orderId = Assert.Single(reply.News).OrderId;
        disp.EnqueueReplace(new ReplaceOrderCommand("2", Petr, orderId, Px(10m), 100, 2_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("3", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9m), 100, 7, 3_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 3UL);
        DrainInbound(disp);

        Assert.Equal(RejectReason.OrderExceedsLimit, Assert.Single(reply.Rejects).Reason);
    }

    [Fact]
    public void MaxOpenOrdersPerFirm_IsIsolatedByFirm()
    {
        var (disp, _, outbound) = NewDispatcher(maxOpenOrdersPerFirm: 1);
        var firmA = new FakeSession(outbound) { EnteringFirm = 7 };
        var firmB = new FakeSession(outbound) { EnteringFirm = 8 };

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            firmA.Id, firmA.EnteringFirm, clOrdIdValue: 1UL);
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9m), 100, 7, 2_000UL),
            firmA.Id, firmA.EnteringFirm, clOrdIdValue: 2UL);
        disp.EnqueueNewOrder(new NewOrderCommand("3", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(8m), 100, 8, 3_000UL),
            firmB.Id, firmB.EnteringFirm, clOrdIdValue: 3UL);

        DrainInbound(disp);

        Assert.Single(firmA.News);
        Assert.Single(firmA.Rejects);
        Assert.Single(firmB.News);
        Assert.Empty(firmB.Rejects);
    }

    [Fact]
    public void MaxOpenOrdersPerFirm_GaugeReflectsCurrentCount()
    {
        var metrics = new MetricsRegistry();
        var (disp, _, outbound) = NewDispatcher(maxOpenOrdersPerFirm: 2, metrics: metrics);
        var reply = new FakeSession(outbound) { EnteringFirm = 7 };

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        Assert.Contains("open_orders_per_firm{firm=\"7\"} 1\n", metrics.RenderProm());
        long orderId = Assert.Single(reply.News).OrderId;

        disp.EnqueueCancel(new CancelOrderCommand("2", Petr, orderId, 2_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Contains("open_orders_per_firm{firm=\"7\"} 0\n", metrics.RenderProm());
    }

    [Fact]
    public void MaxOpenOrdersPerFirm_StopAcceptedCountsTowardCap()
    {
        var (disp, _, outbound) = NewDispatcher(maxOpenOrdersPerFirm: 1);
        var reply = new FakeSession(outbound) { EnteringFirm = 7 };

        disp.EnqueueNewOrder(new NewOrderCommand("S1", Petr, Side.Buy, OrderType.StopLoss, TimeInForce.Day, 0, 100, 7, 1_000UL)
        { StopPxMantissa = Px(11m) }, reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("L2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 2_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);

        Assert.Single(reply.News);
        Assert.Equal(RejectReason.OrderExceedsLimit, Assert.Single(reply.Rejects).Reason);
    }

    [Fact]
    public void MaxOpenOrdersPerFirm_StopCancelDecrementsSoNextNewIsAccepted()
    {
        var (disp, _, outbound) = NewDispatcher(maxOpenOrdersPerFirm: 1);
        var reply = new FakeSession(outbound) { EnteringFirm = 7 };

        disp.EnqueueNewOrder(new NewOrderCommand("S1", Petr, Side.Buy, OrderType.StopLoss, TimeInForce.Day, 0, 100, 7, 1_000UL)
        { StopPxMantissa = Px(11m) }, reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        long stopOrderId = Assert.Single(reply.News).OrderId;

        disp.EnqueueCancel(new CancelOrderCommand("C1", Petr, stopOrderId, 2_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("L2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 3_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 3UL);
        DrainInbound(disp);

        Assert.Equal(2, reply.News.Count);
        Assert.Single(reply.Cancels);
        Assert.Empty(reply.Rejects);
    }

    [Fact]
    public void MaxOpenOrdersPerFirm_TriggeredStopThatRestsKeepsSingleCount()
    {
        var metrics = new MetricsRegistry();
        var (disp, _, outbound) = NewDispatcher(maxOpenOrdersPerFirm: 1, metrics: metrics);
        var stopOwner = new FakeSession(outbound) { EnteringFirm = 7 };
        var maker = new FakeSession(outbound) { EnteringFirm = 8 };
        var trigger = new FakeSession(outbound) { EnteringFirm = 9 };

        disp.EnqueueNewOrder(new NewOrderCommand("S1", Petr, Side.Buy, OrderType.StopLimit, TimeInForce.Day, Px(10m), 200, 7, 1_000UL)
        { StopPxMantissa = Px(10m) }, stopOwner.Id, stopOwner.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("M1", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 2_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("T1", Petr, Side.Buy, OrderType.Limit, TimeInForce.IOC, Px(10m), 100, 9, 3_000UL),
            trigger.Id, trigger.EnteringFirm, clOrdIdValue: 3UL);
        DrainInbound(disp);

        Assert.Contains("open_orders_per_firm{firm=\"7\"} 1\n", metrics.RenderProm());

        disp.EnqueueNewOrder(new NewOrderCommand("L2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9m), 100, 7, 4_000UL),
            stopOwner.Id, stopOwner.EnteringFirm, clOrdIdValue: 4UL);
        DrainInbound(disp);

        Assert.Equal(RejectReason.OrderExceedsLimit, Assert.Single(stopOwner.Rejects).Reason);
        Assert.Contains("open_orders_per_firm{firm=\"7\"} 1\n", metrics.RenderProm());
    }

    [Fact]
    public void MaxOpenOrdersPerFirm_RestoreMatchesSteadyStateForStops()
    {
        var (live, _, liveOutbound) = NewDispatcher(maxOpenOrdersPerFirm: 1);
        var liveReply = new FakeSession(liveOutbound) { EnteringFirm = 7 };

        live.EnqueueNewOrder(new NewOrderCommand("S1", Petr, Side.Buy, OrderType.StopLoss, TimeInForce.Day, 0, 100, 7, 1_000UL)
        { StopPxMantissa = Px(11m) }, liveReply.Id, liveReply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(live);
        var snapshot = live.CaptureChannelState();
        live.EnqueueNewOrder(new NewOrderCommand("L2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 2_000UL),
            liveReply.Id, liveReply.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(live);

        var (restored, _, restoredOutbound) = NewDispatcher(maxOpenOrdersPerFirm: 1);
        restored.RestoreChannelState(snapshot);
        var restoredReply = new FakeSession(restoredOutbound) { EnteringFirm = 7 };
        restored.EnqueueNewOrder(new NewOrderCommand("L2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 2_000UL),
            restoredReply.Id, restoredReply.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(restored);

        Assert.Equal(RejectReason.OrderExceedsLimit, Assert.Single(liveReply.Rejects).Reason);
        Assert.Equal(RejectReason.OrderExceedsLimit, Assert.Single(restoredReply.Rejects).Reason);
    }


    [Fact]
    public void NewOrder_WithMemo_ExecReportNewCarriesMemo()
    {
        var (disp, _, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);
        byte[] memo = System.Text.Encoding.ASCII.GetBytes("ABC123");

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL)
        {
            Memo = memo,
        }, reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Single(reply.News);
        Assert.True(reply.News[0].Memo!.SequenceEqual(memo));
    }

    [Fact]
    public void RejectedOrder_WithMemo_ExecReportRejectCarriesMemo()
    {
        var (disp, _, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);
        byte[] memo = System.Text.Encoding.ASCII.GetBytes("ABC123");

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 0, 7, 1_000UL)
        {
            Memo = memo,
        }, reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Single(reply.Rejects);
        Assert.True(reply.Rejects[0].Memo!.SequenceEqual(memo));
    }

    [Fact]
    public void Crossing_AggressorAndResting_BothGetTradeReportsAndDeleteFrames()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var maker = new FakeSession(outbound);
        var taker = new FakeSession(outbound);

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        Assert.Single(maker.News);
        pkt.Packets.Clear();

        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 2_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);

        Assert.Single(maker.Trades);
        Assert.Single(taker.Trades);
        Assert.Contains("TradePass", maker.Calls);
        Assert.Contains("TradeAgg", taker.Calls);

        Assert.Single(pkt.Packets); // single batched packet (Trade + DeleteOrder)
        var packet = pkt.Packets[0];
        // Sequence number 2 (first packet was for the maker accept)
        Assert.Equal((uint)2, MemoryMarshal.Read<uint>(packet.AsSpan(4, 4)));
    }

    [Fact]
    public void Cancel_RestingOrder_EmitsDeleteFrameAndExecReportCancel()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        long oid = reply.News[0].OrderId;
        pkt.Packets.Clear();

        disp.EnqueueCancel(new CancelOrderCommand("1", Petr, oid, 2_000UL), reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL, origClOrdIdValue: 0UL);
        DrainInbound(disp);

        Assert.Single(reply.Cancels);
        Assert.Equal(oid, reply.Cancels[0].OrderId);
        Assert.Single(pkt.Packets);
    }

    [Fact]
    public void ExecReports_PropagateInboundReceivedTime_FromCommandEnteredAt()
    {
        // #49 / #GAP-11: ER frames emitted while processing an inbound command
        // must carry that command's ingress timestamp so clients can measure
        // gateway latency. After processing returns, the dispatcher resets
        // the per-command timestamp so engine-originated events emitted
        // outside command context (e.g. iceberg restate) get the SBE null
        // sentinel (ulong.MaxValue) and not a stale value.
        var (disp, _, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        const ulong newReceivedAt = 1_700_000_000_111_111_111UL;
        disp.EnqueueNewOrder(
            new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, newReceivedAt),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        Assert.Equal(newReceivedAt, reply.LastReceivedTime);

        long oid = reply.News[0].OrderId;
        const ulong cancelReceivedAt = 1_700_000_000_222_222_222UL;
        disp.EnqueueCancel(
            new CancelOrderCommand("1", Petr, oid, cancelReceivedAt),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL, origClOrdIdValue: 0UL);
        DrainInbound(disp);
        Assert.Equal(cancelReceivedAt, reply.LastReceivedTime);
    }

    [Fact]
    public void UnknownInstrument_EmitsRejectExecReport_NoUmdfPacket()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        disp.EnqueueNewOrder(new NewOrderCommand("1", SecurityId: 999, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Single(reply.Rejects);
        Assert.Empty(pkt.Packets);
    }

    [Fact]
    public void SequenceNumber_NearOverflow_BumpsSequenceVersion_AndResets()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        Assert.Equal((ushort)1, disp.SequenceVersion);
        disp.TestSetSequenceNumber(uint.MaxValue);
        Assert.Equal(uint.MaxValue, disp.SequenceNumber);

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Single(pkt.Packets);
        Assert.Equal((ushort)2, disp.SequenceVersion);
        Assert.Equal((uint)1, disp.SequenceNumber);
        var packet = pkt.Packets[0];
        Assert.Equal((ushort)2, MemoryMarshal.Read<ushort>(packet.AsSpan(2, 2)));
        Assert.Equal((uint)1, MemoryMarshal.Read<uint>(packet.AsSpan(4, 4)));
    }

    private static int OrderOwnerCount(ChannelDispatcher disp)
    {
        // Owner state moved to Gateway (OrderOwnershipMap); kept as a no-op
        // helper for the few legacy assertions that haven't been migrated.
        _ = disp;
        return 0;
    }


    [Fact]
    public void Cancel_ByUnknownOrigClOrdId_EmitsRejectAndNoUmdfPacket()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        disp.EnqueueCancel(new CancelOrderCommand("42", Petr, OrderId: 0, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 42UL, origClOrdIdValue: 12345UL);
        DrainInbound(disp);

        var rej = Assert.Single(reply.Rejects);
        Assert.Equal(RejectReason.UnknownOrderId, rej.Reason);
        Assert.Empty(pkt.Packets);
    }

    [Fact]
    public void FullyFilledOrder_EvictsClOrdIdMap()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var maker = new FakeSession(outbound);
        var taker = new FakeSession(outbound);

        // Maker rests an order tagged with ClOrdId=10.
        disp.EnqueueNewOrder(new NewOrderCommand("10", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 10UL);
        DrainInbound(disp);
        Assert.Single(maker.News);

        // Taker fully consumes the maker's order.
        disp.EnqueueNewOrder(new NewOrderCommand("20", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 2_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 20UL);
        DrainInbound(disp);

        Assert.Single(maker.Trades);

        // The maker's ClOrdId must be evicted from the (firm, ClOrdId) → orderId map:
        // a Cancel by OrigClOrdID=10 must now reject with UnknownOrderId.
        disp.EnqueueCancel(new CancelOrderCommand("11", Petr, OrderId: 0, 3_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 11UL, origClOrdIdValue: 10UL);
        DrainInbound(disp);

        var rej = Assert.Single(maker.Rejects);
        Assert.Equal(RejectReason.UnknownOrderId, rej.Reason);
    }

    [Fact]
    public void OperatorTradeBust_EmitsSinglePacketWithBustFrame_AndAllocatesNextRptSeq()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        // Seed one resting order so the engine's RptSeq counter is non-zero.
        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        uint rptBefore = GetEngine(disp).CurrentRptSeq;
        Assert.True(rptBefore > 0);
        pkt.Packets.Clear();
        uint seqBefore = disp.SequenceNumber;
        ushort versionBefore = disp.SequenceVersion;

        Assert.True(disp.EnqueueOperatorTradeBust(securityId: Petr,
            priceMantissa: Px(10m), size: 100, tradeId: 4242, tradeDate: 19_500));
        DrainInbound(disp);

        // Engine RptSeq was bumped exactly once by the bust.
        Assert.Equal(rptBefore + 1, GetEngine(disp).CurrentRptSeq);

        // Single bust packet emitted on the incremental channel.
        Assert.Single(pkt.Packets);
        var packet = pkt.Packets[0];
        int expectedLen = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize + WireOffsets.TradeBustBlockLength;
        Assert.Equal(expectedLen, packet.Length);

        // Packet header: same SequenceVersion, monotonic SequenceNumber.
        Assert.Equal(versionBefore, MemoryMarshal.Read<ushort>(packet.AsSpan(WireOffsets.PacketHeaderSequenceVersionOffset, 2)));
        Assert.Equal(seqBefore + 1, MemoryMarshal.Read<uint>(packet.AsSpan(WireOffsets.PacketHeaderSequenceNumberOffset, 4)));

        // SBE TemplateId == 57 (TradeBust).
        int sbeHdrStart = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize;
        ushort templateId = MemoryMarshal.Read<ushort>(packet.AsSpan(sbeHdrStart + 2, 2));
        Assert.Equal((ushort)57, templateId);

        // Body: SecurityId + tradeId + rptSeq match.
        int bodyStart = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
        Assert.Equal(Petr, MemoryMarshal.Read<long>(packet.AsSpan(bodyStart + WireOffsets.TradeBustBodySecurityIdOffset, 8)));
        Assert.Equal(4242u, MemoryMarshal.Read<uint>(packet.AsSpan(bodyStart + WireOffsets.TradeBustBodyTradeIdOffset, 4)));
        Assert.Equal(rptBefore + 1, MemoryMarshal.Read<uint>(packet.AsSpan(bodyStart + WireOffsets.TradeBustBodyRptSeqOffset, 4)));

        // Engine state untouched: book still has the seeded order.
        Assert.Equal(1, GetEngine(disp).OrderCount(Petr));

        // No execution reports flowed back to the FakeSession — the bust is
        // purely a market-data event.
        Assert.Empty(reply.Trades);
        Assert.Empty(reply.Cancels);
    }

    [Fact]
    public void OperatorBumpVersion_ClearsBook_BumpsVersions_EmitsChannelResetPacket()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        // Seed two resting orders so the book has state to clear.
        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(11m), 200, 7, 1_001UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);

        ushort versionBefore = disp.SequenceVersion;
        uint seqBefore = disp.SequenceNumber;
        Assert.Equal(2, pkt.Packets.Count);
        Assert.Equal(2, GetEngine(disp).OrderCount(Petr));
        Assert.True(GetEngine(disp).CurrentRptSeq > 0);
        pkt.Packets.Clear();

        Assert.True(disp.EnqueueOperatorBumpVersion());
        DrainInbound(disp);

        // Book wiped, RptSeq reset.
        Assert.Equal(0, GetEngine(disp).OrderCount(Petr));
        Assert.Equal(0u, GetEngine(disp).CurrentRptSeq);

        // SequenceVersion bumped on the dispatcher (incremental). Snapshot
        // rotator is not attached in this test (no rotator wired) — the
        // attach path is exercised in the host-level integration test.
        Assert.Equal((ushort)(versionBefore + 1), disp.SequenceVersion);
        // SequenceNumber rebased: starts at 0 then the ChannelReset flush
        // increments it to 1.
        Assert.Equal(1u, disp.SequenceNumber);
        Assert.NotEqual(seqBefore, disp.SequenceNumber);

        // Exactly one packet emitted: PacketHeader + framing + sbeHdr +
        // ChannelReset_11 body (12).
        Assert.Single(pkt.Packets);
        var packet = pkt.Packets[0];
        int expectedLen = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize + WireOffsets.ChannelResetBlockLength;
        Assert.Equal(expectedLen, packet.Length);

        // Packet header reflects the NEW SequenceVersion.
        Assert.Equal((ushort)(versionBefore + 1), MemoryMarshal.Read<ushort>(packet.AsSpan(WireOffsets.PacketHeaderSequenceVersionOffset, 2)));
        Assert.Equal(1u, MemoryMarshal.Read<uint>(packet.AsSpan(WireOffsets.PacketHeaderSequenceNumberOffset, 4)));

        // SBE TemplateId == 11.
        int sbeHdrStart = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize;
        ushort templateId = MemoryMarshal.Read<ushort>(packet.AsSpan(sbeHdrStart + 2, 2));
        Assert.Equal((ushort)11, templateId);
    }

    [Fact]
    public void OperatorSnapshotNow_OnDispatcherWithoutRotator_IsNoOp()
    {
        // Without a snapshot rotator attached the work item is consumed but
        // produces no incremental packets — verifies the dispatch path is
        // safe in the no-rotator case (the host wires the rotator only when
        // the channel has a snapshot config).
        var (disp, pkt, outbound) = NewDispatcher();
        Assert.True(disp.EnqueueOperatorSnapshotNow());
        DrainInbound(disp);
        Assert.Empty(pkt.Packets);
    }

    /// <summary>
    /// Issue #138: ensures /metrics-style scrapes from a non-dispatch thread
    /// always observe a coherent (SequenceVersion ≥ 1, SequenceNumber ≥ 0)
    /// pair while the dispatch loop is publishing packets concurrently.
    /// Uses Volatile.Read accessors on the public properties — no torn or
    /// hoisted reads should be observable.
    /// </summary>
    [Fact]
    public async Task SequenceCounters_ReadFromForeignThread_NeverTearOrRegress()
    {
        var (disp, _, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);
        disp.Start();
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            // Producer: enqueue resting orders as fast as the bounded queue allows.
            var producer = Task.Run(() =>
            {
                ulong cl = 0;
                while (!stop.IsCancellationRequested)
                {
                    disp.EnqueueNewOrder(
                        new NewOrderCommand(
                            (++cl).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Petr, Side.Buy, OrderType.Limit, TimeInForce.Day,
                            Px(10m), 1, 7, 1_000UL),
                        reply.Id, reply.EnteringFirm, clOrdIdValue: cl);
                }
            });
            // Reader: scrape the public counters from another thread; assert
            // monotonic-non-decreasing within a (version) generation and that
            // version is always ≥ the value observed at construction.
            var reader = Task.Run(() =>
            {
                ushort lastVer = disp.SequenceVersion;
                uint lastSeq = 0;
                while (!stop.IsCancellationRequested)
                {
                    ushort v = disp.SequenceVersion;
                    uint s = disp.SequenceNumber;
                    Assert.True(v >= 1, $"SequenceVersion torn: observed {v}");
                    if (v == lastVer)
                    {
                        // Within the same version, sequence must not regress.
                        Assert.True(s >= lastSeq,
                            $"SequenceNumber regressed within version {v}: {lastSeq} -> {s}");
                    }
                    lastVer = v;
                    lastSeq = s;
                }
            });
            await Task.WhenAll(producer, reader);
        }
        finally
        {
            await disp.DisposeAsync();
        }
    }

    [Fact]
    public void DispatchQueueFull_IncrementsMetricAndDropsItem()
    {
        // Issue #155: verify the SRE counter ticks when the bounded
        // dispatch queue is full. inboundCapacity=1 + a non-started
        // dispatcher leaves the queue static so the second TryWrite
        // returns false deterministically.
        var pkt = new RecordingPacketSink();
        var outbound = new RecordingOutbound();
        var metrics = new ChannelMetrics(channelNumber: 1);
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = pkt,
                Outbound = outbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1_000_000_000UL),
                TradeDate = 19_000,
                InboundCapacity = 1,
                Metrics = metrics,
            });

        var reply = new FakeSession(outbound);

        bool first = disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        Assert.True(first, "first enqueue with a 1-slot channel should succeed");
        Assert.Equal(0, metrics.DispatchQueueFull);

        // Second enqueue must fail because the loop is not draining.
        // Issue #153: the bool return is the contract the gateway uses to
        // emit BusinessMessageReject(SystemBusy) on the wire.
        bool second = disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL);
        Assert.False(second, "second enqueue must report backpressure to caller");
        Assert.Equal(1, metrics.DispatchQueueFull);
    }

    [Fact]
    public void OnDecodeError_IncrementsDecodeErrorsMetric()
    {
        // Issue #155: every gateway decode failure must bump the SRE counter.
        var pkt = new RecordingPacketSink();
        var outbound = new RecordingOutbound();
        var metrics = new ChannelMetrics(channelNumber: 1);
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = pkt,
                Outbound = outbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1_000_000_000UL),
                TradeDate = 19_000,
                Metrics = metrics,
            });

        var reply = new FakeSession(outbound);
        disp.OnDecodeError(reply.Id, "bad sbe header");
        disp.OnDecodeError(reply.Id, "short body");

        Assert.Equal(2, metrics.DecodeErrors);
    }

    /// <summary>
    /// Issue #170 — a single bad work-item (engine bug, sink throw, etc.)
    /// must NOT terminate the channel's dispatch loop. The loop catches
    /// the exception, increments <c>exch_dispatcher_crash_total</c>, logs
    /// with full context, and continues draining so subsequent commands
    /// still get serviced.
    /// </summary>
    private sealed class ThrowingOutbound : ICoreOutbound
    {
        public int Throws;
        public int Successes;
        public bool ThrowOnNext = true;
        public bool WriteExecutionReportNew(B3.Exchange.Contracts.SessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle d = default)
        {
            if (ThrowOnNext) { ThrowOnNext = false; Throws++; throw new InvalidOperationException("synthetic crash"); }
            Successes++;
            return true;
        }
        public bool WriteExecutionReportTrade(B3.Exchange.Contracts.SessionId session, in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveTrade(B3.Exchange.Contracts.SessionId ownerSession, ulong ownerClOrdId, long restingOrderId, in TradeEvent e, long leavesQty, long cumQty, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveCancel(B3.Exchange.Contracts.SessionId ownerSession, ulong ownerClOrdId, long orderId, in OrderCanceledEvent e, ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportModify(B3.Exchange.Contracts.SessionId session, long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq, ulong receivedTimeNanos = ulong.MaxValue, DurabilityHandle d = default, InvestorId? iv = null) => true;
        public bool WriteExecutionReportReject(B3.Exchange.Contracts.SessionId session, in RejectEvent e, ulong clOrdIdValue, DurabilityHandle d = default) => true;
    }

    private sealed class TestSession
    {
        public B3.Exchange.Contracts.SessionId Id { get; } = new("crashtest");
        public uint EnteringFirm => 7;
    }

    [Fact]
    public async Task DispatcherLoop_ContainsWorkItemException_LoopSurvivesAndCrashCounterIncrements()
    {
        var pkt = new RecordingPacketSink();
        var outbound = new ThrowingOutbound();
        var metrics = new ChannelMetrics(channelNumber: 1);
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = pkt,
                Outbound = outbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                TimeSource = new FakeNanosTimeSource(1_000_000_000UL),
                TradeDate = 19_000,
                Metrics = metrics,
            });
        try
        {
            disp.Start();
            var reply = new TestSession();

            // First enqueue triggers the throw inside ProcessOne →
            // dispatch loop catches it and bumps the crash counter.
            Assert.True(disp.EnqueueNewOrder(
                new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
                reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL));

            // Second enqueue must succeed and be serviced — the loop must
            // still be alive after containing the prior crash.
            Assert.True(disp.EnqueueNewOrder(
                new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(11m), 100, 7, 1_000UL),
                reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL));

            // Wait up to 2s for both work items to be processed.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && (metrics.DispatcherCrashes < 1 || outbound.Successes < 1))
            {
                await Task.Delay(20);
            }

            Assert.Equal(1, metrics.DispatcherCrashes);
            Assert.Equal(1, outbound.Throws);
            Assert.True(outbound.Successes >= 1, "second order must be processed after the loop survived the crash");
        }
        finally
        {
            await disp.DisposeAsync();
        }
    }

    private static MatchingEngine GetEngine(ChannelDispatcher disp)
    {
        var f = typeof(ChannelDispatcher).GetField("_engine",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (MatchingEngine)f.GetValue(disp)!;
    }

    private static void DrainInbound(ChannelDispatcher disp) => disp.CreateTestProbe().DrainInbound();

    [Fact]
    public void OperatorRestateGt_RoutesRestateToOwner_NoUmdfFrame_NoEviction()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        // Resting GTC order owned by `reply`.
        disp.EnqueueNewOrder(new NewOrderCommand("g1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Gtc, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        long orderId = Assert.Single(reply.News).OrderId;
        int packetsAfterRest = pkt.Packets.Count;
        uint seqAfterRest = disp.SequenceNumber;

        disp.EnqueueOperatorRestateGt(currentDate: 20_000);
        DrainInbound(disp);

        // Routed to the owning session as a restatement.
        var restate = Assert.Single(reply.Restates);
        Assert.Equal(orderId, restate.OrderId);
        Assert.Equal(TimeInForce.Gtc, restate.Tif);
        Assert.Equal(100L, restate.OpenQuantity);
        // Private ER only: no new UMDF packet, no RptSeq advance.
        Assert.Equal(packetsAfterRest, pkt.Packets.Count);
        Assert.Equal(seqAfterRest, disp.SequenceNumber);

        // Registry NOT evicted: a subsequent cancel still resolves the owner.
        disp.EnqueueCancel(new CancelOrderCommand("c1", Petr, orderId, 3_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);
        Assert.Single(reply.Cancels);
    }


    [Fact]
    public void OperatorExpireDay_CancelsDayOrder_RoutesExpiredToOwner_EmitsUmdfFrame_Evicts()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        // Resting Day order owned by `reply`.
        disp.EnqueueNewOrder(new NewOrderCommand("d1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        long orderId = Assert.Single(reply.News).OrderId;
        int packetsAfterRest = pkt.Packets.Count;
        uint seqAfterRest = disp.SequenceNumber;

        disp.EnqueueOperatorExpireDay();
        DrainInbound(disp);

        // Terminal EXPIRED cancel routed to the owning session.
        var cancel = Assert.Single(reply.Cancels);
        Assert.Equal(orderId, cancel.OrderId);
        Assert.Equal(B3.Exchange.Matching.CancelReason.DayExpired, cancel.Reason);
        // Public event: a new UMDF packet (OrderDelete) and an RptSeq advance.
        Assert.True(pkt.Packets.Count > packetsAfterRest);
        Assert.True(disp.SequenceNumber > seqAfterRest);

        // Registry evicted: a follow-up cancel for the same order no longer resolves an owner.
        reply.Cancels.Clear();
        disp.EnqueueCancel(new CancelOrderCommand("c1", Petr, orderId, 3_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);
        Assert.Empty(reply.Cancels);
    }

    [Fact]
    public void OperatorSetTradingPhase_EmitsSecurityStatusFrameAndGatesNewOrders()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        // Transition PETR to CLOSE — engine emits a SecurityStatus frame.
        Assert.True(disp.EnqueueOperatorSetTradingPhase(Petr, B3.Exchange.Matching.TradingPhase.Close));
        DrainInbound(disp);

        Assert.Single(pkt.Packets);
        var packet = pkt.Packets[0];
        int expectedLen = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize
            + WireOffsets.SbeMessageHeaderSize + WireOffsets.SecurityStatusBlockLength;
        Assert.Equal(expectedLen, packet.Length);

        // SBE TemplateId == 3 (SecurityStatus).
        int sbeHdrStart = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize;
        ushort templateId = MemoryMarshal.Read<ushort>(packet.AsSpan(sbeHdrStart + 2, 2));
        Assert.Equal((ushort)3, templateId);

        int bodyStart = WireOffsets.PacketHeaderSize + WireOffsets.FramingHeaderSize + WireOffsets.SbeMessageHeaderSize;
        Assert.Equal(Petr, MemoryMarshal.Read<long>(packet.AsSpan(bodyStart + WireOffsets.SecurityStatusBodySecurityIdOffset, 8)));
        Assert.Equal((byte)4 /* CLOSE */, packet[bodyStart + WireOffsets.SecurityStatusBodySecurityTradingStatusOffset]);
        pkt.Packets.Clear();

        // A new order while CLOSE must be rejected with MarketClosed.
        disp.EnqueueNewOrder(new NewOrderCommand("c1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 9_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        var rej = Assert.Single(reply.Rejects);
        Assert.Equal(RejectReason.MarketClosed, rej.Reason);

        // Reopen and confirm a subsequent order is accepted.
        Assert.True(disp.EnqueueOperatorSetTradingPhase(Petr, B3.Exchange.Matching.TradingPhase.Open));
        DrainInbound(disp);
        reply.Rejects.Clear();
        disp.EnqueueNewOrder(new NewOrderCommand("c2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 9_001UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);
        Assert.Empty(reply.Rejects);
        Assert.Equal(1, GetEngine(disp).OrderCount(Petr));
    }

    [Fact]
    public void OperatorSetTradingPhase_Idempotent_NoPacketEmitted()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        _ = new FakeSession(outbound);

        // Default is Open; transitioning to Open should be a no-op.
        Assert.True(disp.EnqueueOperatorSetTradingPhase(Petr, B3.Exchange.Matching.TradingPhase.Open));
        DrainInbound(disp);

        Assert.Empty(pkt.Packets);
    }

    [Fact]
    public void Issue319_RestingMultiFill_EmitsCumulativeCumQtyAndLeavesQty()
    {
        // Repro from issue body: SELL 200 resting + two BUY 100 aggressors.
        // Pre-fix the resting side received cumQty=100/leaves=0 on each
        // trade — consumers applying "advance only if cumQty > order.cum"
        // dropped the 2nd ER as stale and never observed the terminal
        // Filled. With #319, cum/leaves accumulate monotonically.
        var (disp, _, outbound) = NewDispatcher();
        var maker = new FakeSession(outbound);
        var taker1 = new FakeSession(outbound);
        var taker2 = new FakeSession(outbound);

        disp.EnqueueNewOrder(new NewOrderCommand("M", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 200, 7, 1_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 100UL);
        DrainInbound(disp);

        disp.EnqueueNewOrder(new NewOrderCommand("T1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 2_000UL),
            taker1.Id, taker1.EnteringFirm, clOrdIdValue: 201UL);
        DrainInbound(disp);

        disp.EnqueueNewOrder(new NewOrderCommand("T2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 9, 3_000UL),
            taker2.Id, taker2.EnteringFirm, clOrdIdValue: 202UL);
        DrainInbound(disp);

        // Maker's passive ERs: cumulative across the two fills.
        Assert.Equal(2, maker.TradeQty.Count);
        Assert.Equal((100L, 100L), maker.TradeQty[0]);
        Assert.Equal((0L, 200L), maker.TradeQty[1]);

        // Each taker fully filled in one print → cum=100/leaves=0.
        var t1 = Assert.Single(taker1.TradeQty);
        Assert.Equal((0L, 100L), t1);
        var t2 = Assert.Single(taker2.TradeQty);
        Assert.Equal((0L, 100L), t2);
    }

    [Fact]
    public void Issue319_AggressorMultiFill_EmitsCumulativeCumQtyAndLeavesQty()
    {
        // Aggressor side: a single BUY 200 that sweeps two resting SELL
        // 100s must emit two ER_Trade frames with cumQty advancing 100
        // → 200 and leaves 100 → 0. Pre-#319 both prints carried
        // cum=100/leaves=0.
        var (disp, _, outbound) = NewDispatcher();
        var m1 = new FakeSession(outbound);
        var m2 = new FakeSession(outbound);
        var taker = new FakeSession(outbound);

        disp.EnqueueNewOrder(new NewOrderCommand("M1", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            m1.Id, m1.EnteringFirm, clOrdIdValue: 11UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("M2", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 1_500UL),
            m2.Id, m2.EnteringFirm, clOrdIdValue: 12UL);
        DrainInbound(disp);

        disp.EnqueueNewOrder(new NewOrderCommand("T", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 200, 9, 2_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 99UL);
        DrainInbound(disp);

        Assert.Equal(2, taker.TradeQty.Count);
        Assert.Equal((100L, 100L), taker.TradeQty[0]);
        Assert.Equal((0L, 200L), taker.TradeQty[1]);
    }

    [Fact]
    public void Issue319_Replace_AfterPartialFill_PreservesCumQty()
    {
        // After a partial fill, a successful Replace that bumps the
        // wire OrderQty must keep cum monotonic: subsequent fills
        // continue from the prior cum, with leaves recomputed against
        // the new (cum + newRemaining) denominator.
        var (disp, _, outbound) = NewDispatcher();
        var maker = new FakeSession(outbound);
        var taker = new FakeSession(outbound);

        // Maker rests SELL 300 @ 10.
        disp.EnqueueNewOrder(new NewOrderCommand("M", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 300, 7, 1_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 50UL);
        DrainInbound(disp);
        long makerOrderId = maker.News[0].OrderId;

        // First taker eats 100 → maker cum=100, leaves=200.
        disp.EnqueueNewOrder(new NewOrderCommand("T1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 2_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 201UL);
        DrainInbound(disp);
        Assert.Equal((200L, 100L), maker.TradeQty[^1]);

        // Maker replaces the resting order down to 100 leaves (price
        // unchanged → priority preserved). New OrderQty becomes
        // cumQty(100) + newLeaves(100) = 200.
        disp.EnqueueReplace(new ReplaceOrderCommand("M2", Petr, makerOrderId, Px(10m), 100, 3_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 51UL, origClOrdIdValue: 50UL);
        DrainInbound(disp);

        // Second taker eats the remaining 100 → terminal fill. Maker's
        // ER must carry cumQty=200/leaves=0.
        disp.EnqueueNewOrder(new NewOrderCommand("T2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 9, 4_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 202UL);
        DrainInbound(disp);

        Assert.Equal(2, maker.TradeQty.Count);
        Assert.Equal((0L, 200L), maker.TradeQty[^1]);
    }

    [Fact]
    public void Issue482_Replace_PriorityLost_AfterPartialFill_PreservesCumQty()
    {
        // Issue #482: when a Replace changes the price (priority lost → DEL + NEW),
        // the replacement order must inherit the original's CumQty so subsequent
        // fills continue from there, not from zero.
        var (disp, _, outbound) = NewDispatcher();
        var maker = new FakeSession(outbound);
        var taker = new FakeSession(outbound);

        // Maker rests SELL 200 @ 10.
        disp.EnqueueNewOrder(new NewOrderCommand("M", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 200, 7, 1_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 50UL);
        DrainInbound(disp);
        long makerOrderId = maker.News[0].OrderId;

        // First taker eats 100 → maker cum=100, leaves=100.
        disp.EnqueueNewOrder(new NewOrderCommand("T1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 2_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 201UL);
        DrainInbound(disp);
        Assert.Equal((100L, 100L), maker.TradeQty[^1]); // leaves=100, cum=100

        // Maker replaces to a different price (priority lost → DEL + NEW).
        // NewQuantity = 100 (new remaining). With prior cum=100, the new
        // effective OrderQty becomes 200 and leaves stays 100.
        disp.EnqueueReplace(new ReplaceOrderCommand("M2", Petr, makerOrderId, Px(9m), 100, 3_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 51UL, origClOrdIdValue: 50UL);
        DrainInbound(disp);

        // Second taker fills the remaining 100 → terminal fill.
        // Maker's ER must carry cumQty=200, leaves=0 (not cumQty=100 as if
        // the replacement started from scratch).
        disp.EnqueueNewOrder(new NewOrderCommand("T2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9m), 100, 9, 4_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 202UL);
        DrainInbound(disp);

        Assert.Equal(2, maker.TradeQty.Count);
        Assert.Equal((0L, 200L), maker.TradeQty[^1]); // leaves=0, cum=200
    }

    [Fact]
    public void Issue484_IOC_Replace_AfterPartialFill_TerminalER_HasLeavesQtyZero()
    {
        // Issue #484: IOC Replace after a partial fill must emit leavesQty=0
        // on the terminal ER_Trade even when _aggressorOrigQty inherited prior fills.
        //
        // Scenario:
        //   1. Maker rests SELL 200 @ 10
        //   2. Taker1 fills 100 → maker cum=100, leaves=100
        //   3. Maker replaces to IOC @ 9, qty=200 (new total order qty)
        //   4. Only 100 available at 9 (taker2's resting BUY 100 @ 9)
        //   5. IOC fills 100, residual 100 dropped
        //   6. Maker's ER_Trade must have cumQty=200, leavesQty=0 (NOT 100)
        var (disp, _, outbound) = NewDispatcher();
        var maker = new FakeSession(outbound);
        var taker1 = new FakeSession(outbound);
        var taker2 = new FakeSession(outbound);

        // Step 1: Maker rests SELL 200 @ 10.
        disp.EnqueueNewOrder(new NewOrderCommand("M", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 200, 7, 1_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 50UL);
        DrainInbound(disp);
        long makerOrderId = maker.News[0].OrderId;

        // Step 2: First taker fills 100 → maker cum=100, leaves=100.
        disp.EnqueueNewOrder(new NewOrderCommand("T1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 2_000UL),
            taker1.Id, taker1.EnteringFirm, clOrdIdValue: 201UL);
        DrainInbound(disp);
        Assert.Equal((100L, 100L), maker.TradeQty[^1]); // leaves=100, cum=100

        // Step 3: Second taker rests 100 @ 9 to provide partial liquidity.
        // Quantity must be a lot-size multiple (100). The IOC will fill this
        // 100 but still have a 100-lot residual that gets dropped.
        disp.EnqueueNewOrder(new NewOrderCommand("T2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9m), 100, 9, 3_000UL),
            taker2.Id, taker2.EnteringFirm, clOrdIdValue: 202UL);
        DrainInbound(disp);

        // Step 4: Maker replaces to IOC @ 9, qty=200 (priority lost → DEL + NEW as IOC).
        // _aggressorOrigQty = orig.CumQty(100) + NewQuantity(200) = 300
        // Only 100 lots are available at 9, so IOC fills 100 and drops residual 100.
        disp.EnqueueReplace(new ReplaceOrderCommand("M2", Petr, makerOrderId, Px(9m), 200, 4_000UL) { NewTif = TimeInForce.IOC },
            maker.Id, maker.EnteringFirm, clOrdIdValue: 51UL, origClOrdIdValue: 50UL);
        DrainInbound(disp);

        // Step 5: verify terminal ER_Trade: cumQty=200, leavesQty=0.
        // Without the fix _aggressorOrigQty=300, aggCum=200, naive leaves=100.
        Assert.Equal(2, maker.TradeQty.Count);
        Assert.Equal((0L, 200L), maker.TradeQty[^1]); // leavesQty=0, cumQty=200
    }

    [Fact]
    public void Issue484_IOC_MultiFill_IntermediateERsHaveNaturalLeavesQty()
    {
        // Issue #484 (multi-fill guard): an IOC aggressor that sweeps multiple
        // resting orders must emit the correct natural leavesQty on all
        // intermediate fills and only 0 on the final fill.
        var (disp, _, outbound) = NewDispatcher();
        var m1 = new FakeSession(outbound) { EnteringFirm = 7 };
        var m2 = new FakeSession(outbound) { EnteringFirm = 8 };
        var taker = new FakeSession(outbound) { EnteringFirm = 9 };

        // Two resting SELLs at 10.
        disp.EnqueueNewOrder(new NewOrderCommand("M1", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            m1.Id, m1.EnteringFirm, clOrdIdValue: 11UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("M2", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 1_500UL),
            m2.Id, m2.EnteringFirm, clOrdIdValue: 12UL);
        DrainInbound(disp);

        // IOC BUY 300 @ 10 — only 200 available, fills 100 + 100, drops residual 100.
        disp.EnqueueNewOrder(new NewOrderCommand("T", Petr, Side.Buy, OrderType.Limit, TimeInForce.IOC, Px(10m), 300, 9, 2_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 99UL);
        DrainInbound(disp);

        // Taker (aggressor IOC) gets 2 fills:
        //   fill 1 (intermediate): cumQty=100, leavesQty=200  (NOT 0)
        //   fill 2 (terminal):     cumQty=200, leavesQty=0
        Assert.Equal(2, taker.TradeQty.Count);
        Assert.Equal((200L, 100L), taker.TradeQty[0]); // intermediate: leaves=200, cum=100
        Assert.Equal((0L, 200L), taker.TradeQty[1]);   // terminal: leaves=0, cum=200
    }

    [Fact]
    public async Task Issue321_OperatorUncrossAuction_ReservedToOpen_EmitsAuctionPrintAndPhaseChange()
    {
        var (disp, _, outbound) = NewDispatcher();
        var maker = new FakeSession(outbound);
        var taker = new FakeSession(outbound);

        Assert.True(disp.EnqueueOperatorSetTradingPhase(Petr, B3.Exchange.Matching.TradingPhase.Reserved));
        DrainInbound(disp);

        disp.EnqueueNewOrder(new NewOrderCommand("M", Petr, Side.Sell, OrderType.Limit, TimeInForce.GoodForAuction, Px(10m), 200, 7, 1_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("T", Petr, Side.Buy, OrderType.Limit, TimeInForce.GoodForAuction, Px(10m), 200, 8, 2_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);

        var tcs = new TaskCompletionSource<PhaseChangeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(disp.EnqueueOperatorUncrossAuction(Petr, B3.Exchange.Matching.TradingPhase.Open, tcs));
        DrainInbound(disp);

        Assert.True(tcs.Task.IsCompletedSuccessfully);
        var outcome = await tcs.Task;
        Assert.True(outcome.TransitionApplied);
        Assert.Equal(B3.Exchange.Matching.TradingPhase.Reserved, outcome.PreviousPhase);
        Assert.Equal(B3.Exchange.Matching.TradingPhase.Open, outcome.CurrentPhase);
        Assert.NotNull(outcome.UncrossPrint);
        Assert.Equal(AuctionPrintKind.Opening, outcome.UncrossPrint!.Value.Kind);
        Assert.Equal(200L, outcome.UncrossPrint.Value.ClearedQuantity);
        Assert.Equal(Px(10m), outcome.UncrossPrint.Value.PriceMantissa);
    }

    [Fact]
    public async Task Issue321_OperatorUncrossAuction_FCCToClose_EmitsClosingPrint()
    {
        var (disp, _, outbound) = NewDispatcher();
        var maker = new FakeSession(outbound);
        var taker = new FakeSession(outbound);

        Assert.True(disp.EnqueueOperatorSetTradingPhase(Petr, B3.Exchange.Matching.TradingPhase.FinalClosingCall));
        DrainInbound(disp);

        disp.EnqueueNewOrder(new NewOrderCommand("M", Petr, Side.Sell, OrderType.Limit, TimeInForce.AtClose, Px(10m), 100, 7, 1_000UL),
            maker.Id, maker.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        disp.EnqueueNewOrder(new NewOrderCommand("T", Petr, Side.Buy, OrderType.Limit, TimeInForce.AtClose, Px(10m), 100, 8, 2_000UL),
            taker.Id, taker.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);

        var tcs = new TaskCompletionSource<PhaseChangeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(disp.EnqueueOperatorUncrossAuction(Petr, B3.Exchange.Matching.TradingPhase.Close, tcs));
        DrainInbound(disp);

        var outcome = await tcs.Task;
        Assert.True(outcome.TransitionApplied);
        Assert.Equal(B3.Exchange.Matching.TradingPhase.FinalClosingCall, outcome.PreviousPhase);
        Assert.Equal(B3.Exchange.Matching.TradingPhase.Close, outcome.CurrentPhase);
        Assert.NotNull(outcome.UncrossPrint);
        Assert.Equal(AuctionPrintKind.Closing, outcome.UncrossPrint!.Value.Kind);
        Assert.Equal(100L, outcome.UncrossPrint.Value.ClearedQuantity);
    }

    [Fact]
    public void Issue321_OperatorUncrossAuction_InvalidTransition_FailsCompletionWithInvalidOperation()
    {
        var (disp, _, outbound) = NewDispatcher();
        _ = new FakeSession(outbound);

        // Default phase is Open; uncross to Reserved is not a permitted target.
        var tcs = new TaskCompletionSource<PhaseChangeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(disp.EnqueueOperatorUncrossAuction(Petr, B3.Exchange.Matching.TradingPhase.Reserved, tcs));
        DrainInbound(disp);

        Assert.True(tcs.Task.IsFaulted);
        Assert.IsType<InvalidOperationException>(tcs.Task.Exception!.InnerException);
    }

    [Fact]
    public async Task Issue321_OperatorSetTradingPhase_WithCompletion_ReturnsOutcome()
    {
        var (disp, _, outbound) = NewDispatcher();
        _ = new FakeSession(outbound);

        Assert.True(disp.EnqueueOperatorSetTradingPhase(Petr, B3.Exchange.Matching.TradingPhase.Pause));
        DrainInbound(disp);

        var tcs = new TaskCompletionSource<PhaseChangeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(disp.EnqueueOperatorSetTradingPhase(Petr, B3.Exchange.Matching.TradingPhase.Open, tcs));
        DrainInbound(disp);

        Assert.True(tcs.Task.IsCompletedSuccessfully);
        var outcome = await tcs.Task;
        Assert.True(outcome.TransitionApplied);
        Assert.Equal(B3.Exchange.Matching.TradingPhase.Pause, outcome.PreviousPhase);
        Assert.Equal(B3.Exchange.Matching.TradingPhase.Open, outcome.CurrentPhase);
        Assert.Null(outcome.UncrossPrint);
    }

    [Fact]
    public void Issue321_PhaseSnapshot_ReflectsLatestTransition()
    {
        var (disp, _, _) = NewDispatcher();

        Assert.True(disp.EnqueueOperatorSetTradingPhase(Petr, B3.Exchange.Matching.TradingPhase.Pause));
        DrainInbound(disp);

        Assert.True(disp.TryGetPhaseSnapshot(Petr, out var phase));
        Assert.Equal(B3.Exchange.Matching.TradingPhase.Pause, phase);
    }

    // ===== Issue #322 =====

    [Fact]
    public async Task Issue322_OperatorHaltInstrument_RejectsSubsequentOrdersAndPopulatesSnapshot()
    {
        var (disp, _, outbound) = NewDispatcher();
        var session = new FakeSession(outbound);

        var tcs = new TaskCompletionSource<HaltOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(disp.EnqueueOperatorHalt(Petr, B3.Exchange.Matching.HaltReason.RegulatoryHalt, "manual", tcs));
        DrainInbound(disp);

        var outcome = await tcs.Task;
        Assert.True(outcome.StateChanged);
        Assert.True(outcome.IsHaltedNow);
        Assert.Equal(B3.Exchange.Matching.HaltReason.RegulatoryHalt, outcome.Reason);
        Assert.Equal("manual", outcome.Note);

        Assert.True(disp.TryGetHaltSnapshot(Petr, out var snap));
        Assert.Equal(B3.Exchange.Matching.HaltReason.RegulatoryHalt, snap.Reason);

        // Subsequent NewOrder is rejected back to the originating session.
        session.Rejects.Clear();
        disp.EnqueueNewOrder(new NewOrderCommand("X", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, session.EnteringFirm, 1_000UL),
            session.Id, session.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        var rej = Assert.Single(session.Rejects);
        Assert.Equal(B3.Exchange.Matching.RejectReason.InstrumentHalted, rej.Reason);
    }

    [Fact]
    public async Task Issue322_OperatorResumeInstrument_RestoresOrderAcceptanceAndClearsSnapshot()
    {
        var (disp, _, outbound) = NewDispatcher();
        var session = new FakeSession(outbound);

        Assert.True(disp.EnqueueOperatorHalt(Petr, B3.Exchange.Matching.HaltReason.NewsHold, null));
        DrainInbound(disp);
        Assert.True(disp.TryGetHaltSnapshot(Petr, out _));

        var resumeTcs = new TaskCompletionSource<HaltOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(disp.EnqueueOperatorResume(Petr, resumeTcs));
        DrainInbound(disp);

        var outcome = await resumeTcs.Task;
        Assert.True(outcome.StateChanged);
        Assert.False(outcome.IsHaltedNow);
        Assert.False(disp.TryGetHaltSnapshot(Petr, out _));

        // New order now accepted again.
        session.News.Clear();
        session.Rejects.Clear();
        disp.EnqueueNewOrder(new NewOrderCommand("Y", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, session.EnteringFirm, 2_000UL),
            session.Id, session.EnteringFirm, clOrdIdValue: 2UL);
        DrainInbound(disp);
        Assert.Empty(session.Rejects);
        Assert.Single(session.News);
    }

    [Fact]
    public void Issue322_OperatorPhaseChange_WhileHalted_FaultsCompletionWithInvalidOperation()
    {
        var (disp, _, _) = NewDispatcher();
        Assert.True(disp.EnqueueOperatorHalt(Petr, B3.Exchange.Matching.HaltReason.RegulatoryHalt, null));
        DrainInbound(disp);

        var tcs = new TaskCompletionSource<PhaseChangeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(disp.EnqueueOperatorSetTradingPhase(Petr, B3.Exchange.Matching.TradingPhase.Pause, tcs));
        DrainInbound(disp);

        Assert.True(tcs.Task.IsFaulted);
        Assert.IsType<InvalidOperationException>(tcs.Task.Exception!.InnerException);
    }
}
