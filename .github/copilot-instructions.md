# Copilot instructions for SbeB3Exchange

Stateful B3 exchange simulator. Inbound: B3 EntryPoint SBE over TCP. Outbound:
B3 UMDF wire format over UDP multicast, plus SBE ExecutionReports back to the
TCP client. Companion to the external `SbeB3UmdfConsumer` repo.

## Toolchain

- .NET SDK pinned in `global.json` (`10.0.201`, `rollForward: latestFeature`).
  Target framework is `net10.0` (set in `Directory.Build.props`).
- `Nullable`, `ImplicitUsings`, and **`TreatWarningsAsErrors=true`** are on
  globally — a new warning fails the build.
- xUnit (`xunit` v2) is the test framework. The `Xunit` namespace is added via
  `<Using Include="Xunit" />` in each test csproj, so test files do not need
  `using Xunit;`.

## Build / test / run

```bash
dotnet build SbeB3Exchange.slnx
dotnet test  SbeB3Exchange.slnx
dotnet run --project src/B3.Exchange.Host -- config/exchange-simulator.json
```

Run a single test project / class / method:

```bash
dotnet test tests/B3.Exchange.Matching.Tests
dotnet test tests/B3.Exchange.Matching.Tests --filter "FullyQualifiedName~MatchingTests"
dotnet test tests/B3.Exchange.Matching.Tests --filter "FullyQualifiedName~MatchingTests.SomeMethodName"
```

### Pre-PR checklist (mirrors required CI checks)

CI on `main` requires `Build & Test` and `Format check` to pass before
auto-merge will complete. Run these locally before opening a PR — they are
the same commands CI runs and they are fast:

```bash
dotnet restore SbeB3Exchange.slnx
dotnet build   SbeB3Exchange.slnx --no-restore -c Release          # warnings-as-errors
dotnet test    SbeB3Exchange.slnx --no-build   -c Release
dotnet format  SbeB3Exchange.slnx --verify-no-changes --no-restore --severity warn
```

If `dotnet format` reports diffs, run it without `--verify-no-changes` to
apply the fixes, then re-run the verification.

Docker:

```bash
docker build -t sbeb3exchange:latest .
docker run --rm --network host -v $(pwd)/config:/app/config sbeb3exchange:latest
```

The host needs host networking for the multicast publish socket and the
EntryPoint TCP listener (default `0.0.0.0:9876`).

## Architecture (big picture)

```
TCP client ─► EntryPointSession ─► HostRouter ─► ChannelDispatcher ─► MatchingEngine
                                    (by SecurityId)        │
                                                           ├─► UMDF MBO/Trade frames ─► UDP multicast
                                                           └─► ExecutionReports ──────► back to TCP client
```

Layered projects under `src/` (build order roughly bottom-up):

1. `B3.EntryPoint.Sbe` / `B3.Umdf.Sbe` — generated SBE bindings from
   `schemas/b3-entrypoint-messages-8.4.2.xml` and
   `schemas/b3-market-data-messages-2.2.0.xml`. Vendored copies; keep in sync
   with `SbeB3UmdfConsumer` when upgrading.
2. `B3.Umdf.WireEncoder` — stateless byte-level encoders for UMDF MBO / Trade /
   Snapshot frames (V16 schema).
3. `B3.Exchange.Instruments` — JSON instrument loader.
4. `B3.Exchange.Matching` — single-thread per-symbol limit order book.
   `MatchingEngine` owns one `LimitOrderBook` per `SecurityId`, monotonic
   order/trade-id allocators, and an `RptSeq` incremented on every emitted
   event. **Not thread-safe by design.**
5. `B3.Exchange.Gateway` — TCP listener, framed SBE inbound decoder,
   ExecutionReport encoder. Each TCP session implements
   `IEntryPointResponseChannel`.
6. `B3.Exchange.Core` — `ChannelDispatcher`: bounded inbound queue +
   single dispatch thread per channel. Pumps decoded commands into the engine,
   buffers emitted UMDF events for the duration of one command, then flushes
   them as a single packet (≤1400 bytes, `PacketHeader` + N inc messages) to
   `IUmdfPacketSink`.
7. `B3.Exchange.Host` — JSON-configured single-binary host wiring the
   EntryPoint listener + dispatchers + `MulticastUdpPacketSink`s.

## Key conventions / gotchas

- **Single-threaded engine, per channel.** `MatchingEngine` and the
  `IMatchingEventSink` callbacks always run on the `ChannelDispatcher`'s
  dedicated dispatch thread. Never call into the engine from another thread;
  enqueue a command instead.
- **Channel = `SecurityId` partition.** `HostRouter` routes commands to a
  dispatcher by the order's `SecurityId`. One matching engine and one outbound
  multicast group per UMDF channel.
- **`orderId → reply-channel` map.** `ChannelDispatcher` keeps an
  `OrderOwnership` map so PASSIVE-side execution reports (a resting order
  filled by another session's aggressor) route back to the originating TCP
  session.
- **One UMDF packet per command.** Events emitted while processing a single
  command are packed into one UMDF packet with a monotonic `SequenceNumber`,
  not flushed individually.
- **Prices are implicit /10000 mantissas** on the wire (see the worked example
  in `docs/EXCHANGE-SIMULATOR.md`).
- **Cancel/Replace lookup:** `Cancel` and `Modify` accept either an explicit
  engine `OrderID` or `OrigClOrdID`. The `(EnteringFirm, ClOrdID) → OrderID`
  index lives in `ChannelDispatcher` and is mutated only on the dispatch thread.
- **Single-tenant:** every TCP session shares `tcp.enteringFirm` from config;
  per-session firm assignment is not implemented.
- **Schemas are vendored.** Do not hand-edit files under `schemas/`; regenerate
  the SBE bindings in `B3.*.Sbe` when upgrading and mirror the change in
  `SbeB3UmdfConsumer`.

## Configuration

`config/exchange-simulator.json` — `tcp.listen`, `tcp.enteringFirm`, and a
`channels[]` array. Each channel entry binds a UMDF channel number to a
multicast group/port/TTL and an instrument list (re-uses the format consumed
by `B3.Exchange.Instruments.InstrumentLoader`). See
`docs/EXCHANGE-SIMULATOR.md` for the full schema and a Python sample that
sends a `SimpleNewOrderV2` over `nc`.
