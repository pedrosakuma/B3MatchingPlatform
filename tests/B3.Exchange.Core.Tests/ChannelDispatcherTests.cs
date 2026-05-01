using System.Runtime.InteropServices;
using B3.Exchange.EntryPoint;
using B3.Exchange.Instruments;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using B3.Umdf.WireEncoder;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Core.Tests;

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

    private sealed class RecordingReply : IGatewayResponseChannel
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
            engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance),
            packetSink: pkt,
            logger: NullLogger<ChannelDispatcher>.Instance,
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
        var reply = new RecordingReply { CaptureCancelIds = true };

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
    public void SequenceNumber_NearOverflow_BumpsSequenceVersion_AndResets()
    {
        var (disp, pkt) = NewDispatcher();
        var reply = new RecordingReply();

        Assert.Equal((ushort)1, disp.SequenceVersion);
        disp.TestSetSequenceNumber(uint.MaxValue);
        Assert.Equal(uint.MaxValue, disp.SequenceNumber);

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply, clOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Single(pkt.Packets);
        Assert.Equal((ushort)2, disp.SequenceVersion);
        Assert.Equal((uint)1, disp.SequenceNumber);
        var packet = pkt.Packets[0];
        Assert.Equal((ushort)2, MemoryMarshal.Read<ushort>(packet.AsSpan(2, 2)));
        Assert.Equal((uint)1, MemoryMarshal.Read<uint>(packet.AsSpan(4, 4)));
    }

    [Fact]
    public void OnSessionClosed_ReleasesOrderOwnerEntries_LeavesOrdersOnBook()
    {
        var (disp, _) = NewDispatcher();
        var sessionA = new RecordingReply();
        var sessionB = new RecordingReply();

        // Two resting orders by sessionA, one by sessionB (different prices so
        // they don't cross).
        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL), sessionA, 1UL);
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.99m), 100, 7, 1_000UL), sessionA, 2UL);
        disp.EnqueueNewOrder(new NewOrderCommand("3", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.05m), 100, 8, 1_000UL), sessionB, 3UL);
        DrainInbound(disp);

        Assert.Equal(3, OrderOwnerCount(disp));

        // Session A drops. After processing OnSessionClosed on the dispatcher
        // thread, only B's owner entry must remain. Orders themselves stay on
        // the book (the engine never sees a Cancel).
        ((IInboundCommandSink)disp).OnSessionClosed(sessionA);
        DrainInbound(disp);

        Assert.Equal(1, OrderOwnerCount(disp));
        // Cross sessionB's sell with a fresh aggressor: passive side has no
        // owner left for A's resting BUYs (correct), but a counter-cross from
        // sessionB itself exercises the still-live ownership for B.
        var sessionC = new RecordingReply();
        disp.EnqueueNewOrder(new NewOrderCommand("4", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(9.99m), 100, 9, 2_000UL), sessionC, 4UL);
        DrainInbound(disp);
        // Aggressor (C) sees its trade ER. Passive owner (A) is gone → no ER
        // delivered to A — verifies the back-reference was actually dropped.
        Assert.Single(sessionC.Trades);
        Assert.Empty(sessionA.Trades);
    }

    [Fact]
    public void OnSessionClosed_AllowsSessionToBeGarbageCollected()
    {
        var (disp, _) = NewDispatcher();

        // Place an order from a session that we will let drop. A helper method
        // isolates the local rooted reference so the JIT cannot keep it
        // alive after the helper returns.
        var weak = PlaceAndForgetSession(disp);

        // At this point the dispatcher's _orderOwners map still references the
        // session, so it should NOT be collectable yet.
        ForceGc();
        Assert.True(weak.IsAlive, "session unexpectedly collected before OnSessionClosed");

        // Notify and drain. After NotifyClosedAndForget returns, the
        // temporary reference it pulled from WeakReference.Target lives in a
        // popped stack frame and cannot be kept rooted by the caller.
        NotifyClosedAndForget(disp, weak);

        ForceGc();
        Assert.False(weak.IsAlive, "session was not GC-eligible after OnSessionClosed; ownership map still roots it");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference PlaceAndForgetSession(ChannelDispatcher disp)
    {
        var session = new RecordingReply();
        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            session, 1UL);
        DrainInbound(disp);
        var weak = new WeakReference(session);
        // session goes out of scope when this method returns.
        return weak;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void NotifyClosedAndForget(ChannelDispatcher disp, WeakReference weak)
    {
        var target = (IGatewayResponseChannel?)weak.Target;
        if (target is null) return;
        ((IInboundCommandSink)disp).OnSessionClosed(target);
        target = null;
        DrainInbound(disp);
    }

    private static void ForceGc()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private static int OrderOwnerCount(ChannelDispatcher disp)
    {
        var f = typeof(ChannelDispatcher).GetField("_orderOwners",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var dict = (System.Collections.IDictionary)f.GetValue(disp)!;
        return dict.Count;
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

    [Fact]
    public void OperatorBumpVersion_ClearsBook_BumpsVersions_EmitsChannelResetPacket()
    {
        var (disp, pkt) = NewDispatcher();
        var reply = new RecordingReply();

        // Seed two resting orders so the book has state to clear.
        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply, clOrdIdValue: 1UL);
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(11m), 200, 7, 1_001UL),
            reply, clOrdIdValue: 2UL);
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
        var (disp, pkt) = NewDispatcher();
        Assert.True(disp.EnqueueOperatorSnapshotNow());
        DrainInbound(disp);
        Assert.Empty(pkt.Packets);
    }

    private static MatchingEngine GetEngine(ChannelDispatcher disp)
    {
        var f = typeof(ChannelDispatcher).GetField("_engine",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (MatchingEngine)f.GetValue(disp)!;
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
