# ADR 0009 — Single-writer threading model for per-channel state

- **Status:** Accepted (retroactively documents the model in place since
  issue #169 / #138 and reinforced by the #374 / #375 fixes and the
  PR #377 Debug CI lane; written now so future work has a stable
  reference instead of reverse-engineering the dispatcher and the
  matching engine.)
- **Date:** 2026-05-20
- **Supersedes:** —
- **Superseded by:** —
- **Part of:** core infrastructure (not under RFC 0001 — applies to the
  whole matching/dispatch stack, not just post-trade).
- **Related ADRs:** [ADR 0002](0002-per-trade-audit-log-shape.md)
  (audit writer is one of the single-writer-owned resources).
- **Related issues:**
  [#169](https://github.com/pedrosakuma/B3MatchingPlatform/issues/169)
  (matching-engine single-thread invariant + per-call assert),
  [#138](https://github.com/pedrosakuma/B3MatchingPlatform/issues/138)
  (dispatcher single-writer invariant + `AssertOnLoopThread`),
  [#374](https://github.com/pedrosakuma/B3MatchingPlatform/issues/374)
  (sync `RunLoop` replaces async `RunLoopAsync` to keep the invariant),
  [#375](https://github.com/pedrosakuma/B3MatchingPlatform/issues/375)
  (test pattern that hopped pool threads between drains),
  [#377](https://github.com/pedrosakuma/B3MatchingPlatform/pull/377)
  (Debug CI lane that enforces the invariant on every PR).

## Context

Each UMDF channel owns a graph of mutable state:

- the per-channel `MatchingEngine` (books, order/trade id allocators,
  `RptSeq`),
- the `ChannelDispatcher` packet buffer, sequence counters
  (`SequenceNumber`, `SequenceVersion`), and per-session
  `(EnteringFirm, ClOrdID) → OrderID` index,
- the audit log writer, dedup index, and snapshot cadence state.

All of it is `[NotThreadSafe]` by design: there are no locks on the hot
path, the buffers are pooled and reused, and the audit/snapshot writers
assume an append-only single producer. The performance and correctness
arguments only hold while exactly one thread mutates this graph.

That thread exists: `ChannelDispatcher` runs a dedicated loop thread
(spawned via `Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)`)
that pumps the bounded inbound channel, calls into the engine, and
flushes the packet buffer. Inbound producers (TCP/FIXP sessions, the
HTTP admin server, persistence replay) marshal work onto it through
`EnqueueXxx` methods backed by a `Channel<T>`.

The invariant is **enforced**, not just documented:

- `MatchingEngine.AssertOnOwnerThread` (`src/B3.Exchange.Matching/MatchingEngine.cs:1933-1960`)
  lazy-latches the first calling thread and trips on every subsequent
  cross-thread call. `BindToDispatchThread` lets the dispatcher latch
  eagerly at startup.
- `ChannelDispatcher.AssertOnLoopThread`
  (`src/B3.Exchange.Core/ChannelDispatcher.Lifecycle.cs:98-109`) is the
  same guard for dispatcher-owned state.

Both asserts are `[Conditional("DEBUG")]`, so they compile out in
Release. Release CI was therefore blind to the whole class for months —
two latent bugs landed even though they tripped these asserts on every
Debug run:

- #374 — `ChannelDispatcher.RunLoopAsync` used
  `await reader.WaitToReadAsync(ct).AsTask().WaitAsync(timeout)`; after
  every idle wait the continuation could resume on a pool thread. The
  rest of the loop body (and any engine call it made) then ran on the
  wrong thread.
- #375 — Test pattern `DrainInbound → await Task.Delay → DrainInbound`:
  the engine latched to the test thread on the first drain, the `await`
  hopped to a pool thread, the second drain re-entered the engine and
  tripped the assert.

Both were caught only in retrospect. PR #377 added a parallel CI lane
that builds and tests under `-c Debug` so this family of bug fails fast
at PR time.

## Decision

1. **One owner thread per channel.** Per-channel mutable state
   (matching engine, dispatcher buffers and counters, audit writer,
   dedup index, snapshot cadence) is mutated only from the
   `ChannelDispatcher` loop thread. There is no exception, including
   "just a read", "just metrics", "just for tests".

2. **The loop runs synchronously, not async.** `ChannelDispatcher.RunLoop`
   blocks on `Task.Wait(timeout, ct)` to combine the inbound wait with
   the heartbeat tick. We do **not** `await` inside the loop, and we do
   **not** `.Unwrap()` an async lambda passed to
   `Task.Factory.StartNew(..., LongRunning)` — both reintroduce the
   continuation-hops-to-pool problem that #374 fixed. If a future
   dependency only exposes an async API, the integration point must
   call `.GetAwaiter().GetResult()` from the loop thread, not `await`.

3. **The loop thread is `LongRunning`.** We spend the whole process
   lifetime in this thread; the `LongRunning` hint tells the scheduler
   to allocate a dedicated thread instead of borrowing a pool worker,
   which keeps the latch stable and avoids pool starvation.

4. **Cross-thread producers go through the channel.** TCP/FIXP
   sessions, the HTTP admin server, persistence callbacks, and tests
   all enqueue via `EnqueueXxx` on `ChannelDispatcher`. They never
   call engine or dispatcher mutation methods directly. The bounded
   `Channel<T>` is the only handoff.

5. **The invariant is asserted in Debug, enforced by Debug CI.**
   `AssertOnOwnerThread` / `AssertOnLoopThread` stay
   `[Conditional("DEBUG")]` for hot-path cost reasons, and the
   `build-test-debug` lane in `.github/workflows/ci.yml` runs the full
   test suite under `-c Debug` on every PR so a regression cannot
   merge silently.

6. **Tests respect the invariant too.** `TestProbe.DrainInbound`
   processes items on the caller's thread. Tests that call it must
   stay on a single thread: no `await Task.Delay` (or any other
   continuation point) between drains, no `Task.Run` wrapping engine
   calls. If a test needs to wait for a background condition between
   drains it uses the sync `WaitFor(Func<bool>, TimeSpan)` helper
   (sleeps on the test thread) rather than the async `WaitForAsync`,
   so the latch stays intact.

## Consequences

- The hot path stays lock-free: no `Monitor` and no `Interlocked` on
  per-channel state on the write side. Throughput stays at the
  Release-mode numbers RFC 0001 §3 budgets for.
- Adding a new mutation entry point on the engine or dispatcher is a
  three-line cost: place `AssertOnOwnerThread()` / `AssertOnLoopThread()`
  as the first statement of the method. No new locking, no rework of
  callers.
- The Debug CI lane roughly doubles CI wall-clock on PRs (still under
  the 20-minute job budget). We accept that cost in exchange for
  catching the entire single-writer bug family at PR time instead of
  in production-shaped E2E tests weeks later.
- Cross-channel state (e.g. host-wide registries, metrics scraped by
  the HTTP server, persistence-replay state machines) is **out of
  scope** for this ADR — those use their own locks or
  `Interlocked`/`Volatile` and are reviewed case by case. This ADR
  governs the per-channel hot path only.
- One controlled exception lives inside the per-channel surface: the
  HTTP-thread reads of `ChannelDispatcher.SequenceNumber` and
  `SequenceVersion` (used by `HttpServer.RenderProm` and similar
  scrapes). The loop thread is still the only writer, but external
  readers are allowed; the writes use `Volatile.Write` and the public
  getters use `Volatile.Read` against the backing fields so weak
  memory models (ARM64, AOT) cannot hoist or tear. See
  `ChannelDispatcher.cs:91-105` and the class-level XML doc. The
  pattern is opt-in per-field: do not generalize it to other state
  without an explicit ADR amendment.

## Amendment 2026-05-20 — Scope beyond engine/dispatcher (issue #384)

The original decision named two concrete owners — `MatchingEngine` and
`ChannelDispatcher` — and the asserts originally lived as `private`
methods on those two classes only. A 2026-05-20 architectural review
flagged that **other** per-channel single-writer components silently
drifted away from the rule: most notably
`B3.Exchange.PostTrade.FileAuditLogWriter`, which took out an
`_stateLock` because `Checkpoint()` is invoked from the
`BackgroundSnapshotWriter` thread rather than the dispatch loop. The
lock is correct given that call shape, but it leaks back-pressure into
`OnTrade` and silently re-introduces locking onto the hot path the
rest of the channel is engineered to avoid.

Decision:

1. **The single-writer invariant applies to every per-channel
   component, not just engine + dispatcher.** Examples covered by the
   amendment: `FileAuditLogWriter`, `BustDedupIndex`, the future
   `IAmendmentsPublisher`, and any other type whose state is mutated
   on behalf of one channel.

2. **The hand-rolled asserts are extracted into a shared
   `B3.Exchange.Matching.Threading.SingleWriterGuard` helper.**
   `MatchingEngine` and `ChannelDispatcher` now hold a private guard
   instance and delegate `BindToDispatchThread` /
   `AssertOnOwnerThread` / `AssertOnLoopThread` to it; semantics are
   unchanged (lazy-latch for direct-drive tests, eager-bind from the
   production loop, `[Conditional("DEBUG")]` for hot-path cost).

3. **Components whose current call shape requires a lock document the
   exception and have a tracked migration.** `FileAuditLogWriter`'s
   `_stateLock` stays for now (its removal requires marshaling
   `Checkpoint()` through the dispatch inbox, a separate redesign);
   it is tracked in a follow-up issue. The contract for *new*
   components is "guard, not lock" — adding a new per-channel
   component that takes a lock requires an explicit ADR amendment
   citing why the dispatch-thread handoff is impractical.

4. **`SingleWriterGuard.RebindForReplay()` is the only sanctioned way
   to clear the owner latch.** Snapshot-replay paths that rebuild
   state on a thread different from the eventual dispatch loop must
   call it; any other re-bind is a smell.

Trade-off accepted: a thin extra layer of indirection on every assert
(`SingleWriterGuard.AssertOwnedByCurrentThread` → the same
`Interlocked.CompareExchange` + `Debug.Assert` as before). The whole
path is `[Conditional("DEBUG")]` so Release builds are byte-identical
to the prior implementation.

## Alternatives considered

- **Locks around engine/dispatcher state.** Rejected: the engine is on
  the critical hot path (sub-microsecond per-order budget); per-call
  `Monitor.Enter` would dominate latency. The whole architecture is
  built on the single-writer assumption.
- **Async dispatch loop with `ConfigureAwait(false)` discipline.**
  Tried (the pre-#374 implementation). The thread the continuation
  resumes on is a scheduler decision, not an `await` decision; on a
  busy pool every `await` is a coin flip. Cannot satisfy the
  invariant without effectively pinning back to a single thread, at
  which point the sync loop is simpler.
- **Remove the asserts, document the invariant in prose only.**
  Rejected: every regression this ADR is reacting to was caught
  *because* of the asserts. They are the load-bearing test
  infrastructure; CONTRIBUTING.md just teaches new contributors how to
  read them.

## Open questions

- Persistence replay currently runs on the loop thread inside
  `LoadPersistedStateOnLoopThread`. If replay grows beyond what fits in
  the startup window, we may need a second "replay thread" with a
  formal handoff. Out of scope until measured.
