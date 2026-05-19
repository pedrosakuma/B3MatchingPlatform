# ADR 0004 — Settlement cycle is out of scope

- **Status:** Rejected (explicit non-feature)
- **Date:** 2026-05-19
- **Related RFC:** [RFC 0001 — Post-trade architecture](../rfc/0001-post-trade-architecture.md), §5 (identity), §7 (failure modes), §8 (roadmap row), §9 (out of scope).
- **Related issue:** [#334](https://github.com/pedrosakuma/B3MatchingPlatform/issues/334)
- **Related ADRs:** [ADR 0005](0005-clearing-boundary-out-of-scope.md) (coupled — clearing is also out of scope).

## 1. Context

[RFC 0001 §9](../rfc/0001-post-trade-architecture.md) already lists
"active clearing/settlement" as out of scope for the simulator. RFC
§4 catalogs a hypothetical per-firm settlement-instruction file with
the explicit note that ADR 0004 "may also decide *not* to ship". RFC
§5 lists `settlementId` and `confirmationId` as **not modelled**.

This ADR records the *not modelled* answer as a deliberate decision
rather than an absence, so future contributors do not assume
settlement features are merely unimplemented and ripe for picking up.

## 2. Decision

**The simulator does not model a settlement cycle.** No
settlement-instruction file is emitted; no `settlementId` is
allocated; no T+N business-date function is shipped; no
fail-to-deliver state machine exists; no admin endpoint accepts
settlement-related commands.

`B3MatchingPlatform`'s post-trade responsibility ends at the EOD
fills drop ([ADR 0001](0001-post-trade-boundary-and-eod-file-export.md))
and the audit log it projects from ([ADR 0002](0002-per-trade-audit-log-shape.md)).
Anything that consumes those artifacts to drive settlement is the
*consumer's* problem and lives in a different repository.

**Trigger to revisit:** a concrete downstream that needs to test a
DvP, novation, or fail-to-deliver scenario *against the simulator*
(rather than against a clearing simulator of its own). Until then
this ADR holds.

## 3. Consequences

- No `B3.Exchange.Settlement` module exists or is planned.
- No new failure modes enter [RFC 0001 §7](../rfc/0001-post-trade-architecture.md#7-failure-modes-and-deferred-decisions);
  the existing "Future ADR 0004 introduces fail-to-deliver scenarios"
  row is closed by this ADR (the answer is "no such scenarios").
- The "Settlement instruction file" row in [RFC 0001 §4](../rfc/0001-post-trade-architecture.md#4-integration-channel-matrix)
  is decided: not emitted.
- `settlementId` stays absent from every artifact: audit log,
  fills.csv, ER, UMDF, admin endpoints.
- If a future ADR ever reverses this, it must define at minimum:
  (a) `settlementId` allocation and its relation to `tradeId`
  (1:1 baseline; 1:N only if netting is also modelled),
  (b) São-Paulo business-date function (RFC §6 bullet),
  (c) per-firm settlement-instruction file format,
  (d) fail-to-deliver failure modes, and
  (e) coupling with [ADR 0005](0005-clearing-boundary-out-of-scope.md)
  (clearing must also be reopened — settlement without clearing is
  incoherent).

## 4. Alternatives considered

- **Ship a token T+2 settlementId allocator.** Rejected: an identifier
  with no associated process or file is dead weight; downstream cannot
  use it; it would surface in audit / ER / UMDF for no purpose.
- **Defer the decision (status Deferred).** Rejected: deferring
  invites speculative work and recurring "should we do this now?"
  conversations. RFC §9 has already drawn the line; this ADR makes
  it explicit.

## 5. Open questions

None.
