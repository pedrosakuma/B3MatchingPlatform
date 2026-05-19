using B3.Exchange.Matching;
using B3.Exchange.PostTrade;
using BenchmarkDotNet.Attributes;

namespace B3.Exchange.Bench;

/// <summary>
/// Issue #329 PR-6: hot-path benchmark for the post-trade audit log
/// writer. The dispatcher invokes <see cref="FileAuditLogWriter.OnTrade"/>
/// synchronously on the dispatch thread for every matched trade, so
/// any per-call regression directly inflates engine latency.
///
/// <para>Scenarios:</para>
/// <list type="bullet">
///   <item><b>OnTrade_NoCheckpoint</b> — steady-state append cost. No
///   fsync runs (writer batches via <c>FileStream</c>'s OS buffer); this
///   isolates serialization + index bookkeeping from disk-flush cost.</item>
///   <item><b>OnTrade_PerCallCheckpoint</b> — worst-case durability
///   (fsync after every record). Models a paranoid "fsync-per-trade"
///   policy and pins the upper bound on per-trade audit overhead.</item>
///   <item><b>OnTrade_WithBackgroundCheckpoint</b> — issue #349: a
///   background thread runs <see cref="FileAuditLogWriter.Checkpoint"/>
///   in a tight loop while OnTrade fires on the bench thread. The fsync
///   inside Checkpoint holds the shared lock for the full I/O latency;
///   any spike here is the tail-latency hit the issue is sizing.</item>
/// </list>
///
/// <para>The benchmark writes to a temp directory under
/// <see cref="GlobalSetup"/> / <see cref="GlobalCleanup"/>; the same
/// writer instance is reused across iterations so file-open and header
/// costs do not skew per-call numbers. Use
/// <c>dotnet run -c Release --project bench/B3.Exchange.Bench -- --filter '*PostTradeAudit*'</c>.</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class PostTradeAuditBenchmarks
{
    private string _root = null!;
    private FileAuditLogWriter _writer = null!;
    private FileAuditLogWriter _checkpointingWriter = null!;
    private FileAuditLogWriter _contentionWriter = null!;
    private CancellationTokenSource? _contentionCts;
    private Task? _contentionTask;
    private PostTradeRecord _record;
    private ulong _ts;
    private uint _tradeId;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "B3PostTradeBench_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _writer = new FileAuditLogWriter(_root, channelNumber: 1);
        _checkpointingWriter = new FileAuditLogWriter(_root, channelNumber: 2);
        _contentionWriter = new FileAuditLogWriter(_root, channelNumber: 3);
        _ts = (ulong)(new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;
        _tradeId = 1;
        _record = NextRecord();

        // Background checkpoint thread for the contention benchmark.
        // Loops Checkpoint() with no sleep so the dispatch-thread path
        // contends with the fsync on (almost) every iteration — this is
        // the worst-case shape of the tail-latency hit issue #349 is
        // trying to characterize.
        _contentionCts = new CancellationTokenSource();
        var ct = _contentionCts.Token;
        var w = _contentionWriter;
        _contentionTask = Task.Factory.StartNew(() =>
        {
            // Seed at least one record so Checkpoint has something to fsync.
            try { w.OnTrade(NextRecord()); } catch { }
            while (!ct.IsCancellationRequested)
            {
                try { w.Checkpoint(); } catch { /* writer disposed */ break; }
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _contentionCts?.Cancel();
        try { _contentionTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _writer.Dispose();
        _checkpointingWriter.Dispose();
        _contentionWriter.Dispose();
        _contentionCts?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Benchmark(Baseline = true)]
    public void OnTrade_NoCheckpoint()
    {
        _writer.OnTrade(NextRecord());
    }

    [Benchmark]
    public void OnTrade_PerCallCheckpoint()
    {
        _checkpointingWriter.OnTrade(NextRecord());
        _checkpointingWriter.OnCommandBoundary(_tradeId);
        _checkpointingWriter.Checkpoint();
    }

    [Benchmark]
    public void OnTrade_WithBackgroundCheckpoint()
    {
        // Bench thread = "dispatch thread"; background task =
        // "async-snapshot writer thread" calling Checkpoint. Any
        // contention shows up as inflated mean/p99 here vs the
        // NoCheckpoint baseline.
        _contentionWriter.OnTrade(NextRecord());
    }

    private PostTradeRecord NextRecord()
    {
        var id = ++_tradeId;
        // Stay within a single UTC day so RotateTo is never invoked; the
        // bench is measuring the steady-state hot path, not rollover.
        return new PostTradeRecord(
            TradeId: id, TransactTimeNanos: _ts, SecurityId: BenchInstruments.PetrSecId,
            AggressorSide: (id & 1) == 0 ? Side.Buy : Side.Sell,
            Quantity: 100, PriceMantissa: BenchInstruments.Px(32.00m),
            BuyClOrdId: 1000UL + id, SellClOrdId: 2000UL + id,
            BuyFirm: 7, SellFirm: 8,
            BuyOrderId: 5000 + id, SellOrderId: 6000 + id);
    }
}

