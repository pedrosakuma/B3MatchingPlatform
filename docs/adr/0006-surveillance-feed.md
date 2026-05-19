# ADR 0006 — Surveillance feed: trade-only, no new artifact in v1

- **Status:** Accepted
- **Date:** 2026-05-19
- **Related RFC:** [RFC 0001 — Post-trade architecture](../rfc/0001-post-trade-architecture.md), §4 (channel matrix row "Surveillance feed"), §8 (roadmap row, scope caveat).
- **Related issue:** [#336](https://github.com/pedrosakuma/B3MatchingPlatform/issues/336)
- **Related ADRs:** [ADR 0001](0001-post-trade-boundary-and-eod-file-export.md) (fills.csv that this ADR points surveillance at), [ADR 0002](0002-per-trade-audit-log-shape.md) (fills-only audit log that bounds what surveillance can detect), [ADR 0008](0008-late-corrections-and-bust-propagation.md) (bust amendments that surveillance must read alongside fills).

## 1. Context

[RFC 0001 §4](../rfc/0001-post-trade-architecture.md#4-integration-channel-matrix)
catalogs a hypothetical "Surveillance feed" — intraday or EOD batch —
intended for a regulator-style consumer doing manipulation /
spoofing / ramping detection. [RFC §8](../rfc/0001-post-trade-architecture.md#8-adr-roadmap)
flags an explicit **scope caveat**: trade-only surveillance heuristics
(wash trades, self-trades, price impact) are reconstructable from a
fills audit log, but **spoofing / layering / quote-stuffing detection
requires order add/modify/cancel/phase context** that fills-only data
cannot supply.

[ADR 0002](0002-per-trade-audit-log-shape.md) shipped the audit log
as **fills-only** in v1 and deferred any order-lifecycle extension to
a follow-up ADR triggered by this one. This ADR therefore has two
linked questions:

1. Does a *new* surveillance artifact ship, or does surveillance
   reuse existing post-trade artifacts?
2. What detection class does the simulator promise to support?

## 2. Decision

**No new surveillance artifact is shipped.** Surveillance consumers
read the same EOD post-trade outputs everyone else reads:

- [ADR 0001](0001-post-trade-boundary-and-eod-file-export.md) `fills.csv`
  + `fills.csv.done` per channel per trade-date.
- [ADR 0008](0008-late-corrections-and-bust-propagation.md) `amendments.csv`
  + `amendments.csv.done` for any post-EOD bust that mutated a
  previously-published fill.

**The supported detection class is trade-only.** Wash trades, self-
trades, price-impact heuristics, opening/closing-price manipulation,
and anything else derivable from the *set of executed trades* is in
scope for what the simulator's outputs can support. The simulator
does not *implement* any detector — it produces the artifacts a
detector would read.

**Spoofing, layering, quote-stuffing, and any other order-lifecycle-
shaped detection is explicitly out of scope** for the v1 surveillance
contract, because the v1 audit log does not capture order
add/modify/cancel/phase events ([ADR 0002 §1](0002-per-trade-audit-log-shape.md#1-record-shape)).
Implementing such detection requires:

1. A new ADR extending the audit-log shape from [ADR 0002 §1](0002-per-trade-audit-log-shape.md#1-record-shape)
   with order-lifecycle record variants (book add / modify / cancel /
   session phase change), including framing, retention, and replay
   semantics.
2. A new ADR (or this one, superseded) defining the surveillance
   artifact that projects those lifecycle records.

Until step 1 lands, no surveillance promise in this ADR may be
weakened or stretched to claim coverage of order-lifecycle-shaped
behaviours.

**Intraday vs EOD.** The simulator publishes surveillance-relevant
data **EOD only**, on the [ADR 0001 §3](0001-post-trade-boundary-and-eod-file-export.md)
schedule. The live UMDF tape exists for any consumer that wants
intraday visibility, but it is not a contractual surveillance feed
(no `.done` sentinel, no retention promise, no immutability).

## 3. Surveillance-relevant guarantees of the existing artifacts

For a downstream surveillance tool to do its job against `fills.csv`
+ `amendments.csv`, it relies on guarantees that *already exist* in
the cited ADRs. Listed here for completeness so the surveillance
contract is self-contained:

| Property | Provided by |
| --- | --- |
| Every executed trade appears exactly once per trade-date | [ADR 0002 §3](0002-per-trade-audit-log-shape.md#3-write-protocol) (durable per-trade append) + [ADR 0001 §3](0001-post-trade-boundary-and-eod-file-export.md) (full-day projection) |
| `buyFirm` + `sellFirm` + `securityId` + `priceMantissa` + `size` per fill | [ADR 0002 §1](0002-per-trade-audit-log-shape.md#1-record-shape) |
| Aggressor side (`aggressorIsBuyer`) | [ADR 0002 §1](0002-per-trade-audit-log-shape.md#1-record-shape) |
| Trade-date is San Paulo business-date | [RFC 0001 §6](../rfc/0001-post-trade-architecture.md) |
| Post-EOD bust visible to surveillance | [ADR 0008 §4](0008-late-corrections-and-bust-propagation.md) `amendments.csv` |
| Pre-EOD bust folded into `fills.csv` | [ADR 0008 §3](0008-late-corrections-and-bust-propagation.md) split |
| Files are immutable once `.done` is published | [ADR 0001 §3](0001-post-trade-boundary-and-eod-file-export.md) |

A wash-trade detector, for example, joins `fills.csv` on
`(buyFirm, sellFirm, securityId)` within a time window; the audit
log retains nanosecond `transactTimeNanos` ([ADR 0002 §1](0002-per-trade-audit-log-shape.md#1-record-shape))
and that field propagates to `fills.csv` ([ADR 0001 §3](0001-post-trade-boundary-and-eod-file-export.md)).
No new artifact is needed.

## 4. What this ADR does *not* commit to

- No intraday surveillance feed. Consumers needing intraday signal
  consume UMDF directly; that is not a regulated contract.
- No detection logic shipped in this repo. The surveillance contract
  is "we publish the data, you publish the detector".
- No new retention policy beyond the existing `postTradeAudit.retentionDays`
  config knob (see [ADR 0002 §8](0002-per-trade-audit-log-shape.md)).
- No new identifiers (no `surveillanceCaseId`, no enriched venue
  metadata).
- No promise about order-lifecycle detectability until a follow-up
  ADR extends the audit shape.

## 5. Consequences

- The "Surveillance feed" row of [RFC 0001 §4](../rfc/0001-post-trade-architecture.md#4-integration-channel-matrix)
  is decided: not emitted as a separate artifact; pointed at
  `fills.csv` + `amendments.csv`.
- [ADR 0002](0002-per-trade-audit-log-shape.md)'s fills-only
  decision is reinforced: no surveillance requirement forces an
  immediate v2 schema bump.
- Any future feature request for spoofing-class detection is
  routed through a new ADR extending the audit log, not bolted onto
  this one.
- No code lands for this ADR. The artifacts surveillance reads
  already exist (or are spec'd by ADRs 0001 / 0008).

## 6. Alternatives considered

- **Ship a separate `surveillance.csv` projection.** Rejected:
  it would be a strict subset of `fills.csv` columns; consumers can
  do that projection themselves. A duplicate artifact adds publish
  cost, retention cost, and a second `.done` race surface for
  zero added information.
- **Block this ADR until ADR 0002 grows order-lifecycle records.**
  Rejected: that conflates a documentation decision (what we promise
  surveillance can do) with a substantial implementation decision
  (rewrite the audit shape). The two are sequenced: this ADR sets
  the v1 promise; a future ADR may extend it.
- **Promise spoofing detection now, with a TODO.** Rejected:
  promising detection that the underlying data physically cannot
  support is the worst failure mode for a compliance-adjacent
  contract.
- **Live multicast surveillance feed parallel to UMDF.** Rejected:
  duplicates the live tape; the live tape already exists and is
  free to consume; adding a second multicast group adds operational
  surface without information gain.

## 7. Open questions

- **Order-lifecycle audit extension trigger.** This ADR explicitly
  defers it. The trigger is a named consumer asking for spoofing-
  class detection. No ETA.
- **Surveillance-specific retention.** Surveillance traditionally
  needs longer retention than D+1 recon. Today, retention is the
  single `postTradeAudit.retentionDays` knob from [ADR 0002 §8](0002-per-trade-audit-log-shape.md).
  If a regulator-style consumer needs years of history, a follow-up
  ADR can carve out a separate retention bucket. Out of scope here.
