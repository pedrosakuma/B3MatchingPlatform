using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Exchange.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using RejectEvent = B3.Exchange.Matching.RejectEvent;
using Side = B3.Exchange.Matching.Side;
using TimeInForce = B3.Exchange.Matching.TimeInForce;
using OrderType = B3.Exchange.Matching.OrderType;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Integration tests for the dispatcher-level persistence cycle
/// (issue #260): Save invoked after each command flush, Restore rebuilds
/// engine + OrderRegistry + sequence counters.
/// </summary>
public class ChannelDispatcherPersistenceTests
{
    private const long Sec = 900_000_000_001L;

    private static Instrument Petr4 => new()
    {
        Symbol = "PETR4",
        SecurityId = Sec,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000m,
        Currency = "BRL",
        Isin = "BRPETRACNPR6",
        SecurityType = "EQUITY",
    };

    private static long Px(decimal p) => (long)(p * 10_000m);

    private sealed class NoOpPacketSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    private sealed class NoOpOutbound : ICoreOutbound
    {
        public bool WriteExecutionReportNew(SessionId s, uint f, ulong c, in OrderAcceptedEvent e, ulong r = ulong.MaxValue) => true;
        public bool WriteExecutionReportTrade(SessionId s, in TradeEvent e, bool a, long o, ulong c, long l, long u) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId s, ulong c, long o, in TradeEvent e, long l, long u) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId s, ulong c, long o, in OrderCanceledEvent e, ulong r, ulong rt = ulong.MaxValue) => true;
        public bool WriteExecutionReportModify(SessionId s, long sec, long o, ulong c, ulong oc, Side side, long np, long nq, ulong tt, uint rpt, ulong rt = ulong.MaxValue) => true;
        public bool WriteExecutionReportReject(SessionId s, in RejectEvent e, ulong c) => true;
    }

    /// <summary>
    /// In-memory persister capturing every Save and forwarding loads to
    /// the most recent snapshot stored. Mirrors
    /// <see cref="FileChannelStatePersister"/> semantics without touching
    /// the disk.
    /// </summary>
    private sealed class InMemoryPersister : IChannelStatePersister
    {
        public Dictionary<byte, ChannelStateSnapshot> Last { get; } = new();
        public int SaveCount { get; private set; }

        public ChannelStateSnapshot? TryLoad(byte channelNumber)
            => Last.TryGetValue(channelNumber, out var s) ? s : null;

        public void Save(ChannelStateSnapshot snapshot)
        {
            SaveCount++;
            Last[snapshot.ChannelNumber] = snapshot;
        }
    }

    private static ChannelDispatcher BuildDispatcher(
        IChannelStatePersister persister,
        out NoOpOutbound outbound)
    {
        outbound = new NoOpOutbound();
        var localOutbound = outbound;
        return new ChannelDispatcher(
            channelNumber: 84,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s,
                NullLogger<MatchingEngine>.Instance),
            packetSink: new NoOpPacketSink(),
            outbound: localOutbound,
            logger: NullLogger<ChannelDispatcher>.Instance,
            persister: persister);
    }

    [Fact]
    public void SaveInvokedAfterEveryCommandFlush_PersistsBookAndOwners()
    {
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("10101");

        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("CL-1", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, 1000UL),
            session, enteringFirm: 100, clOrdIdValue: 0xAAAA));
        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("CL-2", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 200, 100, 2000UL),
            session, enteringFirm: 100, clOrdIdValue: 0xBBBB));
        probe.DrainInbound();

        Assert.Equal(2, persister.SaveCount);
        var snap = persister.TryLoad(84);
        Assert.NotNull(snap);
        Assert.Equal(2, snap!.Engine.Books.Single().Orders.Count);
        Assert.Equal(2, snap.Owners.Count);
        Assert.All(snap.Owners, o => Assert.Equal("10101", o.SessionValue));
        Assert.Contains(snap.Owners, o => o.ClOrdId == 0xAAAA);
        Assert.Contains(snap.Owners, o => o.ClOrdId == 0xBBBB);
        Assert.True(snap.Engine.NextOrderId >= 3);
    }

    [Fact]
    public void RestoreChannelState_RebuildsBookOwnersAndCounters()
    {
        var persister = new InMemoryPersister();
        // Round 1: build state in dispatcher A.
        var dispA = BuildDispatcher(persister, out _);
        var probe = dispA.CreateTestProbe();
        var session = new SessionId("20201");
        Assert.True(dispA.EnqueueNewOrder(
            new NewOrderCommand("CL-X", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 300, 200, 1000UL),
            session, enteringFirm: 200, clOrdIdValue: 0xCCCC));
        probe.DrainInbound();

        // Round 2: brand-new dispatcher with same persister; explicitly
        // call RestoreChannelState to mimic what RunLoopAsync's
        // LoadPersistedStateOnLoopThread does on real boot.
        var dispB = BuildDispatcher(persister, out _);
        var loaded = persister.TryLoad(84);
        Assert.NotNull(loaded);
        dispB.RestoreChannelState(loaded!);

        // OrigClOrdID lookup resolves through the rebuilt OrderRegistry.
        Assert.True(dispB.TryResolveByClOrdId(firm: 200, origClOrdId: 0xCCCC,
            out var orderId, out var securityId));
        Assert.NotEqual(0, orderId);
        Assert.Equal(Sec, securityId);

        // Sequence counters survive round-trip.
        Assert.Equal(dispA.SequenceNumber, dispB.SequenceNumber);
        Assert.Equal(dispA.SequenceVersion, dispB.SequenceVersion);
        Assert.Equal(1, dispB.OrderRegistryCount);
    }

    [Fact]
    public void RestoreChannelState_RejectsMismatchedChannelNumber()
    {
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _);
        var snap = new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: 99,  // dispatcher is 84
            SequenceNumber: 0, SequenceVersion: 1,
            Engine: new EngineStateSnapshot(1, 1, 0,
                Array.Empty<EngineStateSnapshot.PhaseEntry>(),
                Array.Empty<EngineStateSnapshot.BookSnapshot>()),
            Owners: Array.Empty<OrderOwnerSnapshot>());

        Assert.Throws<InvalidOperationException>(() => disp.RestoreChannelState(snap));
    }

    [Fact]
    public void OperatorBumpVersion_TriggersPersistence()
    {
        // Review feedback on PR #261 (issue #260): visible
        // ChannelReset_11 must be durable — otherwise a crash after the
        // reset would resurrect the pre-reset book on next boot.
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("30303");
        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("CL-1", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, 1000UL),
            session, enteringFirm: 300, clOrdIdValue: 0xDDDD));
        probe.DrainInbound();
        var beforeBump = persister.SaveCount;

        Assert.True(disp.EnqueueOperatorBumpVersion());
        probe.DrainInbound();

        Assert.True(persister.SaveCount > beforeBump,
            "BumpVersion must trigger an additional Save");
        var snap = persister.TryLoad(84);
        Assert.NotNull(snap);
        // Engine has been reset → no resting orders, no owners.
        Assert.Empty(snap!.Engine.Books.Single().Orders);
        Assert.Empty(snap.Owners);
        // SequenceVersion bumped, SequenceNumber reset (then bumped to 1
        // by the ChannelReset_11 packet).
        Assert.True(snap.SequenceVersion >= 2);
    }

    [Fact]
    public void OperatorSetTradingPhase_TriggersPersistence()
    {
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _);
        var probe = disp.CreateTestProbe();
        var beforePhase = persister.SaveCount;

        Assert.True(disp.EnqueueOperatorSetTradingPhase(Sec,
            B3.Exchange.Matching.TradingPhase.Pause));
        probe.DrainInbound();

        Assert.True(persister.SaveCount > beforePhase,
            "SetTradingPhase must trigger a Save so the new phase survives restart");
        var snap = persister.TryLoad(84);
        Assert.NotNull(snap);
        Assert.Contains(snap!.Engine.Phases,
            p => p.SecurityId == Sec && p.Phase == B3.Exchange.Matching.TradingPhase.Pause);
    }

    [Fact]
    public void Capture_FiltersStopOrderOwners()
    {
        // Review feedback on PR #261: stop orders are registered in
        // _orders but are intentionally absent from the engine snapshot.
        // CaptureChannelState must filter them out so a restored
        // dispatcher does not have phantom OrigClOrdID → OrderId
        // mappings pointing at non-existent engine orders.
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("40404");

        // One regular Limit order (lands in book) plus one Stop order
        // (registers ownership but stays off-book).
        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("CL-LIMIT", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, 1000UL),
            session, enteringFirm: 400, clOrdIdValue: 0xE001));
        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("CL-STOP", Sec, Side.Buy, OrderType.StopLoss,
                TimeInForce.Day, Px(11.00m), 100, 100, 2000UL)
            { StopPxMantissa = Px(10.50m) },
            session, enteringFirm: 400, clOrdIdValue: 0xE002));
        probe.DrainInbound();

        var snap = persister.TryLoad(84);
        Assert.NotNull(snap);
        Assert.Single(snap!.Engine.Books.Single().Orders);
        // Only the limit order's owner is persisted; the stop owner is
        // dropped because no engine book contains its orderId.
        Assert.Single(snap.Owners);
        Assert.Equal(0xE001UL, snap.Owners.Single().ClOrdId);
    }

    [Fact]
    public void Restore_RejectsSnapshotWithOrphanedOwners()
    {
        // Review feedback on PR #261: structural validation runs before
        // any mutation so a bad snapshot cannot leave the engine
        // half-restored.
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _);
        var snap = new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: 84,
            SequenceNumber: 5, SequenceVersion: 1,
            Engine: new EngineStateSnapshot(2, 1, 0,
                Array.Empty<EngineStateSnapshot.PhaseEntry>(),
                Array.Empty<EngineStateSnapshot.BookSnapshot>()),
            Owners: new[]
            {
                new OrderOwnerSnapshot(
                    OrderId: 99, SessionValue: "x", Firm: 1,
                    ClOrdId: 0xAAAA, Side: Side.Buy, SecurityId: Sec),
            });

        var ex = Assert.Throws<InvalidOperationException>(() => disp.RestoreChannelState(snap));
        Assert.Contains("orderId 99", ex.Message);
        // Engine must be untouched — counters at defaults.
        Assert.Equal(0u, disp.SequenceNumber);
        Assert.Equal((ushort)1, disp.SequenceVersion);
        Assert.Equal(0, disp.OrderRegistryCount);
    }

    [Fact]
    public void Restore_RejectsSnapshotWithDuplicateOrderId()
    {
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _);
        var dupOrder = new RestingOrderRecord(
            OrderId: 1, ClOrdId: "x", Side: Side.Buy, PriceMantissa: Px(10.00m),
            RemainingQuantity: 100, EnteringFirm: 1, InsertTimestampNanos: 0,
            Tif: TimeInForce.Day, MaxFloor: 0, HiddenQuantity: 0);
        var snap = new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: 84,
            SequenceNumber: 0, SequenceVersion: 1,
            Engine: new EngineStateSnapshot(2, 1, 0,
                Array.Empty<EngineStateSnapshot.PhaseEntry>(),
                new[] { new EngineStateSnapshot.BookSnapshot(Sec, new[] { dupOrder, dupOrder }) }),
            Owners: Array.Empty<OrderOwnerSnapshot>());

        var ex = Assert.Throws<InvalidOperationException>(() => disp.RestoreChannelState(snap));
        Assert.Contains("duplicate orderId", ex.Message);
        Assert.Equal(0, disp.OrderRegistryCount);
    }
}
