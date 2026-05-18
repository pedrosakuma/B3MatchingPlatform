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
    /// snapshotSeq and observe truncation deferral.</summary>
    private sealed class ManualPostTradeSink : IPostTradeSink
    {
        public int CheckpointCount;
        public List<long> Boundaries { get; } = new();
        private long _pending;
        public long DurableOverride = -1;
        public void OnTrade(in PostTradeRecord record) { }
        public void OnCommandBoundary(long commandSeq)
        {
            Boundaries.Add(commandSeq);
            if (commandSeq > _pending) _pending = commandSeq;
        }
        public void Checkpoint() => CheckpointCount++;
        // When DurableOverride >= 0 the test pins the watermark; otherwise we
        // honour OnCommandBoundary as the durable seq (i.e. Checkpoint trivially
        // promotes pending → durable).
        public long DurableThroughCommandSeq
            => DurableOverride >= 0 ? DurableOverride : _pending;
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
            packetSink: new NoOpPacketSink(),
            outbound: new NoOpOutbound(),
            logger: NullLogger<ChannelDispatcher>.Instance,
            metrics: metrics,
            persister: persister,
            snapshotThrottle: null,
            useAsyncSnapshotWriter: false,
            wal: wal,
            postTradeSink: postTradeSink);

    private static bool EnqueueOrder(ChannelDispatcher disp, string clOrdId, ulong clOrdIdValue, ulong nanos)
        => disp.EnqueueNewOrder(
            new NewOrderCommand(clOrdId, Sec, Side.Buy, OrderType.Limit,
                TimeInForce.Day, Px(10.00m), 100, 100, nanos),
            new SessionId("10101"), enteringFirm: 700, clOrdIdValue: clOrdIdValue);

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
}
