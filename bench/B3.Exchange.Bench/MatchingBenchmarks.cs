using BenchmarkDotNet.Attributes;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Bench;

/// <summary>
/// Hot-path matching engine benchmarks.
///
/// Scenarios:
/// - <see cref="NewOrder_NoCross"/>     — single LIMIT order rests on an empty book.
/// - <see cref="NewOrder_FullCross"/>   — aggressor sweeps a pre-built book of N levels.
/// - <see cref="Cancel_RestingOrder"/>  — cancel an already-resting order by orderId.
///
/// All benchmarks reset the engine in <see cref="IterationSetupAttribute"/> so the JIT
/// sees a steady-state input. Runs against the host runtime (net10.0) by default.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5, invocationCount: 1)]
public class MatchingBenchmarks
{
    private MatchingEngine _engine = null!;
    private NoOpSink _sink = null!;
    private long _prebuiltCancelOrderId;

    [Params(10, 100)]
    public int CrossLevels;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sink = new NoOpSink();
        _engine = new MatchingEngine(new[] { BenchInstruments.Petr4 }, _sink, NullLogger<MatchingEngine>.Instance);
    }

    // ---------- NewOrder_NoCross ----------

    [IterationSetup(Target = nameof(NewOrder_NoCross))]
    public void Setup_NoCross()
    {
        _engine.ResetForChannelReset();
        _sink.Reset();
    }

    [Benchmark]
    public void NewOrder_NoCross()
    {
        var cmd = new NewOrderCommand(
            ClOrdId: "C1",
            SecurityId: BenchInstruments.PetrSecId,
            Side: Side.Buy,
            Type: OrderType.Limit,
            Tif: TimeInForce.Day,
            PriceMantissa: BenchInstruments.Px(32.00m),
            Quantity: 100,
            EnteringFirm: 7,
            EnteredAtNanos: 0);
        _engine.Submit(cmd);
    }

    // ---------- NewOrder_FullCross ----------

    [IterationSetup(Target = nameof(NewOrder_FullCross))]
    public void Setup_FullCross()
    {
        _engine.ResetForChannelReset();
        _sink.Reset();

        // Build N resting bids descending from 32.00 by 1 tick. Aggressor (sell, market IOC)
        // will sweep all of them in a single Submit().
        long basePx = BenchInstruments.Px(32.00m);
        for (int i = 0; i < CrossLevels; i++)
        {
            _engine.Submit(new NewOrderCommand(
                ClOrdId: $"M{i}",
                SecurityId: BenchInstruments.PetrSecId,
                Side: Side.Buy,
                Type: OrderType.Limit,
                Tif: TimeInForce.Day,
                PriceMantissa: basePx - i * 1, // 1 mantissa = 1 tick (0.0001) — we use Px so /10000.
                Quantity: 100,
                EnteringFirm: 8,
                EnteredAtNanos: 0));
        }
    }

    [Benchmark]
    public void NewOrder_FullCross()
    {
        var aggressor = new NewOrderCommand(
            ClOrdId: "AGG",
            SecurityId: BenchInstruments.PetrSecId,
            Side: Side.Sell,
            Type: OrderType.Market,
            Tif: TimeInForce.IOC,
            PriceMantissa: 0,
            Quantity: 100L * CrossLevels,
            EnteringFirm: 7,
            EnteredAtNanos: 0);
        _engine.Submit(aggressor);
    }

    // ---------- Cancel_RestingOrder ----------

    [IterationSetup(Target = nameof(Cancel_RestingOrder))]
    public void Setup_Cancel()
    {
        _engine.ResetForChannelReset();
        _sink.Reset();
        // Submit one resting order and remember its id (NoOpSink discards events,
        // so we use the engine's own sequential allocator: first order = 1).
        _engine.Submit(new NewOrderCommand(
            ClOrdId: "R1",
            SecurityId: BenchInstruments.PetrSecId,
            Side: Side.Buy,
            Type: OrderType.Limit,
            Tif: TimeInForce.Day,
            PriceMantissa: BenchInstruments.Px(32.00m),
            Quantity: 100,
            EnteringFirm: 7,
            EnteredAtNanos: 0));
        _prebuiltCancelOrderId = _engine.PeekNextOrderId - 1;
    }

    [Benchmark]
    public void Cancel_RestingOrder()
    {
        _engine.Cancel(new CancelOrderCommand(
            ClOrdId: "X1",
            SecurityId: BenchInstruments.PetrSecId,
            OrderId: _prebuiltCancelOrderId,
            EnteredAtNanos: 0));
    }
}
