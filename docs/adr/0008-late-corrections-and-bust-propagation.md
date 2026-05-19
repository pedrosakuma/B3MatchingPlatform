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
- **Framing rule (unchanged from v1, ratified by v2).** Every
  record on disk is `recordLen` (uint32 LE) + `crc32` (uint32 LE)
  + body, where `recordLen` counts `crc32 + body` (i.e.
  `RecordSize - 4`). The existing v1 fill writes
  `recordLen = 81` (`crc(4) + body(77)`); v2 adds bust records
  with `recordLen = 40` (`crc(4) + body(36)`) and reject-attempt
  records with `recordLen = 36` (`crc(4) + body(32)`, see §2.5).
  See `AuditRecordCodec.cs:123-124` for the v1 reference.
- **Record-type discrimination is by `recordLen`, NOT by a leading
  body byte for fills.** A v2 reader peeks `recordLen` first and
  dispatches to the type-specific decoder:
  - `recordLen == 81` → Fill body, **byte-for-byte identical to
    v1**. v1 dumps and v2 files contain bit-identical fill
    records; no upgrade tool is needed and a v2 file that
    contains only fills compares equal to its v1 predecessor
    modulo the file header.
  - `recordLen == 40` → Bust body, schema v2 (§1.1, pre-PR-4).
  - `recordLen == 44` → Bust body, schema v3 (§1.1, PR-4 onward).
  - `recordLen == 36` → Reject-attempt body (§2.5).
  - any other value → reader treats the file as corrupt and
    surfaces the offset, same policy as a CRC failure.

  As defence in depth, bust and reject-attempt bodies carry a
  leading `recordType` byte (`0x02` and `0x03` respectively) that
  the decoder cross-checks against the `recordLen`-derived
  dispatch; a mismatch is treated as corruption. The leading
  byte is inside the CRC-covered body.
- Existing v1 files (any day that was opened before the v2
  upgrade) are read by the v2/v3 reader using a **per-day schema
  view**: if the file header says `schemaVersion=1`, the reader
  decodes only fill records (`recordLen == 81`) and refuses any
  other value as corruption. New files (opened after the
  PR-4 schema bump) carry `schemaVersion=3`; PR-2-era files carry
  `schemaVersion=2` and continue to be read with their original
  schema. This mirrors the "old days are read with their
  original schema" guarantee in ADR 0001 §2.

#### 1.1 Bust record body (schema v3, type=0x02, `recordLen` = 44)

Fixed-width, little-endian, exactly as for fills:

```
recordType            (uint8)   = 0x02
reserved              (uint8)   = 0
cancelledTradeId      (uint32)  — tradeId of the fill being busted
bustTransactTimeNanos (uint64)  — engine clock at the moment the operator
                                  command was accepted
securityId            (int64)   — echo from the original fill; lets readers
                                  reject mismatches without dereferencing
                                  the fill index
reasonCode            (uint16)  — see §2.2
busterFirm            (uint32)  — operator identity (always the host's
                                  operator firm constant for simulator;
                                  reserved for a future per-operator surface)
correlationId         (uint64)  — operator-supplied idempotency key (see §2.1)
declaredTradeDate     (int32)   — LocalMktDate (days since 1970-01-01) of
                                  the original fill being busted; PR-4
                                  addition so post-EOD busts (which land
                                  in fills-<bustToday>.log, not the
                                  original day's file) still attribute
                                  back to the original day for §4
                                  amendments-file regeneration
```

Total body length: 1 + 1 + 4 + 8 + 8 + 2 + 4 + 8 + 4 = **40 bytes**.
On-disk record: 4 (`recordLen`) + 4 (`crc32`) + 40 = **48 bytes**;
`recordLen` reads as **44** (= `crc(4) + body(40)`), matching the
v1 framing convention.

**v2 backwards compatibility** (per the per-day schema view above):
PR-2-era v2 bust records on disk have `recordLen == 40`, omit the
trailing `declaredTradeDate` field (body = 36 bytes, on-disk = 44
bytes), and are decoded into in-memory `BustRecord`s with
`DeclaredTradeDateDays = -1` (the `BustRecord.DeclaredTradeDateAbsent`
sentinel). `BustDedupIndex.LoadFromAuditFiles` falls back to the
file-name day in that case — for v2 records the writer contract
(OnBust appends to `fills-<tradeDate>.log`) made the file-name day
always equal to the trade's day.

- `cancelledTradeId` is the **only** field the projection looks at;
  the other fields are for diagnostics and forward-compat with
  per-operator audit and reason-code analytics.
- A bust record's `bustTransactTimeNanos` controls the file
  partition the same way a fill's does (ADR 0002 §6). A bust
  always lands in the file for the day the bust *happened*,
  which is **not necessarily** the day the original fill happened
  — see §3 and §5 for how the projection decisions key off the
  *original fill's* day instead.

### 2. Operator command contract

#### 2.0 Endpoint split (backwards-compat)

The existing `POST /channel/{ch}/trade-bust/{tradeId}` endpoint
(issue #15) is kept **as-is** for backwards compatibility: no
required `correlationId`, no validation against the audit log,
no audit-log write, response stays HTTP 202, payload format
unchanged. Documented as "legacy replay-only" in the RUNBOOK;
operators using only the wire-tape replay use case keep
working. Existing tests do not need to migrate.

The strict contract specified by §2.1–§2.5 lives on a new URL:

```
POST /admin/post-trade/bust?channel=N&tradeId=T&tradeDate=YYYY-MM-DD
    &correlationId=C[&reason=R][&securityId=S][&priceMantissa=P][&size=Q]
```

Only the new endpoint writes audit records, performs validation,
and feeds the EOD projection. The legacy endpoint is documented
as **non-canonical** — operators that need D+1 recon correctness
must use the new endpoint. A future ADR may deprecate the legacy
URL once consumers have migrated, but this ADR does not.

#### 2.1 Idempotency

- The new endpoint requires a `correlationId` query parameter
  (u64). The same `(channel, tradeId, correlationId)` triple is a
  **no-op on repeat**:
  - If the bust has already been written to the audit log with
    the same `correlationId`, the second call returns HTTP 200
    with body `idempotent-replay` and the response header
    `X-Idempotent: true`. No new audit record; no second UMDF
    frame; no metric increment beyond an `operator_bust_replay_total`
    counter.
  - A second call with a **different** `correlationId` for the
    same `(channel, tradeId)` is rejected with HTTP 409
    (`already-busted`) — see §2.3.
- Dedup lookup: the host maintains a per-channel
  `Dictionary<uint, ulong>` mapping `tradeId → correlationId`
  for the **currently active rolling window of audit days** (every
  audit `.log` file under the channel's `audit/{ch}/` directory
  whose date is `>= todayUtc - retentionDays + 1`, or all of them
  when `retentionDays = 0`). The map is rebuilt at startup by
  scanning the active days' bust records via the v2 reader; from
  then on every accepted bust updates the map in memory. Cost:
  one `Dictionary` entry per bust per channel — bounded by the
  retention horizon, and busts are inherently rare events.
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

#### 2.3 Validation precondition: original-day artifacts must exist

Before ANY audit-log write, UMDF emission, or amendments-file
write, the host validates that:

1. The channel exists (else HTTP 404).
2. `fills-<tradeDate>.log` is present and readable under
   `audit/{ch}/` (else HTTP 410 `gone`, body: "restore the
   original day's audit log before retrying"). This applies
   regardless of pre-EOD or post-EOD because every consequence
   of the bust (audit body, projection fold, amendment row)
   needs the original fill to be locatable.
3. The `tradeId` exists in that day's `.log`, has not already
   been busted with a different `correlationId`, and the
   `securityId` echo (when supplied) matches the original fill.
4. `tradeDate` is the day of the original fill (NOT the day of
   the bust). For cross-day busts the operator MUST supply
   `tradeDate` explicitly — the endpoint does not default it.

Reject-table summary:

| Condition                                            | HTTP | Audit log? |
|------------------------------------------------------|------|------------|
| Channel does not exist                               | 404  | n/a |
| Malformed / missing required parameters              | 400  | n/a |
| `fills-<tradeDate>.log` not on disk (purged / never written) | 410  | n/a — no `.log` to write to |
| `tradeId` unknown for `(channel, tradeDate)`         | 422  | **Yes (§2.5)** — reject-attempt record in TODAY's `.log` |
| `tradeId` already busted with a *different* `correlationId` | 409  | **Yes (§2.5)** — reject-attempt record in TODAY's `.log` |
| `securityId` echo does not match the original fill   | 422  | **Yes (§2.5)** — reject-attempt record in TODAY's `.log` |
| Same `(channel, tradeId, correlationId)` triple replay | 200 (`idempotent-replay`) | No (original bust already recorded) |
| Valid first-time bust                                | 200 (`busted`) | **Yes** — bust record in `<tradeDate>`'s `.log` |

Validation source for (3): `AuditLogReader` over
`fills-<tradeDate>.log` plus the in-memory dedup map from §2.1.
The firm-sparse index gives a bounded read; for the validation
path a full-scan fallback is acceptable since the operator
surface is low-rate. The decoder skips bust and reject-attempt
records (it cares only about fills for this lookup).

#### 2.4 UMDF frame is still published

For an **accepted** bust (the last row of the §2.3 table),
the host publishes one `TradeBust_57` incremental frame
identical to the existing #15 behaviour, on the channel's
incremental stream, under the dispatcher's current
`SequenceVersion` and next `RptSeq`. Sequencing on the
dispatch thread: audit-log write → UMDF frame → amendments-file
write (post-EOD only, see §4). A failure at any step short-
circuits the next; the audit log is the canonical record.

Rejected attempts do **not** publish a UMDF frame — the wire
sees an accepted bust only when the simulator endorses it.

#### 2.5 Reject-attempt audit record (schema v2, type=0x03, `recordLen` = 36)

Per RFC §7's duplicate-bust row, the operator audit trail must
be complete even when the command was a no-op or a hard reject.
This record captures the **attempt**, lands in **today's** audit
file (the day the attempt arrived, NOT the original fill's day,
because for unknown-tradeId attempts there IS no original-fill
day to anchor against), and never produces a UMDF frame or an
amendments-file row.

```
recordType            (uint8)   = 0x03
reserved              (uint8)   = 0
attemptedTradeId      (uint32)
attemptTransactTimeNanos (uint64) — engine clock at command receipt
declaredTradeDate     (int32)   — LocalMktDate (days since 1970-01-01)
                                  the operator declared; 0 if absent
rejectCode            (uint16)  — 1=unknown, 2=already-busted-different-correlationId,
                                  3=securityId mismatch, 4=missing-day (only when
                                  channel exists; 410 path)
busterFirm            (uint32)
correlationId         (uint64)
```

Total body length: 1 + 1 + 4 + 8 + 4 + 2 + 4 + 8 = **32 bytes**.
On-disk record: 4 + 4 + 32 = **40 bytes**; `recordLen` reads as
**36** (= `crc(4) + body(32)`). Distinguished from a bust record
(`recordLen = 40`) by the framing dispatch in §1.

Reject-attempt records are **invisible** to the EOD projection
(EodFillsExporter filters by recordType) and to the amendments
writer. They exist solely for the operator audit trail.

### 3. Pre-EOD vs post-EOD: keyed on the *original fill's* day

- The split between "fold into projection" (§3a) and "write
  amendments file" (§3b) is decided by whether
  `audit/{ch}/{tradeDate}/fills.csv.done` exists at command-time,
  where `tradeDate` is the day of the **original fill** (not the
  day the bust arrived). This is the only key that yields the
  right behaviour for cross-day busts (§5).
- The host checks the sentinel under a short per-channel lock so
  a concurrent EOD export cannot race with a bust accepted
  fractions of a second before the export's `.done` rename. If
  the sentinel appears between the check and the projection
  read, the bust is treated as post-EOD (the late-arriving case)
  and routed to the amendments path. The EOD exporter holds the
  same per-channel lock between its own cancelled-set scan and
  its `.done` rename, so the window is closed.

#### 3a. Pre-EOD path (no `fills.csv.done` yet for `tradeDate`)

- The bust record is appended to `fills-<tradeDate>.log` (the
  day of the original fill, NOT the day the bust arrived — this
  is an explicit override of §1.1's "bust always lands in the
  bust's day" rule for the pre-EOD case, see Cross-day-busts
  carve-out below).
- The EOD projection for that day, when it runs, folds the
  cancellation into the output: the resulting `fills.csv`
  does **not** contain the busted fill at all (vs containing it
  then negating it). Rationale: D+1 recon on the consumer side
  becomes a single-pass match-and-tick exercise; no amendment
  file is needed for same-day busts.
- The projection is the existing `EodFillsExporter`. It already
  walks `fills-YYYY-MM-DD.log`; the v2 reader exposes the
  record-type dispatch, the projection builds an in-memory
  `HashSet<uint>` of cancelled `tradeId`s from bust records in a
  first pass, then in the second pass writes a fill row only if
  its `tradeId` is not in the set. Reject-attempt records (§2.5)
  are skipped entirely.
- Per-day memory cost: 4 bytes × number of pre-EOD busts. For any
  realistic day this is bounded well below 1 MB; single-pass with
  a streaming filter is also viable but the two-pass shape is
  simpler and matches existing exporter tests.

#### 3b. Post-EOD path (`fills.csv.done` exists for `tradeDate`)

- The bust record is appended to `fills-<bustToday>.log` per
  §1.1's default rule (the day the bust *happened*). The
  original day's `.log` is not touched — append-only is
  preserved across days that have already been exported.
- The amendments file for `tradeDate` is updated per §4. This
  is the only post-EOD consumer-visible artifact.

### 4. Post-EOD amendments file shape and publish protocol

- A bust accepted on the post-EOD path (§3b) writes one row to
  `audit/{ch}/{tradeDate}/amendments.csv` and updates the
  sibling `amendments.csv.done` sentinel. Consumers may have
  already ingested `fills.csv`; silently changing it would break
  the immutability contract that `fills.csv.done` implies.
- **`amendments.csv` columns** (header on row 0):
  ```
  cancelTradeId,bustTransactTime,reasonCode,correlationId,sha256OfOriginalFillRow
  ```
  Encoding: UTF-8, no BOM, `\n` (LF) row terminator, no trailing
  whitespace — same conventions as `fills.csv` (see
  `EodFillsExporter.WriteCsv`).
- **`sha256OfOriginalFillRow` is defined precisely as:** the
  SHA-256 of the contiguous byte slice in the published
  `fills.csv` starting at the first byte of the target row
  (the byte after the preceding `\n`, or byte 0 for row 0 — but
  row 0 is the header so this case does not occur) through and
  **including** the row-terminating `\n`. This matches the byte
  range a consumer can compute from a single forward scan of the
  published file. Hex-encoded lowercase in `amendments.csv`.
- The publish protocol mirrors `EodFillsExporter`'s
  delete-old-`.done`-before-replace pattern (see
  `EodFillsExporter.cs:168-187`):
  1. Read the current full state from the audit log (all bust
     records targeting `tradeDate` that have already been
     written, including the one just appended). The amendments
     file is **regenerated in full** from the audit log on every
     update — not literally appended — so a torn write cannot
     leave a partial row visible.
  2. Stage the new `amendments.csv` content to
     `amendments.csv.staging.<pid>.<ts>` and fsync.
  3. Compute SHA-256 of the staged content; stage
     `amendments.csv.done` carrying that digest, fsync.
  4. If a prior `amendments.csv.done` exists at the final path,
     delete it first and fsync the directory. From this point a
     crash leaves no `.done` — consumers correctly poll "not
     ready" until the next bust republishes.
  5. `File.Move` the CSV staging file over the final path,
     fsync the directory.
  6. `File.Move` the `.done` staging file over the final path,
     fsync the directory.

  This sequence guarantees consumers never observe an
  `amendments.csv` body whose SHA-256 does not match the
  `amendments.csv.done` digest. The cost (re-serialise the full
  amendments set per update) is bounded by the small expected
  number of post-EOD busts per day.
- **Reconciliation rule:** the consumer applies amendments
  exactly once, ordered by `bustTransactTime` ascending. For
  each amendment:
  1. Locate the row in `fills.csv` where `tradeId == cancelTradeId`.
  2. Compute SHA-256 of that row per the byte-range definition
     above and check it matches `sha256OfOriginalFillRow`.
     Mismatch → fail loudly; do not silently apply.
  3. Remove that row from the consumer's working ledger. The
     `fills.csv` file on disk is **not** modified.
- An amendments-file write that fails after the audit record is
  already written leaves the system in a consistent state:
  `DurableThroughCommandSeq` is not advanced past the bust's
  command until the next `Checkpoint()`, and on retry the
  in-memory dedup map (§2.1) recognises the same
  `(channel, tradeId, correlationId)` triple and re-runs only
  the amendments-file publish. The bust is therefore
  end-to-end-idempotent across crash boundaries.

### 5. Cross-day busts

- A bust may target a `tradeId` from an earlier day (e.g. `T-1`
  fill busted at `T`). Per §2.3, the operator MUST supply
  `tradeDate=T-1` explicitly. The endpoint routes by §3:
  - If `T-1`'s `fills.csv.done` does not exist yet (unusual
    case — the export job ran late), §3a applies: the bust is
    appended to `fills-T-1.log` and folded into the eventual
    EOD projection.
  - If `T-1`'s `fills.csv.done` already exists (the common
    case — T-1 ran yesterday), §3b applies: the bust is
    appended to `fills-T.log` (today's audit) and a row is
    added to `audit/{ch}/T-1/amendments.csv`.
- A bust record never lives in two audit `.log` files: the
  per-day partition is unambiguous per §3a/§3b. The bust's
  `tradeDate` (an operator-supplied input) is the routing key;
  the bust's `bustTransactTimeNanos` is just a timestamp.

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
