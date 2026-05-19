# ADR 0007 — Position aggregate per firm (deferred)

- **Status:** Deferred (no consumer)
- **Date:** 2026-05-19
- **Related RFC:** [RFC 0001 — Post-trade architecture](../rfc/0001-post-trade-architecture.md), §4 (channel matrix row "Position aggregate dump"), §8 (roadmap row).
- **Related issue:** [#337](https://github.com/pedrosakuma/B3MatchingPlatform/issues/337)
- **Related ADRs:** [ADR 0001](0001-post-trade-boundary-and-eod-file-export.md) (`fills.csv` this projects from), [ADR 0008](0008-late-corrections-and-bust-propagation.md) (`amendments.csv` that must be folded in alongside fills).

## 1. Context

[RFC 0001 §4](../rfc/0001-post-trade-architecture.md#4-integration-channel-matrix)
catalogs a hypothetical per-firm position-aggregate artifact: one row
per `(firm, securityId)` per trade-date with net long/short quantity
and (optionally) volume-weighted average price. The artifact is a
**pure projection** of `fills.csv` ([ADR 0001 §3](0001-post-trade-boundary-and-eod-file-export.md))
and, post-bust, of `amendments.csv` ([ADR 0008 §4](0008-late-corrections-and-bust-propagation.md));
it does *not* introduce new authoritative state.

[RFC 0001 §8](../rfc/0001-post-trade-architecture.md#8-adr-roadmap)
classifies this as a convenience artifact with no immediate consumer.

## 2. Decision

**Do not ship a position-aggregate artifact at this time.** This ADR
records the deliberate decision so that future contributors do not
re-litigate it whenever someone says "wouldn't a positions file be
nice?"

The fills drop is sufficient. Any consumer that needs positions can
trivially derive them from `fills.csv` + `amendments.csv` with a
SQL `GROUP BY` or equivalent. Shipping a positions file from this
repo would *not* save consumers from also reading `fills.csv` for
audit, recon, or surveillance — so the positions file is purely
additive convenience.

**Trigger to revisit:** a named downstream consumer (with a real
ingestion need that justifies the simulator publishing it rather
than the consumer deriving it) asks for the artifact. Until then
this ADR holds.

**When this ADR is revisited**, the implementing ADR (a new ADR
that supersedes this one) must decide:

1. **Netting convention.** Long/short netting per
   `(firm, securityId, trade-date)` is the obvious default, but
   the implementing ADR must call it out — for example, intraday
   round-trips (buy 100, sell 100) net to flat or to "200 traded
   volume, 0 net position"? The artifact must commit to one.
2. **Starting position source.** Net intraday flow is straightforward;
   end-of-prior-day starting position is not. Two options:
   (a) the artifact reports *intraday delta only* (no opening
   position; consumer carries its own roll forward), or
   (b) the simulator tracks per-firm positions across days and
   reports closing position.
   Option (a) is strictly cheaper (still pure projection from one
   day's `fills.csv`); option (b) introduces new authoritative state
   and a recovery surface (snapshot, replay) and should be
   considered a much larger ADR.
3. **VWAP / price columns.** Whether to ship volume-weighted
   average price per `(firm, securityId)`. Trivially derivable
   from fills; same convenience-vs-duplication argument.
4. **Bust handling.** Position must reflect [ADR 0008](0008-late-corrections-and-bust-propagation.md)
   amendments. Implementing ADR must commit to either:
   (a) regenerating the positions file when a post-EOD bust lands
   for that date (mirror of [ADR 0008 §4](0008-late-corrections-and-bust-propagation.md)),
   or (b) shipping a separate `position-amendments` artifact, or
   (c) publishing positions *after* a quiet window only.
   Option (a) is consistent with how `amendments.csv` is published.
5. **Publish protocol.** Must mirror [ADR 0001 §3](0001-post-trade-boundary-and-eod-file-export.md)'s
   atomic `.done` sentinel discipline. Same fsync sequence;
   regenerated in full from `fills.csv` (and `amendments.csv` if
   present); no in-place mutation.
6. **Retention.** Either follow `postTradeAudit.retentionDays`
   ([ADR 0002 §8](0002-per-trade-audit-log-shape.md)) or carve out
   a separate knob — the implementing ADR must say which.

## 3. Consequences

- One less artifact to maintain, version, document, and back-fill.
- Consumers that need positions today derive them from `fills.csv`
  + `amendments.csv`; that derivation is explicitly their
  responsibility.
- No new authoritative state. The starting-position question
  ([§2.2](#2-decision) option b) is deferred indefinitely, which
  also keeps the post-trade module stateless across days (no
  positions snapshot, no positions WAL).
- The "Position aggregate dump" row in [RFC 0001 §4](../rfc/0001-post-trade-architecture.md#4-integration-channel-matrix)
  is decided: not emitted.
- No code lands for this ADR. No retention policy, no admin
  endpoint, no monitoring surface.

## 4. Alternatives considered

- **Ship intraday-delta positions speculatively (cheap option a).**
  Rejected: still adds publish surface, `.done` race surface,
  documentation surface, retention surface; with no consumer there
  is no offsetting value, and consumer SQL is one line.
- **Ship closing positions with simulator-tracked roll forward
  (option b).** Rejected: introduces new authoritative state
  (positions snapshot per firm per date) and a recovery surface
  (replay across days), which is materially larger than
  "convenience artifact". Needs a real consumer demand to justify.
- **Mark this ADR Rejected.** Rejected: "Rejected" implies the
  feature was actively turned down on its merits. The accurate
  status is *Deferred until a consumer materialises* —
  position aggregates are a fine artifact, just not a needed one
  today.

## 5. Open questions

None until a consumer surfaces. The questions in §2 are the agenda
for the implementing ADR if and when this one is superseded.
