# B3.Exchange.Bench — performance baselines

BenchmarkDotNet harness for the simulator's hot paths. See issue #118.

## Scope

| Class | What it measures |
|---|---|
| `MatchingBenchmarks` | `MatchingEngine.Submit` (NewOrder no-cross + full sweep across N levels) and `Cancel` by orderId. |
| `UmdfEncoderBenchmarks` | Single-frame UMDF encode (PacketHeader, OrderAdded, OrderDeleted, Trade). |

The matching benchmarks use a `NoOpSink` to keep the measurement focused on
engine work, and `IterationSetup` to give each invocation a clean book.

## Run locally

```bash
# all benchmarks
dotnet run -c Release --project bench/B3.Exchange.Bench -- --filter '*'

# one class
dotnet run -c Release --project bench/B3.Exchange.Bench -- --filter '*Matching*'

# list available
dotnet run -c Release --project bench/B3.Exchange.Bench -- --list flat
```

Reports are written under `BenchmarkDotNet.Artifacts/results/` (markdown +
CSV + GitHub-flavoured md).

## CI

`.github/workflows/bench-nightly.yml` runs the full suite on a schedule
(02:00 UTC) and uploads `BenchmarkDotNet.Artifacts/` as a build artifact for
trend-tracking. The job is also dispatchable manually
(`workflow_dispatch`) with an optional `filter` input.

This is **baseline-only** today: no failure gate. Once a few nightly runs
have established stable numbers, a regression threshold can be added in a
follow-up.

## Out of scope (for now)

- E2E `ChannelDispatcher` loopback throughput (deferred — needs a stand-alone
  no-op packet sink and synthetic command generator without TCP).
- SBE *decode* benchmarks for inbound EntryPoint frames (deferred — requires
  a representative pre-encoded byte corpus).
- GC profiling beyond what `MemoryDiagnoser` reports.
