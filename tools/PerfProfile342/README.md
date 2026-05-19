# PerfProfile342

Standalone matching-engine profiling harness produced for the
[`#342` SortedDictionary go/no-go gate](https://github.com/pedrosakuma/B3MatchingPlatform/issues/342)
and kept for future operators who want to rerun the gate (or any similar
container-swap proposal) against a long-lived process.

It runs one of three matching workloads in a tight loop for a fixed
wall-clock duration so an external profiler (`dotnet-trace`, `perf`)
can attach to a process whose CPU is dominated by `MatchingEngine` work.

## Scenarios

| Scenario     | What it does                                                                       | Mirrors                              |
| ------------ | ---------------------------------------------------------------------------------- | ------------------------------------ |
| `full-cross` | Builds a depth-N book, then a single aggressor sweeps the whole side               | `MatchingBenchmarks.NewOrder_FullCross` |
| `mid-depth`  | Builds a depth-N book (even ticks), then POSTs a single resting order at mid-book  | quote-stuffing / mid-book POST flow  |
| `no-cross`   | Inserts N resting orders at descending prices on an empty book                     | `MatchingBenchmarks.NewOrder_NoCross`   |

Each iteration calls `engine.ResetForChannelReset()` so memory stays bounded.

## Usage

```bash
dotnet build tools/PerfProfile342/PerfProfile342.csproj -c Release --nologo

# Pick a scenario and run for 35s (3s warmup + 30s window)
dotnet tools/PerfProfile342/bin/Release/net10.0/PerfProfile342.dll \
  full-cross --depth 100 --seconds 35 &
sleep 5

PID=$(pgrep -f PerfProfile342)
dotnet-trace collect -p "$PID" --duration 00:00:30 \
  --providers Microsoft-DotNETCore-SampleProfiler \
  --format Speedscope \
  -o profiles/full-cross-100.nettrace

wait
dotnet-trace report profiles/full-cross-100.nettrace topN -n 30
```

CLI flags: `--depth N` (default 100), `--seconds S` (default 30).

## Go/no-go methodology used for #342

The issue's GO threshold was: any of
`SortedDictionary.MoveNext`, `SortedDictionary+Enumerator..ctor` or
`Comparer<long>.Compare` exceeds **5% of engine CPU** at production-like
depth (100–1000 levels) in the matching path. Below that threshold the
container is not the bottleneck and the swap to a `List<PriceLevel>`
(`B3MarketDataPlatform/src/B3.Umdf.Book/BookSide.cs` pattern) is not worth
the extra complexity (FIFO preservation, iceberg replenishment,
`_byOrderId` integrity).

The captured traces showed none of those frames appearing at all, and the
only SortedDictionary-touching matching frame (`LimitOrderBook.OppositeLevels`,
which copies the SortedDictionary into `_oppositeScratch` for the cross
walk) capped at **1.71%** on the worst scenario — a robust NO-GO. See
the [#342 closing comment](https://github.com/pedrosakuma/B3MatchingPlatform/issues/342#issuecomment-4489820301)
for the full per-scenario top-N tables.

## Known caveat: PollGCWorker noise

`Thread.<PollGC>g__PollGCWorker` shows up at ~75% exclusive in every
`SampleProfiler` trace. That is a known limitation of the runtime
sampler failing to unwind through JIT-inserted GC safe-points — those
samples are real engine work that the profiler couldn't attribute. Even
redistributed proportionally over the named engine frames it does not
move SortedDictionary above 5%.

For a higher-fidelity profile use Linux `perf` with
`DOTNET_PerfMapEnabled=1` and `DOTNET_EnableEventPipe=1` exported in the
target process's environment. Not available on the WSL2 kernel used when
#342 was profiled, which is why the gate was decided on the
SampleProfiler evidence + the structural fact that `OppositeLevels` is
the only frame that can ever cross a SortedDictionary boundary.
