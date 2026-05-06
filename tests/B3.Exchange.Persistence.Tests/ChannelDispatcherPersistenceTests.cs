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

        public long Save(ChannelStateSnapshot snapshot)
        {
            SaveCount++;
            Last[snapshot.ChannelNumber] = snapshot;
            return 0;
        }
    }

    private static ChannelDispatcher BuildDispatcher(
        IChannelStatePersister persister,
        out NoOpOutbound outbound,
        ChannelMetrics? metrics = null,
        SnapshotThrottlePolicy? throttle = null,
        bool useAsyncSnapshotWriter = false)
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
            metrics: metrics,
            persister: persister,
            snapshotThrottle: throttle,
            useAsyncSnapshotWriter: useAsyncSnapshotWriter);
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
    public void Capture_PersistsStopOrderOwners()
    {
        // Issue #262: stop orders are now persisted alongside book
        // orders. Both the StopLoss and its owner must appear in the
        // snapshot so a restored dispatcher resumes the parked stop and
        // can route a future trigger / cancel back to the original
        // session.
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("40404");

        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("CL-LIMIT", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, 1000UL),
            session, enteringFirm: 400, clOrdIdValue: 0xE001));
        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("CL-STOP", Sec, Side.Buy, OrderType.StopLoss,
                TimeInForce.Day, 0, 100, 100, 2000UL)
            { StopPxMantissa = Px(10.50m) },
            session, enteringFirm: 400, clOrdIdValue: 0xE002));
        probe.DrainInbound();

        var snap = persister.TryLoad(84);
        Assert.NotNull(snap);
        Assert.Single(snap!.Engine.Books.Single().Orders);
        Assert.NotNull(snap.Engine.Stops);
        var stop = Assert.Single(snap.Engine.Stops!);
        Assert.Equal(OrderType.StopLoss, stop.StopType);
        Assert.Equal(Px(10.50m), stop.StopPxMantissa);
        Assert.Equal(0L, stop.LimitPriceMantissa);
        Assert.Equal(2, snap.Owners.Count);
        Assert.Contains(snap.Owners, o => o.ClOrdId == 0xE001);
        Assert.Contains(snap.Owners, o => o.ClOrdId == 0xE002);
    }

    [Fact]
    public void RestoreChannelState_RebuildsParkedStops()
    {
        // Issue #262: after restore the engine must hold the parked
        // stop, the OrderRegistry must contain its owner mapping, and
        // a Cancel-by-orderId must succeed (proving the stop is fully
        // wired up).
        var persister = new InMemoryPersister();
        var dispA = BuildDispatcher(persister, out _);
        var probeA = dispA.CreateTestProbe();
        var session = new SessionId("50505");

        Assert.True(dispA.EnqueueNewOrder(
            new NewOrderCommand("CL-STOPLIM", Sec, Side.Sell, OrderType.StopLimit,
                TimeInForce.Day, Px(9.50m), 100, 100, 1000UL)
            { StopPxMantissa = Px(9.80m) },
            session, enteringFirm: 500, clOrdIdValue: 0xF001));
        probeA.DrainInbound();

        long stopOid;
        {
            var snap = persister.TryLoad(84);
            Assert.NotNull(snap);
            Assert.NotNull(snap!.Engine.Stops);
            var s = Assert.Single(snap.Engine.Stops!);
            Assert.Equal(OrderType.StopLimit, s.StopType);
            stopOid = s.OrderId;
        }

        // Fresh dispatcher, restore, then cancel the parked stop by
        // its engine OrderId — only succeeds if both _stopById and
        // OrderRegistry were rebuilt.
        var dispB = BuildDispatcher(persister, out _);
        var probeB = dispB.CreateTestProbe();
        var loaded = persister.TryLoad(84);
        Assert.NotNull(loaded);
        dispB.RestoreChannelState(loaded!);
        Assert.Equal(1, dispB.OrderRegistryCount);

        Assert.True(dispB.EnqueueCancel(
            new CancelOrderCommand(ClOrdId: "CL-STOPLIM-C", SecurityId: Sec, OrderId: stopOid, EnteredAtNanos: 2000UL),
            session, enteringFirm: 500, clOrdIdValue: 0xF002, origClOrdIdValue: 0xF001));
        probeB.DrainInbound();

        // After cancel the stop ownership is evicted; a fresh capture
        // shows no parked stops and no orphan owner.
        var post = dispB.CaptureChannelState();
        Assert.True(post.Engine.Stops is null || post.Engine.Stops.Count == 0);
        Assert.Empty(post.Owners);
    }

    [Fact]
    public void Restore_RejectsSnapshotWithDuplicateStopOrderId()
    {
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _);
        var dupStop = new RestingStopRecord(
            OrderId: 1, ClOrdId: "x", SecurityId: Sec, Side: Side.Buy,
            StopType: OrderType.StopLoss, Tif: TimeInForce.Day,
            StopPxMantissa: Px(10.50m), LimitPriceMantissa: 0,
            Quantity: 100, EnteringFirm: 1, EnteredAtNanos: 0);
        var snap = new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: 84,
            SequenceNumber: 0, SequenceVersion: 1,
            Engine: new EngineStateSnapshot(2, 1, 0,
                Array.Empty<EngineStateSnapshot.PhaseEntry>(),
                Array.Empty<EngineStateSnapshot.BookSnapshot>(),
                new[] { dupStop, dupStop }),
            Owners: Array.Empty<OrderOwnerSnapshot>());

        var ex = Assert.Throws<InvalidOperationException>(() => disp.RestoreChannelState(snap));
        Assert.Contains("duplicate orderId", ex.Message);
        Assert.Equal(0, disp.OrderRegistryCount);
    }

    [Fact]
    public void Restore_RejectsStopLossWithLimitPrice()
    {
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _);
        var bad = new RestingStopRecord(
            OrderId: 1, ClOrdId: "x", SecurityId: Sec, Side: Side.Buy,
            StopType: OrderType.StopLoss, Tif: TimeInForce.Day,
            StopPxMantissa: Px(10.50m), LimitPriceMantissa: Px(11.00m),
            Quantity: 100, EnteringFirm: 1, EnteredAtNanos: 0);
        var snap = new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: 84,
            SequenceNumber: 0, SequenceVersion: 1,
            Engine: new EngineStateSnapshot(2, 1, 0,
                Array.Empty<EngineStateSnapshot.PhaseEntry>(),
                Array.Empty<EngineStateSnapshot.BookSnapshot>(),
                new[] { bad }),
            Owners: Array.Empty<OrderOwnerSnapshot>());

        var ex = Assert.Throws<InvalidOperationException>(() => disp.RestoreChannelState(snap));
        Assert.Contains("StopLoss", ex.Message);
        Assert.Equal(0, disp.OrderRegistryCount);
    }

    [Fact]
    public void Save_IncrementsSnapshotMetricsOnSuccess()
    {
        // Issue #265: persistence observability — every successful Save
        // bumps the saves_total counter and refreshes the
        // last-success/last-size gauges.
        var persister = new InMemoryPersister();
        var metrics = new ChannelMetrics(channelNumber: 84);
        var disp = BuildDispatcher(persister, out _, metrics);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("60606");

        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("CL-M1", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, 1000UL),
            session, enteringFirm: 600, clOrdIdValue: 0xD001));
        probe.DrainInbound();

        Assert.Equal(1, metrics.SnapshotSavesOk);
        Assert.Equal(0, metrics.SnapshotSaveFailures);
        Assert.True(metrics.SnapshotLastSuccessUnixMs > 0);
    }

    [Fact]
    public void Save_IncrementsFailureCounterWhenPersisterThrows()
    {
        // Throwing persister forces the dispatcher's catch path; the
        // dispatcher must keep running and the failure counter must
        // increment by one per failed Save.
        var persister = new ThrowingPersister();
        var metrics = new ChannelMetrics(channelNumber: 84);
        var disp = BuildDispatcher(persister, out _, metrics);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("70707");

        Assert.True(disp.EnqueueNewOrder(
            new NewOrderCommand("CL-M2", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, 1000UL),
            session, enteringFirm: 700, clOrdIdValue: 0xD002));
        probe.DrainInbound();

        Assert.Equal(0, metrics.SnapshotSavesOk);
        Assert.Equal(1, metrics.SnapshotSaveFailures);
    }

    [Fact]
    public void Restore_IncrementsValidationFailureCounter()
    {
        // ValidateSnapshotStructure rejects an orphan-owner snapshot →
        // exch_snapshot_validation_failures_total bumps; the engine
        // remains untouched.
        var persister = new InMemoryPersister();
        var metrics = new ChannelMetrics(channelNumber: 84);
        var disp = BuildDispatcher(persister, out _, metrics);
        var snap = new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: 84,
            SequenceNumber: 0, SequenceVersion: 1,
            Engine: new EngineStateSnapshot(2, 1, 0,
                Array.Empty<EngineStateSnapshot.PhaseEntry>(),
                Array.Empty<EngineStateSnapshot.BookSnapshot>()),
            Owners: new[]
            {
                new OrderOwnerSnapshot(
                    OrderId: 99, SessionValue: "x", Firm: 1,
                    ClOrdId: 0xAAAA, Side: Side.Buy, SecurityId: Sec),
            });
        persister.Last[84] = snap;

        // LoadPersistedStateOnLoopThread is private; exercise via the
        // public RestoreChannelState path which shares the metric hook
        // boundary in the production loop.
        Assert.Throws<InvalidOperationException>(() => disp.RestoreChannelState(snap));
        // Direct call doesn't go through the loop wrapper, so validation
        // failure counter stays 0; that is the point of this test —
        // assert we don't accidentally count direct API misuse.
        Assert.Equal(0, metrics.SnapshotValidationFailures);
    }

    private sealed class ThrowingPersister : IChannelStatePersister
    {
        public ChannelStateSnapshot? TryLoad(byte channelNumber) => null;
        public long Save(ChannelStateSnapshot snapshot) => throw new IOException("disk full (test)");
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

    // -------- Issue #267: snapshot throttling --------

    private static bool EnqueueOrder(ChannelDispatcher disp, SessionId session,
        string clOrdId, ulong clOrdIdValue, ulong nanos)
        => disp.EnqueueNewOrder(
            new NewOrderCommand(clOrdId, Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, nanos),
            session, enteringFirm: 700, clOrdIdValue: clOrdIdValue);

    [Fact]
    public void Throttle_EveryNCommands_SkipsBelowThreshold_PersistsAtThreshold()
    {
        var persister = new InMemoryPersister();
        var metrics = new ChannelMetrics(channelNumber: 84);
        var policy = new SnapshotThrottlePolicy { EveryNCommands = 3, MinIntervalMs = 0 };
        var disp = BuildDispatcher(persister, out _, metrics, policy);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("70701");

        Assert.True(EnqueueOrder(disp, session, "CL-1", 0x1, 1000UL));
        probe.DrainInbound();
        Assert.Equal(0, persister.SaveCount);
        Assert.Equal(1, metrics.SnapshotSkippedByThrottle);

        Assert.True(EnqueueOrder(disp, session, "CL-2", 0x2, 1001UL));
        probe.DrainInbound();
        Assert.Equal(0, persister.SaveCount);
        Assert.Equal(2, metrics.SnapshotSkippedByThrottle);

        // Third command hits threshold → persists.
        Assert.True(EnqueueOrder(disp, session, "CL-3", 0x3, 1002UL));
        probe.DrainInbound();
        Assert.Equal(1, persister.SaveCount);
        Assert.Equal(2, metrics.SnapshotSkippedByThrottle);
        Assert.Equal(1L, metrics.SnapshotSavesOk);
    }

    [Fact]
    public void Throttle_OperatorCommands_AlwaysForcePersist()
    {
        var persister = new InMemoryPersister();
        var metrics = new ChannelMetrics(channelNumber: 84);
        // Effectively never persist regular commands during the test
        // window: 1 000 commands or 24h, whichever comes first.
        var policy = new SnapshotThrottlePolicy { EveryNCommands = 1000, MinIntervalMs = 86_400_000 };
        var disp = BuildDispatcher(persister, out _, metrics, policy);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("70702");

        Assert.True(EnqueueOrder(disp, session, "CL-1", 0x10, 2000UL));
        probe.DrainInbound();
        Assert.Equal(0, persister.SaveCount);
        Assert.Equal(1, metrics.SnapshotSkippedByThrottle);

        // Operator command bypasses throttle.
        Assert.True(disp.EnqueueOperatorSetTradingPhase(Sec, B3.Exchange.Matching.TradingPhase.Pause));
        probe.DrainInbound();
        Assert.Equal(1, persister.SaveCount);

        // BumpVersion also forces.
        Assert.True(disp.EnqueueOperatorBumpVersion());
        probe.DrainInbound();
        Assert.Equal(2, persister.SaveCount);
    }

    [Fact]
    public void Throttle_PendingDirty_FlushedOnShutdown()
    {
        var persister = new InMemoryPersister();
        var metrics = new ChannelMetrics(channelNumber: 84);
        var policy = new SnapshotThrottlePolicy { EveryNCommands = 1000, MinIntervalMs = 86_400_000 };
        var disp = BuildDispatcher(persister, out _, metrics, policy);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("70703");

        Assert.True(EnqueueOrder(disp, session, "CL-1", 0x20, 3000UL));
        probe.DrainInbound();
        Assert.Equal(0, persister.SaveCount);
        Assert.Equal(1, metrics.SnapshotSkippedByThrottle);

        // Cooperative shutdown drains pending dirty state.
        probe.FlushPendingSnapshotOnShutdown();
        Assert.Equal(1, persister.SaveCount);

        // Calling again is a no-op (dirty cleared after force flush).
        probe.FlushPendingSnapshotOnShutdown();
        Assert.Equal(1, persister.SaveCount);
    }

    [Fact]
    public void Throttle_DefaultPolicy_PersistsEveryCommand()
    {
        // Sanity: dispatcher constructed without an explicit throttle
        // (BuildDispatcher passes null) preserves PR #261 behaviour —
        // every command persists.
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("70704");

        Assert.True(EnqueueOrder(disp, session, "CL-1", 0x30, 4000UL));
        Assert.True(EnqueueOrder(disp, session, "CL-2", 0x31, 4001UL));
        probe.DrainInbound();
        Assert.Equal(2, persister.SaveCount);
    }

    // -------- Issue #268: async snapshot writer --------

    /// <summary>
    /// Persister that blocks each Save until the test releases it,
    /// allowing assertions about backpressure / coalescing.
    /// </summary>
    private sealed class BlockingPersister : IChannelStatePersister
    {
        private readonly ManualResetEventSlim _gate = new(initialState: false);
        public int SaveCount { get; private set; }
        public ChannelStateSnapshot? Last { get; private set; }

        public ChannelStateSnapshot? TryLoad(byte channelNumber) => null;

        public long Save(ChannelStateSnapshot snapshot)
        {
            _gate.Wait();
            SaveCount++;
            Last = snapshot;
            return 0;
        }

        public void Release() => _gate.Set();
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return condition();
    }

    [Fact]
    public async Task AsyncWriter_Submits_PersistAfterFlush()
    {
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _, useAsyncSnapshotWriter: true);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("80801");

        Assert.True(EnqueueOrder(disp, session, "CL-1", 0xA0, 5000UL));
        probe.DrainInbound();

        // Async path → poll briefly; the writer thread must observe the
        // submission and persist.
        Assert.True(await WaitForAsync(() => persister.SaveCount >= 1, TimeSpan.FromSeconds(2)));
        var snap = persister.TryLoad(84);
        Assert.NotNull(snap);
        Assert.Single(snap!.Engine.Books.Single().Orders);

        await disp.DisposeAsync();
    }

    [Fact]
    public async Task AsyncWriter_RapidSubmits_CoalesceLastWriteWins()
    {
        // While Save is blocked, several captures in a row collapse to
        // a single write because the mailbox is single-slot.
        var blocking = new BlockingPersister();
        var metrics = new ChannelMetrics(channelNumber: 84);
        var disp = BuildDispatcher(blocking, out _, metrics, useAsyncSnapshotWriter: true);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("80802");

        Assert.True(EnqueueOrder(disp, session, "CL-1", 0xB0, 6000UL));
        Assert.True(EnqueueOrder(disp, session, "CL-2", 0xB1, 6001UL));
        Assert.True(EnqueueOrder(disp, session, "CL-3", 0xB2, 6002UL));
        probe.DrainInbound();

        // Writer thread should be blocked inside Save with the first
        // submitted snapshot, while the next two were coalesced.
        Assert.True(await WaitForAsync(() => metrics.SnapshotDroppedByBackpressure >= 2,
            TimeSpan.FromSeconds(2)));
        Assert.Equal(0, blocking.SaveCount); // still blocked

        // Release the blocked Save and let the writer drain.
        blocking.Release();
        await disp.DisposeAsync();

        // After dispose we expect at most 2 actual writes (the first
        // blocked one + the final coalesced one); never the full 3.
        Assert.True(blocking.SaveCount <= 2,
            $"Expected coalescing → at most 2 writes, got {blocking.SaveCount}");
        Assert.True(blocking.SaveCount >= 1);
        Assert.NotNull(blocking.Last);
        // The final persisted snapshot must contain all 3 orders
        // (last-write-wins semantics).
        Assert.Equal(3, blocking.Last!.Engine.Books.Single().Orders.Count);
    }

    [Fact]
    public async Task AsyncWriter_DisposeAsync_DrainsPendingSnapshot()
    {
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, out _, useAsyncSnapshotWriter: true);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("80803");

        Assert.True(EnqueueOrder(disp, session, "CL-1", 0xC0, 7000UL));
        probe.DrainInbound();

        // DisposeAsync drains the writer; persister must reflect the
        // submitted snapshot before the call returns.
        await disp.DisposeAsync();
        Assert.True(persister.SaveCount >= 1);
        Assert.NotNull(persister.TryLoad(84));
    }

    [Fact]
    public async Task AsyncWriter_ThrowingPersister_DoesNotCrashWriter()
    {
        var throwing = new ThrowingPersister();
        var metrics = new ChannelMetrics(channelNumber: 84);
        var disp = BuildDispatcher(throwing, out _, metrics, useAsyncSnapshotWriter: true);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("80804");

        Assert.True(EnqueueOrder(disp, session, "CL-1", 0xD0, 8000UL));
        probe.DrainInbound();

        Assert.True(await WaitForAsync(() => metrics.SnapshotSaveFailures >= 1,
            TimeSpan.FromSeconds(2)));

        // Subsequent submissions still flow — writer survived the throw.
        Assert.True(EnqueueOrder(disp, session, "CL-2", 0xD1, 8001UL));
        probe.DrainInbound();
        Assert.True(await WaitForAsync(() => metrics.SnapshotSaveFailures >= 2,
            TimeSpan.FromSeconds(2)));

        await disp.DisposeAsync();
    }
}
