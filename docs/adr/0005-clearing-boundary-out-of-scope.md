# ADR 0005 — Clearing boundary: no clearing module

- **Status:** Rejected (explicit non-feature)
- **Date:** 2026-05-19
- **Related RFC:** [RFC 0001 — Post-trade architecture](../rfc/0001-post-trade-architecture.md), §8 (roadmap row), §9 (out of scope).
- **Related issue:** [#335](https://github.com/pedrosakuma/B3MatchingPlatform/issues/335)
- **Related ADRs:** [ADR 0004](0004-settlement-cycle-out-of-scope.md) (decided in tandem — settlement is also out of scope).

## 1. Context

[RFC 0001 §9](../rfc/0001-post-trade-architecture.md) lists "active
clearing/settlement" as out of scope. The roadmap entry for ADR 0005
in [RFC §8](../rfc/0001-post-trade-architecture.md#8-adr-roadmap) is
explicit that it is coupled with ADR 0004 ("likely decided together")
with the most-likely outcome aligned with §9: no `B3.Exchange.Clearing`
module exists.

This ADR records that decision so the absence of a clearing module
is read as deliberate, not as unfinished work.

## 2. Decision

**No clearing module exists.** There is no `B3.Exchange.Clearing`
project, no novation step, no CCP risk surface, no margining, no
guarantee-fund modelling, no give-up / give-in flow.

Post-trade's responsibility ends at the [ADR 0001](0001-post-trade-boundary-and-eod-file-export.md)
EOD fills drop and the [ADR 0002](0002-per-trade-audit-log-shape.md)
audit log. A *clearing simulator* — if one ever exists — lives in a
separate repository, consumes those artifacts as its input, and
owns its own state.

This ADR is paired with [ADR 0004](0004-settlement-cycle-out-of-scope.md):
clearing without settlement (or vice versa) is incoherent, so
reopening either requires reopening both.

**Trigger to revisit:** a concrete consumer needing CCP/clearing
behaviour *against the simulator itself* (not against an external
clearing simulator that consumes our artifacts).

## 3. Consequences

- No new module, no new namespace, no new artifact.
- The "Future surveillance / clearing" row of [RFC §4](../rfc/0001-post-trade-architecture.md#4-integration-channel-matrix)
  remains "their own simulators / stubs" — there is no first-party
  clearing simulator in this repo.
- The matching engine continues to be the only authoritative
  trade-creating component. Trade bust ([ADR 0008](0008-late-corrections-and-bust-propagation.md))
  is the only operator-initiated mutation of a trade after the fact;
  there is no CCP-driven novation that would also need such a path.
- Identifiers tied to clearing (`clearingMemberId`, novated
  `tradeId`, position-account, margin call id, etc.) are not
  allocated and do not appear in any artifact.
- If a future ADR reverses this, it must define at minimum:
  (a) where the clearing boundary sits relative to the existing
  matching/post-trade split,
  (b) whether novation happens in-process or in a separate process,
  (c) how the cleared `tradeId` relates to the matched `tradeId`,
  (d) coupling with [ADR 0004](0004-settlement-cycle-out-of-scope.md)
  (settlement must also be reopened), and
  (e) impact on the audit-log shape from [ADR 0002 §1](0002-per-trade-audit-log-shape.md).

## 4. Alternatives considered

- **Ship an empty `B3.Exchange.Clearing` stub project.** Rejected:
  empty modules attract speculative code; the absence is the point.
- **Defer (status Deferred) rather than Reject.** Rejected: identical
  reasoning to [ADR 0004 §4](0004-settlement-cycle-out-of-scope.md#4-alternatives-considered).

## 5. Open questions

None.
