using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Exchange.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using OrderType = B3.Exchange.Matching.OrderType;
using Side = B3.Exchange.Matching.Side;
using TimeInForce = B3.Exchange.Matching.TimeInForce;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Tests for the per-channel Write-Ahead Log (issue #269): append +
/// read round-trip, truncate after snapshot, crash-recovery replay
/// (snapshot + WAL ⇒ engine ends up in the pre-crash state), and the
/// WAL-disabled fallback that preserves the pre-#269 behaviour.
/// </summary>
public class ChannelDispatcherWalTests
{
    private const long Sec = 900_000_000_002L;

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
        public int Published;
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Published++;
    }

    private sealed class CountingOutbound : ICoreOutbound
    {
        public int NewCount;
        public int TradeCount;
        public int PassiveTradeCount;
        public int PassiveCancelCount;
        public int ModifyCount;
        public int RejectCount;
        public int ReplyCount => NewCount + TradeCount + PassiveTradeCount
            + PassiveCancelCount + ModifyCount + RejectCount;

        public bool WriteExecutionReportNew(SessionId s, uint f, ulong c, in OrderAcceptedEvent e, ulong r = ulong.MaxValue, DurabilityHandle d = default)
        { NewCount++; return true; }
        public bool WriteExecutionReportTrade(SessionId s, in TradeEvent e, bool a, long o, ulong c, long l, long u, DurabilityHandle d = default)
        { TradeCount++; return true; }
        public bool WriteExecutionReportPassiveTrade(SessionId s, ulong c, long o, in TradeEvent e, long l, long u, DurabilityHandle d = default)
        { PassiveTradeCount++; return true; }
        public OrderedStreamWriteResult WriteExecutionReportPassiveCancel(SessionId s, ulong c, long o, in OrderCanceledEvent e, ulong r, ulong rt = ulong.MaxValue, DurabilityHandle d = default)
        { PassiveCancelCount++; return OrderedStreamWriteResult.CommittedAndEnqueued; }
        public bool WriteExecutionReportModify(SessionId s, long sec, long o, ulong c, ulong oc, Side side, long np, long nq, ulong tt, uint rpt, ulong rt = ulong.MaxValue, DurabilityHandle d = default, InvestorId? iv = null)
        { ModifyCount++; return true; }
        public bool WriteExecutionReportReject(SessionId s, in B3.Exchange.Matching.RejectEvent e, ulong c, DurabilityHandle d = default)
        { RejectCount++; return true; }
    }

    private sealed class InMemoryPersister : IChannelStatePersister
    {
        private int _saveCount;

        public Dictionary<byte, ChannelStateSnapshot> Last { get; } = new();
        public int SaveCount => Volatile.Read(ref _saveCount);
        public ChannelStateSnapshot? TryLoad(byte n)
        {
            lock (Last)
            {
                return Last.TryGetValue(n, out var s) ? s : null;
            }

        }
        public long Save(ChannelStateSnapshot s)
        {
            Interlocked.Increment(ref _saveCount);
            lock (Last)
            {
                Last[s.ChannelNumber] = s;
            }
            return 1;
        }
    }

    private sealed class TogglePersister : IChannelStatePersister
    {
        public bool FailSaves { get; set; }
        public ChannelStateSnapshot? Last { get; private set; }

        public ChannelStateSnapshot? TryLoad(byte channelNumber) => Last;

        public long Save(ChannelStateSnapshot snapshot)
        {
            if (FailSaves)
                throw new IOException("simulated snapshot failure");
            Last = snapshot;
            return 1;
        }
    }

    private sealed class RecordingWal : IChannelWriteAheadLog
    {
        private readonly List<WalRecord> _records = new();

        public int AppendCount { get; private set; }

        public int Append(WalRecord record)
        {
            AppendCount++;
            _records.Add(record);
            return 1;
        }

        public IReadOnlyList<WalRecord> ReadAll() => _records.ToArray();
        public void Truncate() => _records.Clear();
        public void TruncateThrough(long throughSeq)
            => _records.RemoveAll(record => record.Seq <= throughSeq);
        public long PendingDurableSeqOrZero => _records.Count == 0 ? 0 : _records[^1].Seq;
        public void WaitForDurable(long seq, CancellationToken cancellationToken = default) { }
    }

    private sealed class BlockingWal : IChannelWriteAheadLog, IDisposable
    {
        private readonly List<WalRecord> _records = new();
        private readonly ManualResetEventSlim _release = new(false);

        public TaskCompletionSource<bool> WaitEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Append(WalRecord record)
        {
            lock (_records) _records.Add(record);
            return 1;
        }

        public IReadOnlyList<WalRecord> ReadAll()
        {
            lock (_records) return _records.ToArray();
        }

        public void Truncate()
        {
            lock (_records) _records.Clear();
        }

        public void TruncateThrough(long throughSeq)
        {
            lock (_records)
                _records.RemoveAll(record => record.Seq <= throughSeq);
        }

        public long PendingDurableSeqOrZero
        {
            get
            {
                lock (_records)
                    return _records.Count == 0 ? 0 : _records[^1].Seq;
            }
        }

        public void WaitForDurable(
            long seq, CancellationToken cancellationToken = default)
        {
            WaitEntered.TrySetResult(true);
            _release.Wait(cancellationToken);
        }

        public void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "wal-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private static ChannelDispatcher BuildDispatcher(
        IChannelStatePersister? persister,
        IChannelWriteAheadLog? wal,
        out NoOpPacketSink sink,
        out CountingOutbound outbound,
        ChannelMetrics? metrics = null,
        SnapshotThrottlePolicy? throttle = null,
        bool useAsyncSnapshotWriter = false,
        B3.Exchange.PostTrade.IPostTradeSink? postTradeSink = null)
    {
        var localSink = sink = new NoOpPacketSink();
        var localOutbound = outbound = new CountingOutbound();
        return new ChannelDispatcher(
            channelNumber: 84,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s,
                NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = localSink,
                Outbound = localOutbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                Metrics = metrics,
                Persister = persister,
                SnapshotThrottle = throttle,
                UseAsyncSnapshotWriter = useAsyncSnapshotWriter,
                Wal = wal,
                PostTradeSink = postTradeSink,
            });
    }

    private static bool EnqueueOrder(ChannelDispatcher disp, SessionId session,
        string clOrdId, ulong clOrdIdValue, ulong nanos)
        => disp.EnqueueNewOrder(
            new NewOrderCommand(clOrdId, Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, nanos),
            session, enteringFirm: 700, clOrdIdValue: clOrdIdValue);

    private static ChannelStateSnapshot MultiPacketCrossingSnapshot()
    {
        const int restingOrderCount = 32;
        var orders = Enumerable.Range(1, restingOrderCount)
            .Select(i => new RestingOrderRecord(
                OrderId: i,
                ClOrdId: $"REST-{i}",
                Side: Side.Sell,
                PriceMantissa: Px(10.00m),
                RemainingQuantity: 100,
                EnteringFirm: (uint)(800 + i),
                InsertTimestampNanos: (ulong)i,
                Tif: TimeInForce.Day,
                MaxFloor: 0,
                HiddenQuantity: 0))
            .ToArray();
        var owners = Enumerable.Range(1, restingOrderCount)
            .Select(i => new OrderOwnerSnapshot(
                OrderId: i,
                SessionValue: $"8{i:0000}",
                Firm: (uint)(800 + i),
                ClOrdId: (ulong)i,
                Side: Side.Sell,
                SecurityId: Sec)
            {
                OriginalQty = 100,
            })
            .ToArray();
        return new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: 84,
            SequenceNumber: 0,
            SequenceVersion: 1,
            Engine: new EngineStateSnapshot(
                NextOrderId: restingOrderCount + 1,
                NextTradeId: 1,
                RptSeq: 0,
                Phases: [new EngineStateSnapshot.PhaseEntry(Sec, TradingPhase.Open)],
                Books: [new EngineStateSnapshot.BookSnapshot(Sec, orders)],
                Stops: []),
            Owners: owners);
    }

    // Readiness waits below poll a background dispatcher thread that must
    // be scheduled + finish WAL replay/snapshot work. The operations are
    // bounded and synchronous once the LongRunning loop thread runs, but
    // under the Release CI lane (whole solution's tests in parallel +
    // coverage instrumentation) CPU oversubscription can delay thread
    // scheduling well past a few seconds — a 5s budget flaked here
    // (issue #543). 30s is pure headroom: the happy path still returns in
    // milliseconds, we only wait longer when the box is saturated.
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

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
    public void FileWal_Append_ReadAll_RoundTrip()
    {
        using var dir = new TempDir();
        using (var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            var rec = new WalRecord(
                Seq: 1, Kind: WalRecordKind.NewOrder,
                SessionValue: "S1", Firm: 100, ClOrdId: 0xAA, OrigClOrdId: 0,
                NewOrder: new NewOrderCommand("CL-1", Sec, Side.Buy, OrderType.Limit,
                    TimeInForce.Day, Px(10m), 100, 100, 1234UL),
                Cancel: null, Replace: null);
            wal.Append(rec);
        }

        using (var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            var all = wal.ReadAll();
            Assert.Single(all);
            Assert.Equal(1L, all[0].Seq);
            Assert.Equal(WalRecordKind.NewOrder, all[0].Kind);
            Assert.NotNull(all[0].NewOrder);
            Assert.Equal("CL-1", all[0].NewOrder!.ClOrdId);
        }
    }

    [Fact]
    public async Task MassCancel_WalReplay_DoesNotResurrectOrdersAfterAcceptedCompletion()
    {
        using var dir = new TempDir();
        var session = new SessionId("S1");

        using (var wal = new FileChannelWriteAheadLog(
            dir.Path, channelNumber: 84,
            NullLogger<FileChannelWriteAheadLog>.Instance,
            fsyncPerWrite: true))
        {
            var live = BuildDispatcher(
                persister: null, wal, out _, out _);
            var probe = live.CreateTestProbe();

            Assert.True(EnqueueOrder(live, session, "CL-1", 1, nanos: 1));
            probe.DrainInbound();
            Assert.True(live.TryResolveByClOrdId(700, 1, out var orderId, out _));

            MassCancelOutcome? terminal = null;
            Assert.True(live.EnqueueResolvedMassCancel(
                [orderId], session, enteringFirm: 700,
                new MassCancelCommand(Sec, null, EnteredAtNanos: 2),
                outcome => terminal = outcome));
            probe.DrainInbound();

            Assert.True(terminal?.Succeeded);
            Assert.False(live.TryResolveByClOrdId(700, 1, out _, out _));
            Assert.Equal(
                [WalRecordKind.NewOrder, WalRecordKind.MassCancel],
                wal.ReadAll().Select(record => record.Kind));

            await live.DisposeAsync();
        }

        using var replayWal = new FileChannelWriteAheadLog(
            dir.Path, channelNumber: 84,
            NullLogger<FileChannelWriteAheadLog>.Instance,
            fsyncPerWrite: true);
        var recovered = BuildDispatcher(
            persister: null, replayWal, out _, out _);
        try
        {
            recovered.Start();
            Assert.False(recovered.TryResolveByClOrdId(700, 1, out _, out _));
        }
        finally
        {
            await recovered.DisposeAsync();
        }
    }

    [Fact]
    public async Task MassCancel_TerminalCompletionWaitsForWalDurability()
    {
        using var wal = new BlockingWal();
        var session = new SessionId("S1");
        var disp = BuildDispatcher(
            persister: null, wal, out _, out _);
        try
        {
            disp.Start();
            Assert.True(EnqueueOrder(disp, session, "CL-1", 1, nanos: 1));
            Assert.True(await WaitForAsync(
                () => disp.TryResolveByClOrdId(700, 1, out _, out _),
                ReadyTimeout));
            Assert.True(disp.TryResolveByClOrdId(
                700, 1, out var orderId, out _));

            var completion = new TaskCompletionSource<MassCancelOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(disp.EnqueueResolvedMassCancel(
                [orderId], session, enteringFirm: 700,
                new MassCancelCommand(Sec, null, EnteredAtNanos: 2),
                outcome => completion.TrySetResult(outcome)));

            await wal.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(completion.Task.IsCompleted);
            Assert.False(disp.TryResolveByClOrdId(700, 1, out _, out _));

            wal.Release();
            var terminal = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(terminal.Succeeded);
        }
        finally
        {
            wal.Release();
            await disp.DisposeAsync();
        }
    }

    [Fact]
    public void MassCancel_SnapshotFailure_CompletesSystemBusyWithoutAcceptedOutcome()
    {
        var persister = new TogglePersister();
        var session = new SessionId("S1");
        var disp = BuildDispatcher(
            persister, wal: null, out _, out _);
        var probe = disp.CreateTestProbe();

        Assert.True(EnqueueOrder(disp, session, "CL-1", 1, nanos: 1));
        probe.DrainInbound();
        Assert.True(disp.TryResolveByClOrdId(700, 1, out var orderId, out _));

        persister.FailSaves = true;
        MassCancelOutcome? terminal = null;
        Assert.True(disp.EnqueueResolvedMassCancel(
            [orderId], session, enteringFirm: 700,
            new MassCancelCommand(Sec, null, EnteredAtNanos: 2),
            outcome => terminal = outcome));
        probe.DrainInbound();

        Assert.False(terminal?.Succeeded);
        Assert.False(disp.TryResolveByClOrdId(700, 1, out _, out _));
    }

    [Fact]
    public void FileWal_Truncate_DropsExistingRecords()
    {
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        wal.Append(new WalRecord(1, WalRecordKind.NewOrder, "S", 1, 1, 0,
            new NewOrderCommand("CL-1", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10m), 100, 100, 1UL), null, null));
        wal.Append(new WalRecord(2, WalRecordKind.NewOrder, "S", 1, 2, 0,
            new NewOrderCommand("CL-2", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10m), 100, 100, 2UL), null, null));
        Assert.Equal(2, wal.ReadAll().Count);
        wal.Truncate();
        Assert.Empty(wal.ReadAll());
    }

    [Fact]
    public void Dispatcher_Append_TruncatesAfterSnapshotPersist()
    {
        using var dir = new TempDir();
        var persister = new InMemoryPersister();
        var wal = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var metrics = new ChannelMetrics(84);
        var disp = BuildDispatcher(persister, wal, out _, out _, metrics);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("10101");

        Assert.True(EnqueueOrder(disp, session, "CL-1", 0x1, 1UL));
        probe.DrainInbound();

        Assert.Equal(1, persister.SaveCount);
        Assert.True(metrics.WalAppends >= 1);
        Assert.True(metrics.WalTruncations >= 1);
        // After a successful snapshot, the WAL on disk is empty.
        Assert.Empty(wal.ReadAll());
        wal.Dispose();
    }

    [Fact]
    public async Task PenultimateEpoch_MultiPacketCommandCompletesInTerminalEpoch()
    {
        var dispatcher = BuildDispatcher(
            persister: null, wal: null, out var sink, out var outbound);
        dispatcher.RestoreChannelState(MultiPacketCrossingSnapshot());
        var probe = dispatcher.CreateTestProbe();
        probe.SetSequence(ushort.MaxValue - 1, uint.MaxValue - 1);
        Assert.True(dispatcher.EnqueueNewOrder(
            new NewOrderCommand(
                "FINAL-CROSS", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 3_200, 700, 900UL),
            new SessionId("57000"), enteringFirm: 700, clOrdIdValue: 0x570));

        probe.DrainInbound();

        Assert.True(sink.Published > 1);
        Assert.Equal(ushort.MaxValue, dispatcher.SequenceVersion);
        Assert.InRange(dispatcher.SequenceNumber, 1u, uint.MaxValue - 1);
        Assert.True(outbound.ReplyCount > 0);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task TerminalEpochBoundary_MultiPacketCommandFailsClosedBeforeAnySideEffects()
    {
        var initial = MultiPacketCrossingSnapshot();
        var persister = new InMemoryPersister();
        var wal = new RecordingWal();
        var sink = new NoOpPacketSink();
        var outbound = new CountingOutbound();
        MatchingEngine? engine = null;
        var dispatcher = new ChannelDispatcher(
            channelNumber: 84,
            engineFactory: eventSink =>
            {
                engine = new MatchingEngine([Petr4], eventSink,
                    NullLogger<MatchingEngine>.Instance);
                return engine;
            },
            options: new ChannelDispatcherOptions
            {
                PacketSink = sink,
                Outbound = outbound,
                Logger = NullLogger<ChannelDispatcher>.Instance,
                Persister = persister,
                Wal = wal,
            });

        var probe = dispatcher.CreateTestProbe();
        dispatcher.RestoreChannelState(initial);
        probe.SetSequence(ushort.MaxValue, uint.MaxValue - 1);
        Assert.True(dispatcher.EnqueueNewOrder(
            new NewOrderCommand(
                "EXHAUSTED-CROSS", Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 3_200, 700, 1_000UL),
            new SessionId("57001"), enteringFirm: 700, clOrdIdValue: 0x571));
        Assert.True(EnqueueOrder(
            dispatcher, new SessionId("57002"), "QUEUED-AFTER", 0x572, 2_000UL));
        Assert.True(dispatcher.EnqueueOperatorPersistSnapshot());

        var error = Assert.Throws<InvalidOperationException>(() => probe.DrainInbound());

        Assert.Contains("terminal wire epoch", error.Message);
        Assert.Equal(ushort.MaxValue, dispatcher.SequenceVersion);
        Assert.Equal(uint.MaxValue - 1, dispatcher.SequenceNumber);
        Assert.Equal(33L, engine!.PeekNextOrderId);
        Assert.Equal(0u, engine.CurrentRptSeq);
        Assert.Equal(32, engine.OrderCount(Sec));
        Assert.Equal(32, dispatcher.OrderRegistryCount);
        Assert.Equal(0, wal.AppendCount);
        Assert.Empty(wal.ReadAll());
        Assert.Equal(0, persister.SaveCount);
        Assert.Equal(0, outbound.ReplyCount);
        Assert.Equal(0, sink.Published);
        Assert.False(EnqueueOrder(
            dispatcher, new SessionId("57003"), "AFTER-FATAL", 0x573, 3_000UL));

        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task TerminalEpochBoundary_AllowsNormalShutdown()
    {
        var dispatcher = BuildDispatcher(
            persister: null, wal: null, out var sink, out var outbound);
        var probe = dispatcher.CreateTestProbe();
        probe.SetSequence(ushort.MaxValue, uint.MaxValue - 1);

        dispatcher.PrepareStartup();
        dispatcher.Activate();

        await dispatcher.DisposeAsync().AsTask().WaitAsync(ReadyTimeout);

        Assert.True(probe.LoopCompletion.IsCompletedSuccessfully);
        Assert.Equal(0, sink.Published);
        Assert.Equal(0, outbound.ReplyCount);
    }

    [Fact]
    public async Task CrashRecovery_SnapshotPlusWal_ReplaysToCrashTimeState()
    {
        using var dir = new TempDir();
        var persister = new InMemoryPersister();

        // Round 1: process two orders, the first persists snapshot, the
        // second is throttled (so its snapshot never lands) — but the
        // WAL captured it. Simulate "crash" by NOT calling
        // FlushPendingSnapshotOnShutdown.
        var wal1 = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var throttle = new SnapshotThrottlePolicy { EveryNCommands = 1, MinIntervalMs = 0 };
        // Use everyN=2 so the 2nd command is the one that would persist;
        // we'll only run 2 commands total but simulate crash before the
        // 2nd snapshot lands by using a persister that fails the second
        // Save.
        throttle = new SnapshotThrottlePolicy { EveryNCommands = 2, MinIntervalMs = 0 };
        var dispA = BuildDispatcher(persister, wal1, out _, out _, throttle: throttle);
        var probeA = dispA.CreateTestProbe();
        var session = new SessionId("99999");

        Assert.True(EnqueueOrder(dispA, session, "CL-A", 0xA, 100UL));
        probeA.DrainInbound();
        // 1st flush: throttle skips snapshot (everyN=2). WAL retains.
        Assert.Equal(0, persister.SaveCount);

        Assert.True(EnqueueOrder(dispA, session, "CL-B", 0xB, 101UL));
        probeA.DrainInbound();
        // 2nd flush: throttle fires → snapshot persisted, WAL truncated.
        Assert.Equal(1, persister.SaveCount);

        // Now process a 3rd command — throttled again, so the snapshot
        // does NOT include it. Simulate crash by disposing the WAL
        // (closing the handle) without calling
        // FlushPendingSnapshotOnShutdown.
        Assert.True(EnqueueOrder(dispA, session, "CL-C", 0xC, 102UL));
        probeA.DrainInbound();
        Assert.Equal(1, persister.SaveCount); // still throttled
        wal1.Dispose();

        // Round 2: brand-new dispatcher with the same persister + a
        // freshly opened WAL pointing at the same file — replay must
        // bring the engine back to the 3-orders-resting state.
        var wal2 = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var metricsB = new ChannelMetrics(84);
        var dispB = BuildDispatcher(persister, wal2, out var sinkB, out var outB,
            metrics: metricsB, throttle: throttle);
        // Drive RunLoopAsync's initial LoadPersistedStateOnLoopThread by
        // starting the dispatcher and waiting for the replayed registry state.
        //
        // Wait on the *terminal* replay signal (WalReplays metric), not just
        // the registry count: ReplayWalOnLoopThread applies each record via
        // ProcessOne (which populates the registry) and only increments
        // AddWalReplays once, after the replay loop completes. Keying the
        // wait off OrderRegistryCount>=3 alone (an intermediate effect) let
        // the test observe WalReplays==0 and flake on the assert below under
        // load (#543 follow-up). WalReplays>=1 implies every replayed record's
        // ProcessOne already ran, so the registry is fully populated too.
        dispB.Start();
        Assert.True(await WaitForAsync(
            () => metricsB.WalReplays >= 1 && dispB.OrderRegistryCount >= 3,
            ReadyTimeout), "dispatcher did not replay WAL tail before timeout");

        Assert.Equal(3, dispB.OrderRegistryCount);
        // Replay itself must not publish historical events. Once recovery is
        // complete, startup emits exactly one ChannelReset_11 for the new
        // durable epoch.
        Assert.Equal(1, sinkB.Published);
        Assert.Equal(0, outB.NewCount);
        // Replay metric reflects the 1 record consumed (the snapshot
        // covered the first 2 commands; only CL-C was in the WAL tail).
        Assert.True(metricsB.WalReplays >= 1);

        wal2.Dispose();
        await dispB.DisposeAsync();
    }

    [Fact]
    public void DisabledWal_PreservesPriorBehaviour()
    {
        var persister = new InMemoryPersister();
        var disp = BuildDispatcher(persister, wal: null, out _, out _);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("12345");

        Assert.True(EnqueueOrder(disp, session, "CL-1", 0x1, 1000UL));
        probe.DrainInbound();
        Assert.Equal(1, persister.SaveCount);
        Assert.Equal(1L, persister.Last[84].LastAppliedSeq);
    }

    [Fact]
    public async Task AsyncWriter_TruncatesWalAfterSaveCallback()
    {
        using var dir = new TempDir();
        var persister = new InMemoryPersister();
        var wal = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var metrics = new ChannelMetrics(84);
        var disp = BuildDispatcher(persister, wal, out _, out _, metrics,
            useAsyncSnapshotWriter: true);
        var probe = disp.CreateTestProbe();
        var session = new SessionId("33333");

        Assert.True(EnqueueOrder(disp, session, "CL-1", 0x1, 1UL));
        probe.DrainInbound();

        // Async writer is on a background thread; wait until it has
        // drained and truncated instead of sleeping a fixed interval.
        // Issue #396: onSaved enqueues an AuditCheckpoint work item before
        // truncation, so TestProbe-driven tests must pump the inbox.
        Assert.True(await WaitForAsync(
            () =>
            {
                probe.DrainInbound();
                return persister.SaveCount >= 1 && metrics.WalTruncations >= 1;
            },
            ReadyTimeout), "async snapshot writer did not save and truncate before timeout");

        Assert.True(persister.SaveCount >= 1);
        Assert.True(metrics.WalTruncations >= 1);
        Assert.Empty(wal.ReadAll());

        await disp.DisposeAsync();
        wal.Dispose();
    }

    [Fact]
    public async Task Replay_HaltsOnSnapshotWalBoundaryGap()
    {
        // Issue #285 follow-up (gpt-5.5 round-2 review of PR #298):
        // FileChannelWriteAheadLog.ReadAll only enforces contiguity
        // among records physically present in the WAL; it cannot
        // detect a gap straddling the snapshot/WAL boundary
        // (snapshot LastAppliedSeq=2 with WAL starting at Seq=4).
        // Replay must halt the channel rather than apply Seq=4 on
        // top of the snapshot baseline.
        using var dir = new TempDir();
        var persister = new InMemoryPersister();

        // Round 1: two orders → snapshot persisted at LastAppliedSeq=2,
        // WAL truncated.
        var wal1 = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var dispA = BuildDispatcher(persister, wal1, out _, out _);
        var probeA = dispA.CreateTestProbe();
        var session = new SessionId("99999");
        Assert.True(EnqueueOrder(dispA, session, "CL-A", 0xA, 100UL));
        probeA.DrainInbound();
        Assert.True(EnqueueOrder(dispA, session, "CL-B", 0xB, 101UL));
        probeA.DrainInbound();
        Assert.Equal(2L, persister.Last[84].LastAppliedSeq);
        wal1.Dispose();

        // Hand-craft a WAL containing a single record with Seq=4
        // (Seq=3 missing) — within ReadAll a single record passes
        // contiguity trivially, so only the dispatcher-level boundary
        // check can catch this.
        using (var raw = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            raw.Append(new WalRecord(
                Seq: 4, Kind: WalRecordKind.NewOrder,
                SessionValue: "99999", Firm: 700, ClOrdId: 0xC, OrigClOrdId: 0,
                NewOrder: new NewOrderCommand("CL-C", Sec, Side.Buy, OrderType.Limit,
                    TimeInForce.Day, Px(10.00m), 100, 100, 102UL),
                Cancel: null, Replace: null));
        }

        var wal2 = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var metricsB = new ChannelMetrics(84);
        var dispB = BuildDispatcher(persister, wal2, out var sinkB, out var outB,
            metrics: metricsB);
        var error = Assert.Throws<InvalidOperationException>(() => dispB.Start());

        Assert.Contains("WAL replay failed during boot recovery", error.Message);
        Assert.False(dispB.IsWalHealthy);
        // Engine must remain at the snapshot baseline (2 resting orders);
        // the post-gap record was not applied.
        Assert.Equal(2, dispB.OrderRegistryCount);
        // Replay must NOT publish on the wire or send ERs.
        Assert.Equal(0, sinkB.Published);
        Assert.Equal(0, outB.NewCount);

        wal2.Dispose();
        await dispB.DisposeAsync();
    }
}
