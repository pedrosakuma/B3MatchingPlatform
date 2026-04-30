using System.Runtime.InteropServices;
using B3.Exchange.EntryPoint;
using B3.Exchange.Instruments;
using B3.Exchange.Integration;
using B3.Exchange.Matching;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Integration.Tests;

public class ChannelDispatcherTests
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

    private sealed class RecordingReply : IEntryPointResponseChannel
    {
        public long ConnectionId => 1;
        public uint EnteringFirm => 7;
        public bool IsOpen => true;
        public List<string> Calls { get; } = new();
        public List<OrderAcceptedEvent> News { get; } = new();
        public List<OrderCanceledEvent> Cancels { get; } = new();
        public List<RejectEvent> Rejects { get; } = new();
        public List<TradeEvent> Trades { get; } = new();
        public bool CaptureCancelIds { get; set; }
        public List<(ulong ClOrdId, ulong OrigClOrdId)> CancelIds { get; } = new();
        public bool WriteExecutionReportNew(in OrderAcceptedEvent e) { News.Add(e); Calls.Add("New"); return true; }
        public bool WriteExecutionReportTrade(in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty)
        { Trades.Add(e); Calls.Add(isAggressor ? "TradeAgg" : "TradePass"); return true; }
        public bool WriteExecutionReportCancel(in OrderCanceledEvent e, ulong clOrdIdValue, ulong origClOrdIdValue) { Cancels.Add(e); Calls.Add("Cancel"); if (CaptureCancelIds) CancelIds.Add((clOrdIdValue, origClOrdIdValue)); return true; }
        public bool WriteExecutionReportModify(long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq) { Calls.Add("Modify"); return true; }
        public bool WriteExecutionReportReject(in RejectEvent e, ulong clOrdIdValue) { Rejects.Add(e); Calls.Add("Reject"); return true; }
        public bool WriteSessionReject(byte terminationCode) { Calls.Add("SessionReject"); return true; }
        public bool WriteBusinessMessageReject(byte refMsgType, uint refSeqNum, ulong businessRejectRefId, uint businessRejectReason, string? text = null) { Calls.Add("BusinessReject"); return true; }
    }

    private static (ChannelDispatcher disp, RecordingPacketSink pkt) NewDispatcher()
    {
        var pkt = new RecordingPacketSink();
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink),
            packetSink: pkt,
            nowNanos: () => 1_000_000_000UL, tradeDate: 19_000);
        return (disp, pkt);
    }

    [Fact]
    public void NewOrder_AcceptedRestingOrder_EmitsOrderAddedFrameAndExecReportNew()
    {
        var (disp, pkt) = NewDispatcher();
        var reply = new RecordingReply();

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply, clOrdIdValue: 1UL);

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
    public void Crossing_AggressorAndResting_BothGetTradeReportsAndDeleteFrames()
    {
        var (disp, pkt) = NewDispatcher();
        var maker = new RecordingReply();
        var taker = new RecordingReply();

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            maker, clOrdIdValue: 1UL);
        DrainInbound(disp);
        Assert.Single(maker.News);
        pkt.Packets.Clear();

        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 2_000UL),
            taker, clOrdIdValue: 2UL);
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
        var (disp, pkt) = NewDispatcher();
        var reply = new RecordingReply();

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply, clOrdIdValue: 1UL);
        DrainInbound(disp);
        long oid = reply.News[0].OrderId;
        pkt.Packets.Clear();

        disp.EnqueueCancel(new CancelOrderCommand("1", Petr, oid, 2_000UL), reply, clOrdIdValue: 1UL, origClOrdIdValue: 0UL);
        DrainInbound(disp);

        Assert.Single(reply.Cancels);
        Assert.Equal(oid, reply.Cancels[0].OrderId);
        Assert.Single(pkt.Packets);
    }

    [Fact]
    public void UnknownInstrument_EmitsRejectExecReport_NoUmdfPacket()
    {
        var (disp, pkt) = NewDispatcher();
        var reply = new RecordingReply();

        disp.EnqueueNewOrder(new NewOrderCommand("1", SecurityId: 999, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply, clOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Single(reply.Rejects);
        Assert.Empty(pkt.Packets);
    }

    [Fact]
    public void Cancel_ByOrigClOrdId_ResolvesViaSessionMap()
    {
        var (disp, pkt) = NewDispatcher();
        var reply = new RecordingReply();

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply, clOrdIdValue: 1UL);
        DrainInbound(disp);
        long oid = reply.News[0].OrderId;
        pkt.Packets.Clear();

        // Cancel without OrderID — only OrigClOrdID = 1.
        disp.EnqueueCancel(new CancelOrderCommand("99", Petr, OrderId: 0, 2_000UL),
            reply, clOrdIdValue: 99UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Single(reply.Cancels);
        Assert.Equal(oid, reply.Cancels[0].OrderId);
        Assert.Empty(reply.Rejects);
        Assert.Single(pkt.Packets); // DeleteOrder UMDF frame
    }

    [Fact]
    public void Cancel_ByUnknownOrigClOrdId_EmitsRejectAndNoUmdfPacket()
    {
        var (disp, pkt) = NewDispatcher();
        var reply = new RecordingReply();

        disp.EnqueueCancel(new CancelOrderCommand("42", Petr, OrderId: 0, 1_000UL),
            reply, clOrdIdValue: 42UL, origClOrdIdValue: 12345UL);
        DrainInbound(disp);

        var rej = Assert.Single(reply.Rejects);
        Assert.Equal(RejectReason.UnknownOrderId, rej.Reason);
        Assert.Empty(pkt.Packets);
    }

    [Fact]
    public void Replace_ByOrigClOrdId_LostPriority_ResolvesViaSessionMap()
    {
        var (disp, pkt) = NewDispatcher();
        var reply = new RecordingReply();

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply, clOrdIdValue: 1UL);
        DrainInbound(disp);
        long origOid = reply.News[0].OrderId;
        pkt.Packets.Clear();

        // Replace with NEW PRICE (loses priority): only OrigClOrdID provided.
        disp.EnqueueReplace(
            new ReplaceOrderCommand("2", Petr, OrderId: 0, NewPriceMantissa: Px(11m), NewQuantity: 100, EnteredAtNanos: 2_000UL),
            reply, clOrdIdValue: 2UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);

        // Engine emits OrderCanceled (lost priority) + OrderAccepted.
        Assert.Single(reply.Cancels);
        Assert.Equal(origOid, reply.Cancels[0].OrderId);
        Assert.Equal(2, reply.News.Count);   // initial accept + replacement accept
        Assert.Empty(reply.Rejects);

        // Old ClOrdId evicted; new ClOrdId now resolves the new orderId.
        long newOid = reply.News[1].OrderId;
        Assert.NotEqual(origOid, newOid);

        // Subsequent cancel by the OLD ClOrdId must fail (evicted).
        disp.EnqueueCancel(new CancelOrderCommand("3", Petr, OrderId: 0, 3_000UL),
            reply, clOrdIdValue: 3UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);
        Assert.Single(reply.Rejects);
        Assert.Equal(RejectReason.UnknownOrderId, reply.Rejects[^1].Reason);

        // Cancel by NEW ClOrdId resolves to newOid.
        disp.EnqueueCancel(new CancelOrderCommand("4", Petr, OrderId: 0, 4_000UL),
            reply, clOrdIdValue: 4UL, origClOrdIdValue: 2UL);
        DrainInbound(disp);
        Assert.Equal(2, reply.Cancels.Count);
        Assert.Equal(newOid, reply.Cancels[^1].OrderId);
    }

    [Fact]
    public void FullyFilledOrder_EvictsClOrdIdMap()
    {
        var (disp, pkt) = NewDispatcher();
        var maker = new RecordingReply();
        var taker = new RecordingReply();

        // Maker rests an order tagged with ClOrdId=10.
        disp.EnqueueNewOrder(new NewOrderCommand("10", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            maker, clOrdIdValue: 10UL);
        DrainInbound(disp);
        Assert.Single(maker.News);

        // Taker fully consumes the maker's order.
        disp.EnqueueNewOrder(new NewOrderCommand("20", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 8, 2_000UL),
            taker, clOrdIdValue: 20UL);
        DrainInbound(disp);

        Assert.Single(maker.Trades);

        // The maker's ClOrdId must be evicted from the (firm, ClOrdId) → orderId map:
        // a Cancel by OrigClOrdID=10 must now reject with UnknownOrderId.
        disp.EnqueueCancel(new CancelOrderCommand("11", Petr, OrderId: 0, 3_000UL),
            maker, clOrdIdValue: 11UL, origClOrdIdValue: 10UL);
        DrainInbound(disp);

        var rej = Assert.Single(maker.Rejects);
        Assert.Equal(RejectReason.UnknownOrderId, rej.Reason);
    }

    [Fact]
    public void Cancel_OnSelf_ER_CarriesBothClOrdIdAndOrigClOrdId()
    {
        var (disp, _) = NewDispatcher();
        var reply = new RecordingReply { CaptureCancelIds = true };

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply, clOrdIdValue: 1UL);
        DrainInbound(disp);

        disp.EnqueueCancel(new CancelOrderCommand("99", Petr, OrderId: 0, 2_000UL),
            reply, clOrdIdValue: 99UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Single(reply.CancelIds);
        var (clOrd, origClOrd) = reply.CancelIds[0];
        Assert.Equal(99UL, clOrd);
        Assert.Equal(1UL, origClOrd);
    }

    private static void DrainInbound(ChannelDispatcher disp)
    {
        // Reflect into the bounded channel and process until empty without
        // starting the dispatcher's own loop.
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
