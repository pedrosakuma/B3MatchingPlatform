# ADR 0010 — Post-trade orchestration boundary

- **Status:** Accepted (issue #380, PR refactor/380-post-trade-boundary).
- **Date:** 2026-05-20
- **Supersedes:** —
- **Superseded by:** —
- **Amends:** [ADR 0001](0001-post-trade-boundary-and-eod-file-export.md)
  (the original PostTrade-vs-Core boundary that this ADR tightens).
- **Related ADRs:**
  [ADR 0001](0001-post-trade-boundary-and-eod-file-export.md) (PostTrade
  scope), [ADR 0008](0008-late-corrections-and-bust-propagation.md)
  (bust validation + post-EOD routing this ADR refactors),
  [ADR 0009](0009-single-writer-threading-model.md) (the
  single-writer invariant the orchestrator preserves).
- **Related issues:**
  [#380](https://github.com/pedrosakuma/B3MatchingPlatform/issues/380)
  (post-trade boundary drift — this ADR's driver).

## Context

ADR 0001 declared a clean boundary: `B3.Exchange.PostTrade` owns audit
files, dedup, EOD exports and amendments; `B3.Exchange.Core`
(`ChannelDispatcher`) owns wire framing, sequence numbering and the
matching-engine command loop. Over time the bust pipeline (ADR 0008)
crept across the line. The dispatcher held five PostTrade-typed
fields directly — `_auditRootDir`, `_bustDedup`, `_dropRootDir`,
`_amendmentsPublisher`, `_postTradeSink` — and inlined ~90 lines of
orchestration: validation, file-existence checks for the
`fills.csv.done` sidecar, conditional pre-EOD vs post-EOD routing,
audit / dedup writes under a shared lock, and amendments republish
with its own error-swallowing branch. Three concrete problems
followed:

1. **Two places to change.** Adding a new bust outcome (or moving the
   routing rule) meant touching both `BustValidator` (PostTrade) and
   `ChannelDispatcher.Operator.ProcessOperatorBustV2` (Core).
2. **No unit-testable surface for the orchestration.** The validator
   was unit-tested; the routing decision was only exercised through
   end-to-end dispatcher tests, so the post-EOD branch's amendments
   error handler had no narrow regression suite.
3. **EOD lock leaked.** `PostTradeRoutingLock` lived on the
   dispatcher as a public property so `ExchangeHost.TriggerEodExport`
   could share it. The lock's purpose is post-trade; its owner was
   Core.

## Decision

Introduce a narrow port in `B3.Exchange.PostTrade`:

```csharp
public interface IPostTradeOrchestrator
{
    object RoutingLock { get; }
    BustProcessOutcome ProcessBust(in BustRequest request,
                                   byte channel,
                                   int tradeDateDaysSinceEpoch);
}
```

The default implementation, `PostTradeOrchestrator`, composes
`BustValidator`, `BustDedupIndex`, `IPostTradeSink` and
`IAmendmentsPublisher`, and owns the routing monitor. Every disk-visible
state change happens inside `ProcessBust`, under `RoutingLock`.

`ChannelDispatcher` keeps:

- The matching-engine command loop and the `AssertOnLoopThread`
  invariant (ADR 0009).
- `IPostTradeSink` for the trade-event hot path (`OnTrade` is gated by
  `_bootAuditDurableSeq` during WAL replay — a concern intrinsic to
  the dispatcher's persistence lifecycle, not to bust orchestration).
- The wire-emission half of `ProcessOperatorBustV2`: range-check the
  `LocalMktDate`, call `_postTradeOrch.ProcessBust`, and — on
  `Accept` — write the `TradeBust_57` frame and flush the packet.
- A `PostTradeRoutingLock` public property, now a delegation that
  returns `_postTradeOrch.RoutingLock` (or a private legacy monitor
  when no orchestrator is wired). The property is preserved so
  `ExchangeHost.TriggerEodExport` keeps working without a host-side
  refactor; new code should resolve the orchestrator directly.

`ChannelDispatcherOptions` gains a `PostTradeOrchestrator` property.
For backward compatibility, the dispatcher continues to accept the
five inline properties (`PostTradeSink`, `AuditRootDir`, `BustDedup`,
`DropRootDir`, `AmendmentsPublisher`); when an explicit orchestrator
is not supplied but the inline triple `PostTradeSink + AuditRootDir +
BustDedup` is present, the dispatcher auto-composes a default
orchestrator. This keeps every existing call site (host + tests)
working unchanged and limits the blast radius of the refactor.

## Consequences

**Positive**
- One place to change orchestration logic; one place to test it.
  `PostTradeOrchestratorTests` covers every validator outcome × pre-EOD/
  post-EOD × amendments-failure permutation in isolation.
- The dispatcher's `ProcessOperatorBustV2` shrinks from ~150 LOC to
  ~50 LOC and reads as a wire-emission step (matching the dispatcher's
  responsibility per ADR 0001).
- `RoutingLock` ownership moves to PostTrade where the concept lives.

**Neutral**
- The five legacy options on `ChannelDispatcherOptions` remain valid
  for backward compatibility. They can be removed in a future change
  once every host call site migrates to constructing
  `PostTradeOrchestrator` explicitly; that is out of scope for #380.

**Negative**
- The auto-composition path means the host can construct an orchestrator
  three different ways (explicit, inline triple, or nothing → null).
  Mitigated by the option's XML doc and the orchestrator constructor's
  required arg validation.

## Alternatives considered

1. **Move audit/dedup/bust state directly into a PostTrade façade and
   inject it as the only post-trade dep.** Rejected for this PR: it
   would force every host call site and test fixture to migrate
   simultaneously, ballooning the diff and risking subtle behavioural
   drift. The opt-in `PostTradeOrchestrator` option + auto-composition
   path captures 100% of the boundary clarity without the migration
   churn. A follow-up issue can deprecate the inline options once the
   ecosystem has stabilised.
2. **Keep the lock on the dispatcher but extract only the validation +
   write logic.** Rejected because the lock _is_ the post-trade
   routing concern; leaving it in Core would re-create the same
   leak in the next feature.
3. **Inline everything into a single `BustPipeline` static helper
   and pass the state as parameters.** Rejected because the routing
   lock and dedup index must be per-channel singletons; a static
   helper would push that ownership back onto the caller.

## Implementation notes

- `_postTradeSink` stays on the dispatcher because the trade-event
  hot path (`Sinks.cs:479` `_postTradeSink.OnTrade(record)`) is gated
  by `_bootAuditDurableSeq`, a WAL-replay invariant that lives
  exclusively on the dispatcher. Routing that call through the
  orchestrator would force the orchestrator to know about WAL replay
  state, recreating the boundary smear from the opposite direction.
- The orchestrator's `ProcessBust` is synchronous and thread-affine:
  the dispatcher calls it from the loop thread, so the orchestrator's
  internal mutations (dedup add, sink write) inherit the
  single-writer guarantee from ADR 0009. The `RoutingLock` exists
  for cross-thread synchronization with the EOD exporter, _not_ to
  protect the orchestrator's own state. Callers outside the
  dispatcher loop MUST serialize their own invocations; the
  interface documents this contract explicitly.
- The post-EOD amendments republish is split across two orchestrator
  calls so the dispatcher can preserve the legacy
  audit → UMDF emit → amendments file ordering (ADR 0008 §4):
  `ProcessBust` performs the audit / dedup write and signals
  `IsPostEodAccept = true` on the outcome; the dispatcher then emits
  and flushes the `TradeBust_57` frame; only after a successful flush
  does the dispatcher invoke `PublishPostEodAmendments`. This means
  a frame-emit failure cannot leave `amendments.csv` announcing a
  bust that never reached consumers. Idempotent-replay republish
  stays inline inside `ProcessBust` because no UMDF frame follows on
  that branch.
