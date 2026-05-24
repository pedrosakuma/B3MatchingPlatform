using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Exchange.Persistence;
using B3.Exchange.PostTrade;
using Microsoft.Extensions.Logging.Abstractions;
using OrderType = B3.Exchange.Matching.OrderType;
using Side = B3.Exchange.Matching.Side;
using TimeInForce = B3.Exchange.Matching.TimeInForce;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Issue #329 PR-4: WAL truncation must be gated by the post-trade audit
/// log's durability watermark. These tests use a controllable
/// <see cref="IPostTradeSink"/> to pin the dispatcher's behaviour on both
/// the sync (dispatch-thread) and async (writer-thread) truncation paths.
/// </summary>
public class ChannelDispatcherAuditWatermarkTests
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
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    private sealed class NoOpOutbound : ICoreOutbound
    {
        public bool WriteExecutionReportNew(SessionId s, uint f, ulong c, in OrderAcceptedEvent e, ulong r = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportTrade(SessionId s, in TradeEvent e, bool a, long o, ulong c, long l, long u, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveTrade(SessionId s, ulong c, long o, in TradeEvent e, long l, long u, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportPassiveCancel(SessionId s, ulong c, long o, in OrderCanceledEvent e, ulong rc, ulong r = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportModify(SessionId s, long sid, long o, ulong c, ulong oc, Side side, long np, long nq, ulong tt, uint rs, ulong r = ulong.MaxValue, DurabilityHandle d = default) => true;
        public bool WriteExecutionReportReject(SessionId s, in B3.Exchange.Matching.RejectEvent e, ulong c, DurabilityHandle d = default) => true;
    }

    private sealed class InMemoryPersister : IChannelStatePersister
    {
        public Dictionary<byte, ChannelStateSnapshot> Last { get; } = new();
        public int SaveCount;
        public ChannelStateSnapshot? TryLoad(byte n)
            => Last.TryGetValue(n, out var s) ? s : null;
        public long Save(ChannelStateSnapshot s)
        {
            SaveCount++;
            Last[s.ChannelNumber] = s;
            return 1;
        }
    }

    /// <summary>Controllable sink: exposes manual watermark advancement so the
    /// test can hold DurableThroughCommandSeq below the dispatcher's
    /// snapshotSeq and observe truncation deferral. Also counts OnTrade
    /// invocations so PR-5 tests can assert the replay-mode gate.</summary>
    private sealed class ManualPostTradeSink : IPostTradeSink
    {
        public int CheckpointCount;
        public int OnTradeCount;
        public List<long> Boundaries { get; } = new();
        private long _pending;
        public long DurableOverride = -1;
        public void OnTrade(in PostTradeRecord record) { OnTradeCount++; }
        public void OnCommandBoundary(long commandSeq)
        {
            Boundaries.Add(commandSeq);
            if (commandSeq > _pending) _pending = commandSeq;
        }
        public void Checkpoint() => CheckpointCount++;
        public void OnBust(in BustRecord record, DateOnly tradeDate) { }
        public void OnRejectAttempt(in RejectAttemptRecord record) { }
        // When DurableOverride >= 0 the test pins the watermark; otherwise we
        // honour OnCommandBoundary as the durable seq (i.e. Checkpoint trivially
        // promotes pending → durable).
        public long DurableThroughCommandSeq
            => DurableOverride >= 0 ? DurableOverride : _pending;
    }

    private sealed class ThreadRecordingPostTradeSink : IDispatchThreadCheckpointSink
    {
        private long _pending;
        private long _durable;
        public int PrepareThreadId;
        public int FlushThreadId;
        public int PrepareCount;
        public int FlushCount;

        public void OnTrade(in PostTradeRecord record) { }
        public void OnCommandBoundary(long commandSeq)
        {
            if (commandSeq > _pending) _pending = commandSeq;
        }
        public void Checkpoint()
        {
            using var op = BeginCheckpointOnDispatchThread();
            op.FlushToDiskAndCommit();
        }
        public void OnBust(in BustRecord record, DateOnly tradeDate) { }
        public void OnRejectAttempt(in RejectAttemptRecord record) { }
        public long DurableThroughCommandSeq => Volatile.Read(ref _durable);

        public IAuditCheckpointOperation BeginCheckpointOnDispatchThread()
        {
            PrepareThreadId = Environment.CurrentManagedThreadId;
            PrepareCount++;
            return new Operation(this, _pending);
        }

        private sealed class Operation : IAuditCheckpointOperation
        {
            private readonly ThreadRecordingPostTradeSink _owner;
            private readonly long _pending;
            public Operation(ThreadRecordingPostTradeSink owner, long pending)
            {
                _owner = owner;
                _pending = pending;
            }
            public void FlushToDiskAndCommit()
            {
                _owner.FlushThreadId = Environment.CurrentManagedThreadId;
                _owner.FlushCount++;
                Volatile.Write(ref _owner._durable, _pending);
            }
            public void Dispose() { }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "wm-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    private static ChannelDispatcher BuildDispatcher(
        IChannelStatePersister persister,
        IChannelWriteAheadLog wal,
        ChannelMetrics metrics,
        IPostTradeSink postTradeSink)
        => new(
            channelNumber: 84,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = new NoOpPacketSink(),
                Outbound = new NoOpOutbound(),
                Logger = NullLogger<ChannelDispatcher>.Instance,
                Metrics = metrics,
                Persister = persister,
                SnapshotThrottle = null,
                UseAsyncSnapshotWriter = false,
                Wal = wal,
                PostTradeSink = postTradeSink,
            });

    private static bool EnqueueOrder(ChannelDispatcher disp, string clOrdId, ulong clOrdIdValue, ulong nanos)
        => disp.EnqueueNewOrder(
            new NewOrderCommand(clOrdId, Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, nanos),
            new SessionId("10101"), enteringFirm: 700, clOrdIdValue: clOrdIdValue);

    private static bool EnqueueSideOrder(ChannelDispatcher disp, Side side, string clOrdId, ulong clOrdIdValue, ulong nanos, uint firm = 700, string sessionId = "10101")
        => disp.EnqueueNewOrder(
            new NewOrderCommand(clOrdId, Sec, side, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, firm, nanos),
            new SessionId(sessionId), enteringFirm: firm, clOrdIdValue: clOrdIdValue);

    [Fact]
    public void SyncTruncate_DeferredWhenAuditWatermarkBehind()
    {
        using var dir = new TempDir();
        var persister = new InMemoryPersister();
        var wal = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var metrics = new ChannelMetrics(84);
        var sink = new ManualPostTradeSink { DurableOverride = 0 };
        var disp = BuildDispatcher(persister, wal, metrics, sink);
        var probe = disp.CreateTestProbe();

        Assert.True(EnqueueOrder(disp, "CL-1", 0x1, 1UL));
        probe.DrainInbound();

        // Snapshot ran (SaveCount=1) and Checkpoint was attempted, but the
        // watermark stayed at 0 (< snapshot's LastAppliedSeq=1) so truncate
        // was deferred. The WAL record must still be on disk.
        Assert.Equal(1, persister.SaveCount);
        Assert.True(sink.CheckpointCount >= 1);
        Assert.Equal(0, metrics.WalTruncations);
        Assert.True(metrics.AuditWalTruncateDeferred >= 1);
        Assert.Single(wal.ReadAll());
        wal.Dispose();
    }

    [Fact]
    public void SyncTruncate_ProceedsOnceAuditWatermarkCatchesUp()
    {
        using var dir = new TempDir();
        var persister = new InMemoryPersister();
        var wal = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var metrics = new ChannelMetrics(84);
        var sink = new ManualPostTradeSink { DurableOverride = 0 };
        var disp = BuildDispatcher(persister, wal, metrics, sink);
        var probe = disp.CreateTestProbe();

        // Cmd 1: watermark behind → deferred.
        Assert.True(EnqueueOrder(disp, "CL-1", 0x1, 1UL));
        probe.DrainInbound();
        Assert.Equal(1, metrics.AuditWalTruncateDeferred);
        Assert.Equal(0, metrics.WalTruncations);

        // Operator catches up the watermark to cover everything we've
        // processed so far; the next command's snapshot must now truncate.
        sink.DurableOverride = long.MaxValue;
        Assert.True(EnqueueOrder(disp, "CL-2", 0x2, 2UL));
        probe.DrainInbound();
        Assert.True(metrics.WalTruncations >= 1);
        Assert.Empty(wal.ReadAll());
        wal.Dispose();
    }

    [Fact]
    public void SyncTruncate_NoGate_WhenSinkIsNullPostTradeSink()
    {
        // Regression: a channel with audit disabled (default) must keep the
        // pre-#329 truncate-everything behaviour — the no-op sink reports
        // DurableThroughCommandSeq=long.MaxValue.
        using var dir = new TempDir();
        var persister = new InMemoryPersister();
        var wal = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var metrics = new ChannelMetrics(84);
        var disp = BuildDispatcher(persister, wal, metrics, NullPostTradeSink.Instance);
        var probe = disp.CreateTestProbe();

        Assert.True(EnqueueOrder(disp, "CL-1", 0x1, 1UL));
        probe.DrainInbound();

        Assert.True(metrics.WalTruncations >= 1);
        Assert.Equal(0, metrics.AuditWalTruncateDeferred);
        Assert.Empty(wal.ReadAll());
        wal.Dispose();
    }

    [Fact]
    public void AuditCheckpoint_RunsAfterSnapshotSave_EvenWithoutWal()
    {
        // Issue #352 follow-up (PR #353 review): the audit sink must
        // be Checkpointed after every successful snapshot save even
        // when no WAL is configured. Pre-fix, TruncateWalAfterSyncSave
        // returned immediately on `_wal is null`, so audit-only
        // channels never fsync'd .log/.idx or advanced the watermark.
        var persister = new InMemoryPersister();
        var metrics = new ChannelMetrics(84);
        var sink = new ManualPostTradeSink();
        var disp = new ChannelDispatcher(
            channelNumber: 84,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = new NoOpPacketSink(),
                Outbound = new NoOpOutbound(),
                Logger = NullLogger<ChannelDispatcher>.Instance,
                Metrics = metrics,
                Persister = persister,
                SnapshotThrottle = null,
                UseAsyncSnapshotWriter = false,
                Wal = null,
                PostTradeSink = sink,
            });
        var probe = disp.CreateTestProbe();

        Assert.True(EnqueueOrder(disp, "CL-1", 0x1, 1UL));
        probe.DrainInbound();

        // Snapshot must have run AND the audit sink Checkpointed,
        // even though no WAL was configured. WAL-related metrics
        // stay at zero (there is no WAL to truncate or defer).
        Assert.Equal(1, persister.SaveCount);
        Assert.True(sink.CheckpointCount >= 1,
            $"expected audit Checkpoint to run, got CheckpointCount={sink.CheckpointCount}");
        Assert.Equal(0, metrics.WalTruncations);
        Assert.Equal(0, metrics.AuditWalTruncateDeferred);
    }

    [Fact]
    public async Task AsyncSnapshot_AuditCheckpoint_PreparesOnDispatcher_AndFlushesOnWriterThread()
    {
        var persister = new InMemoryPersister();
        var sink = new ThreadRecordingPostTradeSink();
        var disp = new ChannelDispatcher(
            channelNumber: 84,
            engineFactory: s => new MatchingEngine(new[] { Petr4 }, s, NullLogger<MatchingEngine>.Instance),
            options: new ChannelDispatcherOptions
            {
                PacketSink = new NoOpPacketSink(),
                Outbound = new NoOpOutbound(),
                Logger = NullLogger<ChannelDispatcher>.Instance,
                Metrics = new ChannelMetrics(84),
                Persister = persister,
                SnapshotThrottle = null,
                UseAsyncSnapshotWriter = true,
                Wal = null,
                PostTradeSink = sink,
            });

        disp.Start();
        Assert.True(EnqueueOrder(disp, "CL-ASYNC", 0xA1, 1UL));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((Volatile.Read(ref sink.FlushCount) == 0 || persister.SaveCount == 0) && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        Assert.True(sink.PrepareCount >= 1);
        Assert.True(sink.FlushCount >= 1);
        Assert.NotEqual(0, sink.PrepareThreadId);
        Assert.NotEqual(0, sink.FlushThreadId);
        Assert.NotEqual(sink.PrepareThreadId, sink.FlushThreadId);
        Assert.True(sink.DurableThroughCommandSeq >= 1);

        await disp.DisposeAsync();
    }


    [Fact]
    public void Dispatcher_ForwardsCommandSeq_ToSinkBoundaries()
    {
        // Pins the contract that OnCommandBoundary fires after every command
        // with the dispatcher's _lastAppliedSeq value.
        using var dir = new TempDir();
        var persister = new InMemoryPersister();
        var wal = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var metrics = new ChannelMetrics(84);
        var sink = new ManualPostTradeSink { DurableOverride = long.MaxValue };
        var disp = BuildDispatcher(persister, wal, metrics, sink);
        var probe = disp.CreateTestProbe();

        Assert.True(EnqueueOrder(disp, "CL-1", 0x1, 1UL));
        Assert.True(EnqueueOrder(disp, "CL-2", 0x2, 2UL));
        Assert.True(EnqueueOrder(disp, "CL-3", 0x3, 3UL));
        probe.DrainInbound();

        // 3 commands → 3 boundaries, monotonically increasing from 1.
        Assert.Equal(new long[] { 1, 2, 3 }, sink.Boundaries);
        wal.Dispose();
    }

    [Fact]
    public async Task Replay_SuppressesOnTrade_ForCommands_AtOrBelow_BootDurableSeq()
    {
        // Issue #329 PR-5: during WAL replay the audit sink must NOT see
        // OnTrade for any trade whose owning command was already fsync'd
        // pre-crash. Phase 1 uses the live (DrainInbound) path to stage
        // WAL records that cross. Phase 2 uses the REAL replay path
        // (disp.Start → LoadPersistedStateOnLoopThread → ReplayWalOnLoopThread)
        // with a sink whose recovered watermark covers both commands;
        // OnTrade must not be invoked and AuditReplaySkipped must bump.
        using var dir = new TempDir();
        // Phase 1 — populate the WAL with cross-producing commands.
        // DurableOverride=0 PREVENTS the post-snapshot WAL truncation
        // (PR-4 gate) so phase 2 has the staged records to replay.
        {
            var wal = new FileChannelWriteAheadLog(dir.Path, 84,
                NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
            var sink = new ManualPostTradeSink { DurableOverride = 0 };
            var disp = BuildDispatcher(new InMemoryPersister(),
                wal, new ChannelMetrics(84), sink);
            var probe = disp.CreateTestProbe();
            // Different firms to keep this independent of any future
            // self-trade-prevention default.
            Assert.True(EnqueueSideOrder(disp, Side.Sell, "S-1", 0x10, 1UL, firm: 7, sessionId: "10101"));
            Assert.True(EnqueueSideOrder(disp, Side.Buy, "B-1", 0x11, 2UL, firm: 8, sessionId: "10102"));
            probe.DrainInbound();
            Assert.Equal(new long[] { 1, 2 }, sink.Boundaries);
            // CRITICAL: this trade must actually happen for the gate test
            // below to be meaningful. If this regresses (e.g. matching
            // engine changes default phase, or this test's crossing setup
            // becomes invalid) we fail here loudly instead of in phase 2.
            Assert.Equal(1, sink.OnTradeCount);
            wal.Dispose();
        }

        // Phase 2 — real replay. Sink claims watermark=2 (covers both
        // staged commands) → the gate must skip the trade's OnTrade call.
        var metrics2 = new ChannelMetrics(84);
        var sink2 = new ManualPostTradeSink { DurableOverride = 2 };
        var wal2 = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        // Sanity: phase 1 actually wrote both records to the WAL so
        // there is something for the replay path to consume.
        var files = string.Join(", ", Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories)
            .Select(f => $"{Path.GetRelativePath(dir.Path, f)}({new FileInfo(f).Length}B)"));
        Assert.True(wal2.ReadAll().Count == 2, $"expected 2 WAL records; files=[{files}]");
        var persister2 = new InMemoryPersister();
        var disp2 = BuildDispatcher(persister2, wal2, metrics2, sink2);

        disp2.Start();
        // Quiescence: AddWalReplays runs AFTER the entire WAL replay
        // foreach completes. SaveCount bumps mid-replay (each command's
        // OnAfterCommandFlushed under the AlwaysPersist throttle) so it
        // is NOT a reliable post-replay barrier. Poll WalReplays
        // directly.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (metrics2.WalReplays < 2 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        // The replay path itself emits a trade event the gate must
        // suppress. Boundaries still fire (they don't go through the gate).
        Assert.Equal(2, metrics2.WalReplays);
        Assert.Equal(0, sink2.OnTradeCount);
        Assert.True(metrics2.AuditReplaySkipped >= 1,
            $"expected AuditReplaySkipped>=1, got {metrics2.AuditReplaySkipped}");

        await disp2.DisposeAsync();
        wal2.Dispose();
    }

    [Fact]
    public async Task Replay_AllowsOnTrade_ForCommands_Above_BootDurableSeq()
    {
        // Mirror with watermark BELOW the trade's command seq: the
        // gate must let the trade through so the audit log is repaired.
        using var dir = new TempDir();
        {
            var wal = new FileChannelWriteAheadLog(dir.Path, 84,
                NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
            // DurableOverride=0 prevents PR-4 truncation, preserving the
            // WAL records phase 2 needs to replay.
            var sink = new ManualPostTradeSink { DurableOverride = 0 };
            var disp = BuildDispatcher(new InMemoryPersister(),
                wal, new ChannelMetrics(84), sink);
            var probe = disp.CreateTestProbe();
            Assert.True(EnqueueSideOrder(disp, Side.Sell, "S-1", 0x10, 1UL, firm: 7, sessionId: "10101"));
            Assert.True(EnqueueSideOrder(disp, Side.Buy, "B-1", 0x11, 2UL, firm: 8, sessionId: "10102"));
            probe.DrainInbound();
            wal.Dispose();
        }

        var metrics2 = new ChannelMetrics(84);
        // Watermark=1 → command 2 (the trade) is NOT covered.
        var sink2 = new ManualPostTradeSink { DurableOverride = 1 };
        var wal2 = new FileChannelWriteAheadLog(dir.Path, 84,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var persister2 = new InMemoryPersister();
        var disp2 = BuildDispatcher(persister2, wal2, metrics2, sink2);

        disp2.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (metrics2.WalReplays < 2 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        Assert.Equal(0, metrics2.AuditReplaySkipped);
        Assert.Equal(2, metrics2.WalReplays);

        await disp2.DisposeAsync();
        wal2.Dispose();
    }
}
