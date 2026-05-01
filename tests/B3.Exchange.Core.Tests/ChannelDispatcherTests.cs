using System.Runtime.InteropServices;
using B3.Exchange.Gateway;
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

    private sealed class FakeSession
    {
        private static int _nextId;
        public B3.Exchange.Contracts.SessionId Id { get; } = new("s" + System.Threading.Interlocked.Increment(ref _nextId));
        public uint EnteringFirm { get; init; } = 7;
        public List<string> Calls { get; } = new();
        public List<OrderAcceptedEvent> News { get; } = new();
        public List<OrderCanceledEvent> Cancels { get; } = new();
        public List<RejectEvent> Rejects { get; } = new();
        public List<TradeEvent> Trades { get; } = new();
        public bool CaptureCancelIds { get; set; }
        public List<(ulong ClOrdId, ulong OrigClOrdId)> CancelIds { get; } = new();

        public FakeSession(RecordingOutbound outbound) { outbound.Register(this); }
    }

    private sealed class RecordingOutbound : ICoreOutbound
    {
        private readonly Dictionary<B3.Exchange.Contracts.SessionId, FakeSession> _sessions = new();
        public void Register(FakeSession s) => _sessions[s.Id] = s;
        private FakeSession? Find(B3.Exchange.Contracts.SessionId id)
            => _sessions.TryGetValue(id, out var s) ? s : null;

        public bool WriteExecutionReportNew(B3.Exchange.Contracts.SessionId session, ulong clOrdIdValue, in OrderAcceptedEvent e)
        { if (Find(session) is { } s) { s.News.Add(e); s.Calls.Add("New"); } return true; }
        public bool WriteExecutionReportTrade(B3.Exchange.Contracts.SessionId session, in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty)
        { if (Find(session) is { } s) { s.Trades.Add(e); s.Calls.Add(isAggressor ? "TradeAgg" : "TradePass"); } return true; }
        public bool WriteExecutionReportCancel(B3.Exchange.Contracts.SessionId session, in OrderCanceledEvent e, ulong clOrdIdValue, ulong origClOrdIdValue)
        { if (Find(session) is { } s) { s.Cancels.Add(e); s.Calls.Add("Cancel"); if (s.CaptureCancelIds) s.CancelIds.Add((clOrdIdValue, origClOrdIdValue)); } return true; }
        public bool WriteExecutionReportModify(B3.Exchange.Contracts.SessionId session, long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue, Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq)
        { if (Find(session) is { } s) { s.Calls.Add("Modify"); } return true; }
        public bool WriteExecutionReportReject(B3.Exchange.Contracts.SessionId session, in RejectEvent e, ulong clOrdIdValue)
        { if (Find(session) is { } s) { s.Rejects.Add(e); s.Calls.Add("Reject"); } return true; }
    }

    private static (ChannelDispatcher disp, RecordingPacketSink pkt, RecordingOutbound outbound) NewDispatcher()
    {
        var pkt = new RecordingPacketSink();
        var outbound = new RecordingOutbound();
        var disp = new ChannelDispatcher(channelNumber: 1,
            engineFactory: sink => new MatchingEngine(new[] { Petr4 }, sink, NullLogger<MatchingEngine>.Instance),
            packetSink: pkt,
            outbound: outbound,
            logger: NullLogger<ChannelDispatcher>.Instance,
            nowNanos: () => 1_000_000_000UL, tradeDate: 19_000);
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
    public void Cancel_ByOrigClOrdId_ResolvesViaSessionMap()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound) { CaptureCancelIds = true };

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        long oid = reply.News[0].OrderId;
        pkt.Packets.Clear();

        // Cancel without OrderID — only OrigClOrdID = 1.
        disp.EnqueueCancel(new CancelOrderCommand("99", Petr, OrderId: 0, 2_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 99UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Single(reply.Cancels);
        Assert.Equal(oid, reply.Cancels[0].OrderId);
        Assert.Empty(reply.Rejects);
        Assert.Single(pkt.Packets); // DeleteOrder UMDF frame
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

    [Fact]
    public void OnSessionClosed_ReleasesOrderOwnerEntries_LeavesOrdersOnBook()
    {
        var (disp, _, outbound) = NewDispatcher();
        var sessionA = new FakeSession(outbound);
        var sessionB = new FakeSession(outbound);

        // Two resting orders by sessionA, one by sessionB (different prices so
        // they don't cross).
        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL), sessionA.Id, sessionA.EnteringFirm, 1UL);
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.99m), 100, 7, 1_000UL), sessionA.Id, sessionA.EnteringFirm, 2UL);
        disp.EnqueueNewOrder(new NewOrderCommand("3", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(10.05m), 100, 8, 1_000UL), sessionB.Id, sessionB.EnteringFirm, 3UL);
        DrainInbound(disp);

        Assert.Equal(3, OrderOwnerCount(disp));

        // Session A drops. After processing OnSessionClosed on the dispatcher
        // thread, only B's owner entry must remain. Orders themselves stay on
        // the book (the engine never sees a Cancel).
        ((IInboundCommandSink)disp).OnSessionClosed(sessionA.Id);
        DrainInbound(disp);

        Assert.Equal(1, OrderOwnerCount(disp));
        // Cross sessionB's sell with a fresh aggressor: passive side has no
        // owner left for A's resting BUYs (correct), but a counter-cross from
        // sessionB itself exercises the still-live ownership for B.
        var sessionC = new FakeSession(outbound);
        disp.EnqueueNewOrder(new NewOrderCommand("4", Petr, Side.Sell, OrderType.Limit, TimeInForce.Day, Px(9.99m), 100, 9, 2_000UL), sessionC.Id, sessionC.EnteringFirm, 4UL);
        DrainInbound(disp);
        // Aggressor (C) sees its trade ER. Passive owner (A) is gone → no ER
        // delivered to A — verifies the back-reference was actually dropped.
        Assert.Single(sessionC.Trades);
        Assert.Empty(sessionA.Trades);
    }

    [Fact]
    public void OnSessionClosed_RemovesSessionIdOwnerEntries()
    {
        // With a value-only owner map (SessionId is a struct), there is no
        // session reference to GC. The behavioral guarantee is simpler:
        // OnSessionClosed must evict every entry whose owner SessionId equals
        // the closed session.
        var (disp, _, outbound) = NewDispatcher();
        var session = new FakeSession(outbound);
        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            session.Id, session.EnteringFirm, 1UL);
        disp.EnqueueNewOrder(new NewOrderCommand("2", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(9.99m), 100, 7, 1_001UL),
            session.Id, session.EnteringFirm, 2UL);
        DrainInbound(disp);

        Assert.Equal(2, OrderOwnerCount(disp));

        ((IInboundCommandSink)disp).OnSessionClosed(session.Id);
        DrainInbound(disp);

        Assert.Equal(0, OrderOwnerCount(disp));
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
    public void Replace_ByOrigClOrdId_LostPriority_ResolvesViaSessionMap()
    {
        var (disp, pkt, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound);

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);
        long origOid = reply.News[0].OrderId;
        pkt.Packets.Clear();

        // Replace with NEW PRICE (loses priority): only OrigClOrdID provided.
        disp.EnqueueReplace(
            new ReplaceOrderCommand("2", Petr, OrderId: 0, NewPriceMantissa: Px(11m), NewQuantity: 100, EnteredAtNanos: 2_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 2UL, origClOrdIdValue: 1UL);
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
            reply.Id, reply.EnteringFirm, clOrdIdValue: 3UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);
        Assert.Single(reply.Rejects);
        Assert.Equal(RejectReason.UnknownOrderId, reply.Rejects[^1].Reason);

        // Cancel by NEW ClOrdId resolves to newOid.
        disp.EnqueueCancel(new CancelOrderCommand("4", Petr, OrderId: 0, 4_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 4UL, origClOrdIdValue: 2UL);
        DrainInbound(disp);
        Assert.Equal(2, reply.Cancels.Count);
        Assert.Equal(newOid, reply.Cancels[^1].OrderId);
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
    public void Cancel_OnSelf_ER_CarriesBothClOrdIdAndOrigClOrdId()
    {
        var (disp, _, outbound) = NewDispatcher();
        var reply = new FakeSession(outbound) { CaptureCancelIds = true };

        disp.EnqueueNewOrder(new NewOrderCommand("1", Petr, Side.Buy, OrderType.Limit, TimeInForce.Day, Px(10m), 100, 7, 1_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 1UL);
        DrainInbound(disp);

        disp.EnqueueCancel(new CancelOrderCommand("99", Petr, OrderId: 0, 2_000UL),
            reply.Id, reply.EnteringFirm, clOrdIdValue: 99UL, origClOrdIdValue: 1UL);
        DrainInbound(disp);

        Assert.Single(reply.CancelIds);
        var (clOrd, origClOrd) = reply.CancelIds[0];
        Assert.Equal(99UL, clOrd);
        Assert.Equal(1UL, origClOrd);
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
