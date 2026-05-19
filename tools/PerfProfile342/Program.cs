using System.Diagnostics;
using B3.Exchange.Bench;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

// Standalone perf-profile harness for issue #342 (SortedDictionary go/no-go).
// Runs one of three matching workloads in a tight loop for a fixed wall-clock
// duration so an external profiler (dotnet-trace, perf) can attach to a
// long-lived process whose CPU is dominated by MatchingEngine work.
//
// Usage:
//   PerfProfile342 <scenario> [--depth N] [--seconds S]
//
// Scenarios (each iteration rebuilds the book so memory stays bounded;
// the profile therefore includes book-building time alongside the named
// hot path — that is intentional for the #342 gate which cares about
// the *aggregate* SortedDictionary cost across insert + cross paths):
//   full-cross    Builds a depth-N book then a single aggressor sweeps
//                 the whole side. Wider scope than
//                 MatchingBenchmarks.NewOrder_FullCross, which measures
//                 the sweep in isolation.
//   mid-depth    Builds a depth-N book on even offsets, then POSTs a
//                 single resting order at an odd offset guaranteed to
//                 fall between two existing levels (forces a real
//                 mid-book insert into SortedDictionary).
//   no-cross    Rests at descending prices on empty book (matches
//                 MatchingBenchmarks.NewOrder_NoCross).

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: PerfProfile342 <full-cross|mid-depth|no-cross> [--depth N] [--seconds S]");
    return 2;
}

string scenario = args[0];
int depth = 100;
int seconds = 30;
for (int i = 1; i + 1 < args.Length; i += 2)
{
    if (args[i] == "--depth") depth = int.Parse(args[i + 1]);
    else if (args[i] == "--seconds") seconds = int.Parse(args[i + 1]);
}

var sink = new NoOpSink();
var engine = new MatchingEngine(new[] { BenchInstruments.Petr4 }, sink, NullLogger<MatchingEngine>.Instance);
long basePx = BenchInstruments.Px(32.00m);

Console.WriteLine($"scenario={scenario} depth={depth} seconds={seconds} pid={Environment.ProcessId}");
Console.WriteLine("warming up 3s ...");
RunFor(scenario, depth, engine, sink, basePx, TimeSpan.FromSeconds(3));

Console.WriteLine($"profiling window OPEN — attach now (pid={Environment.ProcessId}) ...");
var sw = Stopwatch.StartNew();
long iterations = RunFor(scenario, depth, engine, sink, basePx, TimeSpan.FromSeconds(seconds));
sw.Stop();

Console.WriteLine($"profiling window CLOSED iterations={iterations} elapsedMs={sw.ElapsedMilliseconds} ops/s={iterations * 1000.0 / sw.ElapsedMilliseconds:N0}");
return 0;

static long RunFor(string scenario, int depth, MatchingEngine engine, NoOpSink sink, long basePx, TimeSpan duration)
{
    var deadline = Stopwatch.StartNew();
    long iterations = 0;
    switch (scenario)
    {
        case "full-cross":
            while (deadline.Elapsed < duration)
            {
                engine.ResetForChannelReset();
                sink.Reset();
                long band = basePx;
                for (int i = 0; i < depth; i++)
                {
                    engine.Submit(MakeOrder(Side.Buy, OrderType.Limit, TimeInForce.Day, band - i, 100, firm: 8));
                }
                long aggressorQty = 100L * depth;
                engine.Submit(MakeOrder(Side.Sell, OrderType.Market, TimeInForce.IOC, 0, aggressorQty, firm: 7));
                iterations++;
            }
            break;

        case "mid-depth":
            // Build a fresh book of `depth` levels each iteration (so memory
            // stays bounded) and POST a single mid-depth resting order.
            while (deadline.Elapsed < duration)
            {
                engine.ResetForChannelReset();
                sink.Reset();
                for (int i = 0; i < depth; i++)
                {
                    engine.Submit(MakeOrder(Side.Buy, OrderType.Limit, TimeInForce.Day, basePx - i * 2, 100, firm: 8));
                }
                // POST at mid-depth — odd offset guaranteed to fall between
                // two even-offset resting levels regardless of `depth` parity.
                int midOffset = (depth - 1) | 1;
                long midPx = basePx - midOffset;
                engine.Submit(MakeOrder(Side.Buy, OrderType.Limit, TimeInForce.Day, midPx, 100, firm: 7));
                iterations++;
            }
            break;

        case "no-cross":
            while (deadline.Elapsed < duration)
            {
                engine.ResetForChannelReset();
                sink.Reset();
                for (int i = 0; i < depth; i++)
                {
                    engine.Submit(MakeOrder(Side.Buy, OrderType.Limit, TimeInForce.Day, basePx - i, 100, firm: 7));
                }
                iterations++;
            }
            break;

        default:
            throw new ArgumentException($"unknown scenario '{scenario}'");
    }
    return iterations;

    static NewOrderCommand MakeOrder(Side side, OrderType type, TimeInForce tif, long px, long qty, byte firm)
        => new(ClOrdId: "P", SecurityId: BenchInstruments.PetrSecId, Side: side, Type: type, Tif: tif,
            PriceMantissa: px, Quantity: qty, EnteringFirm: firm, EnteredAtNanos: 0);
}
