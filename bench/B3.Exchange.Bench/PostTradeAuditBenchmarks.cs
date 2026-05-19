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
/// <para>Two scenarios:</para>
/// <list type="bullet">
///   <item><b>OnTrade_NoCheckpoint</b> — steady-state append cost. No
///   fsync runs (writer batches via <c>FileStream</c>'s OS buffer); this
///   isolates serialization + index bookkeeping from disk-flush cost.</item>
///   <item><b>OnTrade_PerCallCheckpoint</b> — worst-case durability
///   (fsync after every record). Models a paranoid "fsync-per-trade"
///   policy and pins the upper bound on per-trade audit overhead.</item>
/// </list>
///
/// <para>The benchmark writes to a temp directory under
/// <see cref="GlobalSetup"/> / <see cref="GlobalCleanup"/>; the same
/// writer instance is reused across iterations so file-open and header
/// costs do not skew per-call numbers. Use
/// <c>dotnet run -c Release --project bench/B3.Exchange.Bench -- --filter '*PostTradeAudit*'</c>.</para>
///
/// <para>For the cross-thread lock contention scenario (issue #349) see
/// <see cref="PostTradeAuditContentionBenchmarks"/> in a separate class
/// so its background checkpointer thread does not pollute these
/// baseline measurements.</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class PostTradeAuditBenchmarks
{
    private string _root = null!;
    private FileAuditLogWriter _writer = null!;
    private FileAuditLogWriter _checkpointingWriter = null!;
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
        _ts = (ulong)(new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;
        _tradeId = 1;
        _record = NextRecord();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _writer.Dispose();
        _checkpointingWriter.Dispose();
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

    private PostTradeRecord NextRecord()
    {
        var id = ++_tradeId;
        return new PostTradeRecord(
            TradeId: id, TransactTimeNanos: _ts, SecurityId: BenchInstruments.PetrSecId,
            AggressorSide: (id & 1) == 0 ? Side.Buy : Side.Sell,
            Quantity: 100, PriceMantissa: BenchInstruments.Px(32.00m),
            BuyClOrdId: 1000UL + id, SellClOrdId: 2000UL + id,
            BuyFirm: 7, SellFirm: 8,
            BuyOrderId: 5000 + id, SellOrderId: 6000 + id);
    }
}

/// <summary>
/// Issue #349: measures the dispatch-thread tail latency cost of an
/// <see cref="FileAuditLogWriter.OnTrade"/> call that contends with a
/// concurrent <see cref="FileAuditLogWriter.Checkpoint"/> (held lock
/// across both <c>Flush(flushToDisk:true)</c> fsyncs).
///
/// <para>Isolated in its own class so the background checkpointer
/// thread only runs while this scenario is being measured — included
/// in <see cref="PostTradeAuditBenchmarks"/> it would pollute the
/// <c>OnTrade_NoCheckpoint</c> baseline with unrelated fsync /
/// filesystem pressure.</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class PostTradeAuditContentionBenchmarks
{
    private string _root = null!;
    private FileAuditLogWriter _writer = null!;
    private CancellationTokenSource? _cts;
    private Task? _checkpointTask;
    private ulong _ts;
    private uint _tradeId;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "B3PostTradeContention_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _writer = new FileAuditLogWriter(_root, channelNumber: 3);
        _ts = (ulong)(new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;
        _tradeId = 1;

        // Seed at least one record so Checkpoint has something to fsync.
        _writer.OnTrade(NextRecord());

        // Background checkpoint loop with no sleep — worst-case shape
        // of the tail-latency hit issue #349 is sizing.
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var w = _writer;
        _checkpointTask = Task.Factory.StartNew(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { w.Checkpoint(); } catch { break; }
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cts?.Cancel();
        try { _checkpointTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _writer.Dispose();
        _cts?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Benchmark]
    public void OnTrade_WithBackgroundCheckpoint()
    {
        _writer.OnTrade(NextRecord());
    }

    private PostTradeRecord NextRecord()
    {
        var id = ++_tradeId;
        return new PostTradeRecord(
            TradeId: id, TransactTimeNanos: _ts, SecurityId: BenchInstruments.PetrSecId,
            AggressorSide: (id & 1) == 0 ? Side.Buy : Side.Sell,
            Quantity: 100, PriceMantissa: BenchInstruments.Px(32.00m),
            BuyClOrdId: 1000UL + id, SellClOrdId: 2000UL + id,
            BuyFirm: 7, SellFirm: 8,
            BuyOrderId: 5000 + id, SellOrderId: 6000 + id);
    }
}


