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
public class MatchingBenchmarks
{
    // Batch size per benchmark invocation. Large enough to push per-iteration
    // time well above BenchmarkDotNet's minimum-iteration-time threshold, so
    // the per-op CI tightens to a useful level (target < 5%).
    private const int Batch = 2048;

    private MatchingEngine _engine = null!;
    private NoOpSink _sink = null!;
    private long[] _cancelOrderIds = null!;
    private long _basePx;
    private long _aggressorQty;

    [Params(10, 100)]
    public int CrossLevels;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sink = new NoOpSink();
        _engine = new MatchingEngine(new[] { BenchInstruments.Petr4 }, _sink, NullLogger<MatchingEngine>.Instance);
        _cancelOrderIds = new long[Batch];
        _basePx = BenchInstruments.Px(32.00m);
        _aggressorQty = 100L * CrossLevels;
    }

    // ---------- NewOrder_NoCross ----------
    // Submits Batch distinct LIMIT bids at descending prices so each one rests
    // (never crosses) on a previously-empty book.

    [IterationSetup(Target = nameof(NewOrder_NoCross))]
    public void Setup_NoCross()
    {
        _engine.ResetForChannelReset();
        _sink.Reset();
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public void NewOrder_NoCross()
    {
        for (int i = 0; i < Batch; i++)
        {
            _engine.Submit(new NewOrderCommand(
                ClOrdId: "C",
                SecurityId: BenchInstruments.PetrSecId,
                Side: Side.Buy,
                Type: OrderType.Limit,
                Tif: TimeInForce.Day,
                PriceMantissa: _basePx - i,
                Quantity: 100,
                EnteringFirm: 7,
                EnteredAtNanos: 0));
        }
    }

    // ---------- NewOrder_FullCross ----------
    // Each invocation runs Batch aggressor sweeps against a freshly-built book
    // of CrossLevels resting bids. Setup pre-stages Batch * CrossLevels makers
    // so the measured loop is purely the sweep cost.

    [IterationSetup(Target = nameof(NewOrder_FullCross))]
    public void Setup_FullCross()
    {
        _engine.ResetForChannelReset();
        _sink.Reset();

        // Stage Batch independent price bands so each aggressor sweep finds
        // its own CrossLevels-deep book to consume. Bands are spaced so they
        // never cross each other.
        long band = _basePx;
        for (int s = 0; s < Batch; s++)
        {
            for (int i = 0; i < CrossLevels; i++)
            {
                _engine.Submit(new NewOrderCommand(
                    ClOrdId: "M",
                    SecurityId: BenchInstruments.PetrSecId,
                    Side: Side.Buy,
                    Type: OrderType.Limit,
                    Tif: TimeInForce.Day,
                    PriceMantissa: band - i,
                    Quantity: 100,
                    EnteringFirm: 8,
                    EnteredAtNanos: 0));
            }
            band -= CrossLevels;
        }
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public void NewOrder_FullCross()
    {
        for (int s = 0; s < Batch; s++)
        {
            _engine.Submit(new NewOrderCommand(
                ClOrdId: "AGG",
                SecurityId: BenchInstruments.PetrSecId,
                Side: Side.Sell,
                Type: OrderType.Market,
                Tif: TimeInForce.IOC,
                PriceMantissa: 0,
                Quantity: _aggressorQty,
                EnteringFirm: 7,
                EnteredAtNanos: 0));
        }
    }

    // ---------- Cancel_RestingOrder ----------
    // Pre-seeds Batch resting orders; benchmark cancels each one once.

    [IterationSetup(Target = nameof(Cancel_RestingOrder))]
    public void Setup_Cancel()
    {
        _engine.ResetForChannelReset();
        _sink.Reset();
        long firstId = _engine.PeekNextOrderId;
        for (int i = 0; i < Batch; i++)
        {
            _engine.Submit(new NewOrderCommand(
                ClOrdId: "R",
                SecurityId: BenchInstruments.PetrSecId,
                Side: Side.Buy,
                Type: OrderType.Limit,
                Tif: TimeInForce.Day,
                PriceMantissa: _basePx - i,
                Quantity: 100,
                EnteringFirm: 7,
                EnteredAtNanos: 0));
            _cancelOrderIds[i] = firstId + i;
        }
    }

    [Benchmark(OperationsPerInvoke = Batch)]
    public void Cancel_RestingOrder()
    {
        for (int i = 0; i < Batch; i++)
        {
            _engine.Cancel(new CancelOrderCommand(
                ClOrdId: "X",
                SecurityId: BenchInstruments.PetrSecId,
                OrderId: _cancelOrderIds[i],
                EnteredAtNanos: 0));
        }
    }
}
