# ADR 0008 — Late corrections and bust propagation

- **Status:** Accepted (specification — no implementing PR yet; the
  decisions below are the contract that implementation must honour
  when issue [#338](https://github.com/pedrosakuma/B3MatchingPlatform/issues/338)
  is picked up).
- **Date:** 2026-05-19
- **Supersedes:** —
- **Superseded by:** —
- **Part of:** [RFC 0001 — Post-trade architecture](../rfc/0001-post-trade-architecture.md)
  (catalogued in §8 as the ADR that unblocks "any real D+1 recon
  flow that handles busts").
- **Related ADRs:** [ADR 0001](0001-post-trade-boundary-and-eod-file-export.md)
  (post-trade boundary + EOD CSV drop) and
  [ADR 0002](0002-per-trade-audit-log-shape.md) (audit log shape;
  ADR 0008 inherits §7's deferral and now owns both the audit-log
  shape extension and the bust semantics).
- **Related issues:**
  [#15](https://github.com/pedrosakuma/B3MatchingPlatform/issues/15)
  (current trade-bust admin replay),
  [#329](https://github.com/pedrosakuma/B3MatchingPlatform/issues/329)
  (audit log infra),
  [#330](https://github.com/pedrosakuma/B3MatchingPlatform/issues/330)
  (EOD CSV drop),
  [#338](https://github.com/pedrosakuma/B3MatchingPlatform/issues/338)
  (this ADR's tracking issue).
- **Blocks:** any consumer-side D+1 recon that must distinguish
  "trade happened" from "trade happened then busted" across the
  EOD boundary.
- **Unblocks:** implementation issue to be opened once this ADR
  is accepted.

## Context

The simulator already exposes an operator trade-bust endpoint
(`POST /channel/{ch}/trade-bust/{tradeId}`, issue #15, implemented
in `ChannelDispatcher.Operator.cs::ProcessTradeBust`). Its current
shape is a **pure UMDF replay**: the host accepts caller-supplied
echo fields (`securityId` required; `priceMantissa`, `size`,
`tradeDate` optional), allocates the next `RptSeq`, and publishes a
single `TradeBust_57` incremental frame. No engine state changes;
no audit-log record is written; no validation against the actual
trade is performed (the operator is expected to look up the echo
fields manually against `fills-YYYY-MM-DD.log`).

That shape is sufficient for exercising consumer-side bust handlers
end-to-end (the original motivation of #15), but it is **not**
sufficient as soon as the EOD CSV drop (ADR 0001 / #330) is in use
for D+1 recon:

- A consumer that already consumed
  `audit/{ch}/{date}/fills.csv.done` has no signal that a fill it
  ingested has subsequently been busted by the operator.
- The audit log (ADR 0002) is fills-only in v1, so even if the
  consumer wanted to re-derive its position from the simulator's
  source of truth, the busts would be invisible there too.
- [ADR 0002 §7](0002-per-trade-audit-log-shape.md#7-correction-events-out-of-scope-for-v1-reverses-rfc-7-pre-eod-constraint)
  explicitly reversed [RFC 0001 §7](../rfc/0001-post-trade-architecture.md#7-failure-modes-the-post-trade-module-must-handle)'s
  "pre-EOD bust must enter the audit stream" constraint, on the
  grounds that locking in a wire layout for correction events
  before the bust semantics were decided risked an incoherent
  half-design. ADR 0008 is the place to make both decisions
  together.

This ADR therefore owns:

1. The **audit-log shape extension** for correction events
   (the schema-v2 bump path ADR 0002 left open).
2. The **operator command contract** — idempotency, validation,
   reject conditions.
3. The **pre-EOD bust path** — fold corrections into the projection
   so the published `fills.csv` is already correct.
4. The **post-EOD bust path** — amendment file with a deterministic
   reconciliation rule the consumer can apply mechanically.

## Decision

### 1. Audit log schema-v2: fills + busts on the same stream

- Bump the audit-log file header `schemaVersion` from `1` to `2`
  (`AuditRecordCodec.SchemaVersion`). A v2 reader handles both
  fills and busts; a v1 reader refuses v2 files outright on header
  validation (already enforced by `ReadFileHeader` —
  see `src/B3.Exchange.PostTrade/AuditRecordCodec.cs:87-89`).
- Add a **1-byte record-type discriminator** as the first byte of
  the record body (immediately after `recordLen` + `crc32`):
  - `0x01` — **Fill** (existing 77-byte body shape; total
    on-disk record stays 85 bytes for backward semantic
    compatibility with v1 dumps modulo the new leading byte).
  - `0x02` — **Bust** (new shape; see §1.1).
  - All other values → reader treats the file as corrupt and
    surfaces the offset, same policy as a CRC failure.
- The discriminator sits **inside** the CRC-covered body, so a
  flipped discriminator byte is caught by the existing per-record
  CRC32. `recordLen` continues to carry the body byte count so
  variable-length records (fill vs bust) can coexist on the
  same stream.
- Existing v1 files (any day that was opened before the v2
  upgrade) are read by the v2 reader using a **per-day schema
  view**: if the file header says `schemaVersion=1`, the reader
  assumes every record is a fill and does not look for the
  discriminator. New files (opened after the upgrade) carry
  `schemaVersion=2` and the discriminator. This mirrors the
  "old days are read with their original schema" guarantee in
  ADR 0001 §2.

#### 1.1 Bust record body (schema v2, type=0x02)

Fixed-width, little-endian, exactly as for fills:

```
recordType       (uint8)   = 0x02
reserved         (uint8)   = 0
cancelledTradeId (uint32)  — tradeId of the fill being busted
bustTransactTimeNanos (uint64) — engine clock at the moment the operator
                                 command was accepted
securityId       (int64)   — echo from the original fill; lets readers
                             reject mismatches without dereferencing
                             the fill index
reasonCode       (uint16)  — see §2.2
busterFirm       (uint32)  — operator identity (always the host's
                             operator firm constant for simulator;
                             reserved for a future per-operator surface)
correlationId    (uint64)  — operator-supplied idempotency key (see §2.1)
```

Total body length: 1 + 1 + 4 + 8 + 8 + 2 + 4 + 8 = **36 bytes**
(vs 77 for fills). On-disk record: 4 + 4 + 36 = **44 bytes** for a
bust record (`recordLen` will read as 36 ≠ the fixed 77 of v1).

- `cancelledTradeId` is the **only** field the projection looks at;
  the other fields are for diagnostics and forward-compat with
  per-operator audit and reason-code analytics.
- A bust record's `transactTimeNanos` controls the file partition
  the same way a fill's does (ADR 0002 §6). A bust always lands in
  the file for the day the bust *happened*, which is **not
  necessarily** the day the original fill happened — see §4.

### 2. Operator command contract

#### 2.1 Idempotency

- The HTTP endpoint gains a required `correlationId` query
  parameter (u64). The same `(channel, tradeId, correlationId)`
  triple is a **no-op on repeat**:
  - If the bust has already been written to the audit log with
    the same `correlationId`, the second call returns HTTP 200
    with body `idempotent-replay` and the response header
    `X-Idempotent: true`. No new audit record; no second UMDF
    frame; no metric increment beyond an `operator_bust_replay_total`
    counter.
  - A second call with a **different** `correlationId` for the
    same `(channel, tradeId)` is rejected with HTTP 409
    (`already-busted`) — see §2.3.
- Rationale: operators retry on network failure all the time. A
  bust that *appears* to have failed must be safe to retry. The
  correlation id is what lets the host distinguish "retry of the
  same intent" from "second operator changed their mind about
  busting twice".

#### 2.2 Reason codes

A small enum (kept narrow on purpose — extensions are a schema
bump, not a free-form text field):

| Value | Meaning                                          |
|-------|--------------------------------------------------|
| 1     | Operator error (fat-finger, misrouted order)     |
| 2     | System error (engine bug, replay artifact)       |
| 3     | Regulatory request                               |
| 4     | Counterparty dispute                             |
| 99    | Other / unspecified (default if absent)          |

Reason codes are echo-only: they do not change projection
behaviour. They exist so post-incident reviews can filter the
audit stream without parsing free text. The endpoint accepts
`?reason=N` (default `99`).

#### 2.3 Reject conditions

| Condition                                            | HTTP | Audit log? |
|------------------------------------------------------|------|------------|
| `tradeId` unknown for `(channel, date)`              | 422  | No — no anchor for the record |
| `tradeId` already busted with a *different* `correlationId` | 409  | No — original bust already records the operator audit |
| `securityId` echo does not match the original fill   | 422  | No |
| `tradeDate` does not match the day the fill was written | 422  | No |
| Channel does not exist                               | 404  | n/a |
| Malformed / missing required parameters              | 400  | n/a |
| Same `(channel, tradeId, correlationId)` triple replay | 200 (`idempotent-replay`) | No (already recorded) |
| Valid first-time bust                                | 200 (`busted`) | **Yes** — fill-followed-by-bust on the stream |

Validation source: the host looks the `tradeId` up via
`AuditLogReader` over `fills-<tradeDate>.log` (the firm-sparse
index gives a bounded read; for the validation path a full-scan
fallback is acceptable since the operator surface is low-rate). If
the file is not on disk (operator pruned it, host moved
machines), the endpoint returns HTTP 410 (`gone`) with a body
documenting that bust-replay-against-archived-day requires
restoring the file first.

#### 2.4 UMDF frame is still published

Independent of audit-log accounting, every accepted bust still
publishes one `TradeBust_57` incremental frame (unchanged from
issue #15). The audit-log write happens **first** under the same
dispatch-thread invocation, so a successful UMDF emission implies
a durably-pending audit record (the next `Checkpoint()` fsyncs it
along with everything else).

### 3. Pre-EOD bust: fold into the projection

- A bust accepted **before** the EOD projection for that day has
  run is folded into the projection: the resulting
  `fills.csv` does **not** contain the busted fill at all (vs
  containing it then negating it). Rationale: D+1 recon on the
  consumer side becomes a single-pass match-and-tick exercise; no
  amendment file is needed for same-day busts.
- The projection is the existing `EodFillsExporter`. It already
  walks `fills-YYYY-MM-DD.log`; the v2 reader exposes both record
  types via an enumerator that yields either `Fill` or `Bust`. The
  projection builds an in-memory `HashSet<uint>` of cancelled
  `tradeId`s in a first pass, then in the second pass writes a
  fill row only if its `tradeId` is not in the set.
- Per-day memory cost: 4 bytes × number of pre-EOD busts. For any
  realistic day this is bounded well below 1 MB; single-pass with
  a streaming filter is also viable but the two-pass shape is
  simpler and matches existing exporter tests.
- A bust whose `tradeId` is not present in the day's fills (or
  refers to a different day's fills) is logged at WARN by the
  exporter and ignored. This case should not happen given §2.3's
  validation, but the projection stays defensive.

### 4. Post-EOD bust: amendments file, never rewrite

- A bust accepted **after** the EOD CSV for the day was published
  (i.e. after `fills.csv.done` exists for that channel+date) must
  not rewrite `fills.csv`. Consumers may have already ingested
  the original file; silently changing it would break the
  immutability contract that `fills.csv.done` implies.
- Instead, a sibling file is written on a separate atomic-rename
  staged-then-promoted path:
  - `audit/{ch}/{date}/amendments.csv` — one row per post-EOD
    bust, in append-only order.
  - `audit/{ch}/{date}/amendments.csv.done` — sentinel updated
    whenever a new amendment row is durably appended. Atomic
    replace via `File.Move(..., overwrite: true)`. Carries the
    sha256 of the current `amendments.csv` content so consumers
    can detect a partial read by comparing observed-vs-claimed
    digest.
- **`amendments.csv` columns** (header on row 0):
  ```
  cancelTradeId,bustTransactTime,reasonCode,correlationId,sha256OfOriginalFillRow
  ```
  The `sha256OfOriginalFillRow` lets a consumer that has
  `fills.csv` in hand prove the amendment targets the exact row
  it's about to drop, defending against the (unlikely) case of
  parallel file corruption.
- **Reconciliation rule:** the consumer applies amendments
  exactly once, ordered by `bustTransactTime` ascending. For each
  amendment:
  1. Locate the row in `fills.csv` where `tradeId == cancelTradeId`.
  2. Compute the sha256 of that row's serialised bytes and check
     it matches `sha256OfOriginalFillRow`. Mismatch → fail loudly.
  3. Remove that row from the consumer's working ledger. The
     `fills.csv` file on disk is **not** modified.
- A bust may arrive for a day whose `fills.csv` has been **purged**
  by host retention (ADR 0002 §8). In that case the audit-log
  bust record is still written, the UMDF frame is still
  published, but the amendments file write fails (since the
  parent directory is gone) and surfaces HTTP 410 to the
  operator. Operator must restore the day's audit log + CSV from
  backup before re-running the bust.

### 5. Cross-day busts

- A bust may target a `tradeId` from an earlier day (e.g. `T-1`
  fill busted at `T`). Per §1.1, the bust record lands in the file
  for the day the bust *happened* (`T`), so the audit log of
  day `T-1` is **not** rewritten — append-only is preserved.
- The projection for day `T-1`'s EOD CSV was published yesterday
  and is also not rewritten — see §4. The amendment is written to
  `audit/{ch}/T-1/amendments.csv`. The bust record lives in two
  audit files: as a v2 bust record in `T`'s `.log` (the canonical
  audit), and as an `amendments.csv` row in `T-1`'s drop
  directory (the consumer-facing projection).
- The endpoint requires `tradeDate=T-1` so the validation in §2.3
  routes the amendments file write to the correct directory.

### 6. Consumer migration

- A consumer that ignores `amendments.csv` entirely keeps the
  current "fills are immutable once `.done`" behaviour and silently
  diverges from the simulator's view of the world for busted
  trades. This is acceptable because it preserves backwards
  compatibility — existing recon flows do not break, they just
  carry pre-bust state.
- A consumer that wants bust-aware recon checks for the existence
  of `amendments.csv.done` in addition to `fills.csv.done` and
  applies the rule in §4. The recommended polling order:
  1. Wait for `fills.csv.done`.
  2. Read `fills.csv` into the ledger.
  3. Check `amendments.csv.done`; if present, apply per §4.
  4. Re-check `amendments.csv.done` on a periodic (e.g. 1h) cadence
     until a configurable cutoff (e.g. T+1 18:00 UTC).
- This staircase is the price of "never rewrite a published file".
  The alternative (full rewrite with generation tokens) was
  considered and rejected — see Alternatives.

## Consequences

- **Positive:**
  - Schema-v2 audit log carries the full operator decision stream
    (fills + busts), which makes ADR 0006 (surveillance) feasible
    for trade-only surveillance without a further schema bump.
  - Pre-EOD busts produce a clean `fills.csv` on first publish;
    most realistic operator scenarios never need the amendments
    machinery.
  - Post-EOD path preserves the immutability of published files,
    keeping `fills.csv.done` a meaningful contract.
  - Idempotency on `correlationId` makes the operator command
    safe to retry — operationally important.
- **Negative / accepted trade-offs:**
  - Two file shapes (v1 / v2) for the audit log means readers
    must branch on header version. Bounded complexity; the codec
    is single-purpose.
  - Bust validation requires the audit log of the original day to
    be on disk. Aggressive retention can lock the operator out
    of busting historical trades (we surface 410 explicitly
    rather than silently succeeding).
  - Consumers that don't poll for `amendments.csv.done` silently
    drift. We document the migration but cannot enforce it from
    the simulator side.
  - The bust record carries a `correlationId` field that is dead
    weight after the dedup window passes (no garbage collection
    — the log is append-only). Acceptable: 8 bytes/bust is
    rounding error against typical fill volume.

## Alternatives considered

- **Full rewrite of `fills.csv` with a generation token in
  `.done`.** Rejected: breaks the immutability promise of the
  `.done` sentinel that the consumer side
  ([B3TradingPlatform#274](https://github.com/pedrosakuma/B3TradingPlatform/issues/274))
  relies on. Forces every consumer to either lock the file on
  read or invent its own staleness detection. The amendments file
  shape solves the same problem with strictly less coupling.
- **Negate the fill in the EOD CSV** (write a `qty = -original`
  row). Rejected: turns the CSV into a balance-of-fills view
  rather than an event log, complicates the schema, and creates
  an irreversible asymmetry between "fills.csv" and "audit log"
  semantics.
- **Skip schema v2; record busts in a separate file from day 1**
  (e.g. `busts-YYYY-MM-DD.log`). Tempting because it sidesteps
  the discriminator byte. Rejected because it forces every
  audit-log consumer (today: `EodFillsExporter`; tomorrow:
  surveillance per ADR 0006) to open two files instead of one,
  and ordering between fills and busts of the same day is lost
  unless we cross-reference timestamps. The discriminator costs
  one byte per record; multi-file would cost ordering and a
  second sidecar to coordinate.
- **No `correlationId`; idempotency by `tradeId` only.** Rejected
  because two distinct operators (or the same operator across
  retries spanning a config reload) cannot tell whether a
  reported "already busted" is their own retry or another
  operator's prior action. The `correlationId` disambiguates
  this at the cost of one extra query parameter.
- **Reason codes as free text.** Rejected: not enforceable, not
  filterable without parsing, and a future schema bump becomes
  inevitable the moment a downstream tool wants typed reason
  analytics. Narrow numeric enum keeps options open.
- **Auto-detect pre-EOD vs post-EOD from the existence of
  `fills.csv.done`.** Adopted — see §3 vs §4: this is the
  trigger. The host's bust path checks the sentinel at command
  time and routes accordingly. No separate API surface.

## Open questions

- **`busterFirm` source.** v1 of this design uses a single
  host-level operator firm constant. A future ADR (or this one,
  if needed before the implementing PR lands) may introduce a
  per-operator identity surface; the field is sized for it
  (`uint32`).
- **Amendments-file retention.** Likely follows the same
  `postTradeAudit.retentionDays` knob as the `.log` files. Not
  explicitly committed here; will be settled by the implementing
  PR alongside the test for retention interaction.
- **`POST /admin/post-trade/bust` vs reusing
  `POST /channel/{ch}/trade-bust/{tradeId}`.** This ADR keeps the
  existing URL for backwards-compat and adds the new behaviour
  behind required parameters (`correlationId`). The implementing
  PR may decide to introduce a new namespaced URL if it turns
  out the old path is exercised by enough fixtures that a clean
  cut is preferable.
- **Operator-side dedup window.** The current design dedups
  forever (audit log is append-only). A bounded window
  (e.g. 24h) would let the host reject duplicate `correlationId`s
  cheaply via an in-memory LRU set. Deferred until there is
  evidence of operational pain.
