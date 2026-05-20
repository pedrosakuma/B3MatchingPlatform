# Contributing to B3MatchingPlatform

Short, opinionated guide for humans and agents touching this codebase.

## Pre-PR checklist

Run these locally before opening a PR. The Release `dotnet build` /
`dotnet test` and `dotnet format` invocations match the required CI
checks (`Build & Test` and `Format check`). The Debug build/test
mirror the new `Build & Test (Debug)` lane — that job exists in CI
but is not yet a *required* check (see the comment in
`.github/workflows/ci.yml`); run it locally anyway so a Debug-only
regression doesn't surface as a red lane on your PR. CI itself adds
loggers, coverage, and `--blame-hang-timeout` on top of these — those
flags don't change pass/fail, only artifact shape.

```bash
dotnet restore SbeB3Exchange.slnx
dotnet build   SbeB3Exchange.slnx --no-restore -c Release
dotnet test    SbeB3Exchange.slnx --no-build   -c Release
dotnet build   SbeB3Exchange.slnx --no-restore -c Debug
dotnet test    SbeB3Exchange.slnx --no-build   -c Debug
dotnet format  SbeB3Exchange.slnx --verify-no-changes --no-restore --severity warn
```

`TreatWarningsAsErrors=true` is on globally — a new warning fails the
build. If `dotnet format` reports diffs, run it without
`--verify-no-changes` to apply the fixes, then re-run the verification.

## Threading model (read this before touching the dispatcher or engine)

Per-channel state — `MatchingEngine`, `ChannelDispatcher` buffers and
sequence counters, audit writer, dedup index, snapshot cadence — is
**single-writer**. Exactly one thread (the dispatcher's `LongRunning`
loop thread) is allowed to mutate it. There is no lock; the assumption
is what makes the hot path lock-free.

The invariant is documented and decided in
[ADR 0009](docs/adr/0009-single-writer-threading-model.md). Two recent
bugs (#374 dispatcher async-loop continuation hop, #375 test-thread hop
after `await Task.Delay`) violated it; both compiled and ran green in
Release but tripped the Debug assert on every run.

Rules of thumb when working in this area:

- **Never `await` inside the dispatch loop.** Use sync `Task.Wait(timeout, ct)`
  or `.GetAwaiter().GetResult()` from the loop thread. An `await` lets
  the continuation hop to a pool thread, and on a busy pool every
  `await` is a coin flip.
- **Do not `.Unwrap()` an async lambda passed to
  `Task.Factory.StartNew(..., LongRunning)`.** That reintroduces the
  same continuation-hop problem. Pass a synchronous `Action`.
- **Cross-thread producers go through `ChannelDispatcher.EnqueueXxx`.**
  TCP/FIXP sessions, the HTTP admin server, persistence callbacks, and
  tests never call engine or dispatcher mutation methods directly. The
  bounded `Channel<T>` is the only handoff.
- **Add `AssertOnOwnerThread()` / `AssertOnLoopThread()` to any new
  mutation entry point.** It is the first statement of the method.
  These asserts are `[Conditional("DEBUG")]` so they have zero
  Release-mode cost; the Debug CI lane runs them on every PR.
- **Tests must respect the invariant too.** If a test calls
  `TestProbe.DrainInbound` more than once, do not put `await Task.Delay`
  or any other continuation point between drains — the engine has
  latched to the test thread, and the second drain on a different pool
  thread will trip the assert. Use the sync `WaitFor(Func<bool>, TimeSpan)`
  helper to wait for background conditions between drains.

The two CI lanes catch different things:

- `Build & Test` (Release) — wire correctness, performance-sensitive
  paths compiled the way production runs.
- `Build & Test (Debug)` — `Debug.Assert` enforces single-writer
  invariants that are compiled out in Release.

Both must pass before merge.

## PR flow

- Branch naming: `fix/<issue>-<slug>`, `feat/<slug>`, `docs/<slug>`,
  `ci/<slug>`, `refactor/<slug>`. Issue numbers in the branch name help
  reviewers wire context fast.
- Open PRs as **draft** while iterating. Mark ready only when the
  pre-PR checklist passes locally.
- Squash-merge by default (the repo's auto-merge rule uses squash).
  Keep the squash commit message self-contained — the merged history is
  what future readers will see.
- A merged PR that closes an issue must link it via `Closes #N` in the
  PR body, not just the commit message.

## ADRs vs RFCs vs runbook vs code comments

- **ADRs** (`docs/adr/`) record decisions that are expensive to reverse:
  module boundaries, on-disk formats, protocol surfaces, threading
  model. One ADR per file, numbered sequentially. See
  [docs/adr/README.md](docs/adr/README.md) for the lifecycle.
- **RFCs** (`docs/rfc/`) are umbrella architectural plans that may spawn
  multiple ADRs. The post-trade architecture (RFC 0001) is the only one
  so far.
- **Runbook** (`docs/RUNBOOK.md`) is operational: how to halt a
  channel, how to read metrics, how to recover from a crashed
  dispatcher. Cite ADRs where they explain *why* a procedure exists.
- **Code comments** cite the ADR or issue number (`// See ADR 0009`,
  `// issue #374`) rather than repeating the rationale. The ADR is the
  source of truth; the comment is a pointer.

## Schemas are vendored

Do not hand-edit files under `schemas/`. When upgrading B3 EntryPoint
or UMDF, regenerate the SBE bindings in `B3.*.Sbe` and mirror the
change in the companion `SbeB3UmdfConsumer` repo. The two repos must
agree on the schema version.
