# ADR 0002 — Per-trade audit log shape

- **Status:** Accepted (retroactively documents the shape shipped by the
  #329 series — PR-1 through PR-6 — and formalizes the open questions
  pinned by [RFC 0001 §8](../rfc/0001-post-trade-architecture.md#8-adr-roadmap).
  The implementing code is already on `main`; this ADR records the
  decisions so they are referenceable from future work without
  reverse-engineering the codebase.)
- **Date:** 2026-05-20
- **Supersedes:** —
- **Superseded by:** —
- **Part of:** [RFC 0001 — Post-trade architecture](../rfc/0001-post-trade-architecture.md)
  (this ADR is the second child of that umbrella and the first
  one written *under* the RFC roadmap).
- **Related ADRs:** [ADR 0001](0001-post-trade-boundary-and-eod-file-export.md)
  (post-trade boundary; this ADR sits inside the
  `B3.Exchange.PostTrade` module ADR 0001 defined).
- **Related issues:**
  [#329](https://github.com/pedrosakuma/B3MatchingPlatform/issues/329)
  (per-trade audit log infra),
  [#330](https://github.com/pedrosakuma/B3MatchingPlatform/issues/330)
  (EOD fills file export — first downstream consumer of this log),
  [#332](https://github.com/pedrosakuma/B3MatchingPlatform/issues/332)
  (this ADR's tracking issue),
  [#348](https://github.com/pedrosakuma/B3MatchingPlatform/issues/348)
  (WAL prefix-truncate gate fix),
  [#349](https://github.com/pedrosakuma/B3MatchingPlatform/issues/349)
  (lock-free fsync).
- **Unblocks:** [ADR 0008](#) (late corrections / bust propagation) and
  the audit-log-extension follow-up referenced by RFC §8 ADR 0006.

## Context

[ADR 0001](0001-post-trade-boundary-and-eod-file-export.md) drew the
post-trade boundary and committed the simulator to a **per-trade audit
log as the post-trade source of truth**. It deliberately stopped short
of specifying the wire shape, the back-pressure policy, the
day-rollover rule, and the durability protocol that gates WAL
truncation — those decisions were pinned to a follow-up ADR by
[RFC 0001 §7 and §8](../rfc/0001-post-trade-architecture.md#7-failure-modes-the-post-trade-module-must-handle).

This ADR is that follow-up. It enumerates each of the eight decisions
called out in issue
[#332](https://github.com/pedrosakuma/B3MatchingPlatform/issues/332)
and records the choice taken, along with the rationale and the
explicit consequence for downstream ADRs.

The reference implementation lives in
[`src/B3.Exchange.PostTrade/`](../../src/B3.Exchange.PostTrade/) —
specifically `AuditRecordCodec.cs`, `AuditIndexCodec.cs`,
`AuditWatermarkCodec.cs`, `FileAuditLogWriter.cs`, and
`AuditLogReader.cs`. The dispatcher integration sits in
`src/B3.Exchange.Core/ChannelDispatcher.Sinks.cs` and
`ChannelDispatcher.Wal.cs`. Every concrete choice below is anchored to
the code so a future reader can verify the ADR against the source.

## Decision

### 1. Record framing: fixed-width little-endian binary

- One file per channel per UTC business date:
  `{rootDir}/{channelNumber}/fills-YYYY-MM-DD.log`.
- File starts with a **24-byte header**: magic `B3PT` (4),
  `schemaVersion` u16 (currently 1), reserved u16, `channelNumber` u8,
  3-byte pad, `tradeDate` ASCII `YYYY-MM-DD` (10). See
  `AuditRecordCodec.FileHeaderSize`.
- Each record is **85 bytes total**, framed as
  `recordLen` u32 + `crc32` u32 + 77-byte body (`tradeId`, transact
  time nanos, `securityId`, aggressor side, qty, price mantissa,
  buy/sell `ClOrdID`, buy/sell firm, buy/sell `orderId`). The CRC is
  IEEE 802.3 CRC-32 over the body only — `recordLen` is needed before
  reading the body and `crc32` is what is being verified. See
  `AuditRecordCodec.RecordSize`.
- Fixed-width was chosen over SBE and over delimited text:
  - **vs SBE:** the audit log is internal to this repo and consumed
    by a single in-process projection (`EodFillsExporter`) plus
    eventual replay/diagnostic tooling. The SBE toolchain is reserved
    for the wire-format boundaries (`B3.EntryPoint.Sbe`,
    `B3.Umdf.Sbe`) where there is an external counter-party that
    must match B3 exactly. Carrying SBE into post-trade would import
    a code-generation dependency for no interop benefit.
  - **vs delimited text (CSV / NDJSON):** fixed-width gives O(1)
    record offsets which the sparse firm index (`.idx`, see §2 of
    [ADR 0001](0001-post-trade-boundary-and-eod-file-export.md))
    relies on, makes CRC placement unambiguous, and avoids the
    ambiguity of quoting/escaping in text formats. The cost is that
    the file is not human-readable without
    `AuditLogReader` — an acceptable trade-off for a machine-only
    artifact (the human-readable surface is `fills.csv` from the EOD
    drop).
- The file header carries a **schema version** so columns can be
  added without rewriting historical days. A reader that encounters a
  newer `schemaVersion` than it knows about MUST refuse to project,
  never silently truncate.

### 2. Scope: fills-only

- The audit log in v1 records **trades only** — one record per engine
  `TradeEvent`. Order-lifecycle events (add / modify / cancel /
  phase changes) are **not** recorded on this stream.
- Rationale: the only committed consumer
  (`EodFillsExporter` → `fills-YYYY-MM-DD.csv` for downstream D+1
  recon, per [ADR 0001 §3](0001-post-trade-boundary-and-eod-file-export.md))
  needs fills only. Adding lifecycle events now would multiply the
  dispatch-thread write cost by orders of magnitude (typical
  add/modify/cancel volume is 50–100× trade volume in real
  equities flow) for a consumer that does not exist.
- **Coupling with ADR 0006 (surveillance feed):** RFC §8 flags this
  explicitly. Trade-only surveillance (wash trades, self-trades,
  price-impact heuristics) is reconstructable from a fills-only log.
  Spoofing / layering / quote-stuffing detection is not. If
  ADR 0006 is ever written and wants those capabilities, it must
  either (a) narrow its scope to trade-only surveillance, or
  (b) trigger a follow-up ADR that extends the audit log with an
  order-lifecycle stream — a sibling file pair, not a new column on
  the fills record, so existing readers keep working.
- The audit log **does** carry per-side `ClOrdID` and `orderId`, so a
  consumer can join fills back to the order stream observed via the
  live `ER_*` channel if it kept its own copy. The audit log does not
  retain the orders themselves.

### 3. No derived identifier beyond `tradeId`

- The on-disk record carries `tradeId` (engine-monotonic per channel,
  identical to the value already published on UMDF `Trade_53` and on
  `ER_Trade`) and a `transactTimeNanos` timestamp. No additional
  per-day sequence number is allocated.
- Rationale: `(channel, tradeId)` is already globally unique per
  channel for the lifetime of the channel; the projection layer
  (`EodFillsExporter`) and any future amendment artifact can key off
  `tradeId` alone. A per-day sparse-index sequence would have been
  useful for an O(1) "Nth record of day X" lookup, but the actual
  consumer pattern is "scan day X filtered by firm Y", which the
  firm-keyed sparse index (`.idx`) serves directly without needing a
  numbered position.

### 4. Back-pressure: block the dispatch thread (no queue, no drop)

- `OnTrade` runs **synchronously on the dispatch thread** under
  `FileAuditLogWriter._stateLock`. There is no in-memory queue
  between matching and the audit writer. If the underlying file
  system stalls, the dispatch thread stalls with it — back-pressure
  flows back to the engine, then to the gateway throttle, exactly as
  ADR 0001 §1's "matching → post-trade" boundary requires.
- Drop and spill are rejected:
  - **Drop** is incompatible with calling the artifact an "audit"
    log; the loss would silently undermine every D+1 recon.
  - **Spill** to a temporary disk buffer would require its own
    durability protocol, its own CRC, its own truncation policy —
    effectively a second audit log in front of the first one. Net
    complexity is worse for no clear win.
- The dispatch thread is not idle during fsync: the slow
  `fsync` syscall in `Checkpoint()` runs **outside** `_stateLock`
  against pinned `SafeFileHandle` refs (see
  `FileAuditLogWriter.Checkpoint` — implementation of
  [#349](https://github.com/pedrosakuma/B3MatchingPlatform/issues/349)).
  Only the `Write` itself blocks `OnTrade`.
- The dispatcher catches and swallows any exception thrown by
  `OnTrade` (see `ChannelDispatcher.Sinks.OnTrade`). The writer
  responds by entering **write-fault state** (`_writeFault = true`,
  sticky for the writer's lifetime). Every subsequent
  `OnCommandBoundary` and `Checkpoint` becomes a no-op /
  throws — which keeps the WAL truncation gate closed (see §5),
  guaranteeing the operator can recover by restarting and replaying
  the WAL.

### 5. Audit-durability watermark protocol

This is the contract referenced as "ADR 0001 §2 first-cut" in
[RFC §7](../rfc/0001-post-trade-architecture.md#7-failure-modes-the-post-trade-module-must-handle).

- The writer exposes `long DurableThroughCommandSeq` (highest
  engine `commandSeq` for which every produced trade has been
  `fsync`'d to the audit log).
- On the dispatch thread, after each command's UMDF packet is
  published, `ChannelDispatcher` calls `OnCommandBoundary(commandSeq)`.
  That tags the pending watermark — cheap; no I/O.
- The async snapshot writer calls `Checkpoint()` from its dedicated
  writer thread on its existing snapshot cadence. `Checkpoint()`:
  1. snapshots `_pendingCommandSeq` under `_stateLock`,
  2. user-space-flushes the `.log` and `.idx` streams under the lock,
  3. AddRefs both `SafeFileHandle`s, releases the lock,
  4. issues `RandomAccess.FlushToDisk` outside the lock,
  5. writes the watermark sidecar (`audit-watermark.bin`, 20 bytes,
     CRC'd, atomic-rename via `File.Move(..., overwrite: true)` —
     see `AuditWatermarkCodec`),
  6. on success, promotes pending → durable and increments the
     `audit_checkpoint_total` metric.
- The WAL prefix-truncate gate (`ChannelDispatcher.Wal.cs`,
  implementation of [#348](https://github.com/pedrosakuma/B3MatchingPlatform/issues/348))
  reads `DurableThroughCommandSeq` before dropping WAL records: a
  record is droppable only when its `commandSeq` is `<=` the audit
  watermark **and** `<=` the snapshot watermark. This guarantees a
  post-crash boot can always replay every command whose trades have
  not been durably audited yet.
- On restart, `FileAuditLogWriter`'s constructor reads
  `audit-watermark.bin` if present and CRC-valid; missing or torn
  sidecars are treated as "watermark unknown = 0", forcing
  conservative re-emission of every trade observed during WAL
  replay. The dispatcher's WAL-replay path gates each emitted
  `OnTrade` on the recovered watermark to suppress trades already
  durably written before the crash.

### 6. Day-rollover at UTC midnight: deterministic by record timestamp

- The trading-date partition is taken from each record's
  `TransactTimeNanos`, not from wall-clock at write time (see
  `FileAuditLogWriter.ToUtcDate`). The dispatcher's monotonic engine
  clock is the source of `TransactTimeNanos`, so the partition is
  deterministic across replay and across time-warped scenario tests.
- A trade whose `TransactTimeNanos` lies before UTC midnight goes
  into yesterday's file regardless of when it actually reaches the
  audit sink. A trade exactly at midnight nanosecond
  `00:00:00.000000000Z` belongs to the new day (UTC date is
  computed via `DateTime.UnixEpoch.AddTicks(...)` — the boundary is
  inclusive at the day start).
- Rationale: the only alternative — wall-clock — would mean a
  long-running replay could route a historical trade into the
  current operational day, corrupting both. The chosen rule has the
  property that the same sequence of `TradeEvent`s always produces
  the same file partition, which is the determinism property the
  EOD projection needs to round-trip in tests.
- The UTC choice itself was made by RFC 0001 §6 (project-wide
  default for post-trade artifacts). This ADR does not revisit it.

### 7. Correction events: out of scope for v1

- Operator trade busts (`POST /channel/{ch}/trade-bust/{tradeId}`,
  see `ChannelDispatcher.Operator.cs` and the RUNBOOK entry under
  "trade-bust") **do not** write to the audit log in v1. They
  publish a UMDF `TradeBust_57` frame only; the runbook explicitly
  documents that the simulator does not retain a per-trade audit
  record for the bust beyond the operator's own command log.
- This is the right scope for ADR 0002 because:
  - Including bust events on the same stream requires a tagged
    record variant (the body shape currently has no discriminator
    byte; adding one is a schema-version bump).
  - The semantics of late corrections — pre-EOD bust vs
    post-EOD bust, amendment file vs full rewrite, idempotency on
    duplicate busts — is a coherent decision in its own right
    that RFC §8 already pinned to **ADR 0008**.
- Forward compatibility: when ADR 0008 lands, it can introduce a
  new record variant by bumping `schemaVersion` to 2. The reader
  already enforces a version match on header read so v1 readers
  will refuse v2 files outright (no silent misinterpretation).
- Until ADR 0008 ships, the operational guidance is: pre-EOD busts
  before the EOD export runs, accept that post-EOD busts are
  documentation-only (UMDF tape + operator log) and not folded into
  the EOD CSV. The RUNBOOK reflects this.

### 8. Retention and backup: operator-owned, explicit non-promise

- The audit log writer **does not** rotate, compress, age out, or
  delete files. Once written, a `fills-YYYY-MM-DD.log` /
  `fills-YYYY-MM-DD.idx` pair stays in place until the operator
  removes it. The watermark sidecar (`audit-watermark.bin`) is the
  one file the writer rewrites in place.
- Backup is the operator's responsibility. The simulator gives
  three durability guarantees and no more:
  1. Within a successfully-fsync'd window
     (`commandSeq <= DurableThroughCommandSeq`), the on-disk file
     content is recoverable byte-for-byte after a crash.
  2. CRC32 per record allows offline detection of post-fsync
     corruption (cosmic ray, bit-rot, partial device failure).
     `AuditLogReader` surfaces the offset of the first failing
     record so an operator can quote it in a recovery ticket.
  3. The watermark sidecar's atomic-rename update means the WAL
     truncation gate can never overshoot what is actually on disk;
     a lost sidecar is conservatively rebuilt as "watermark = 0"
     on next start.
- Loss of an audit log file (operator error, filesystem corruption
  past CRC recovery, accidental `rm`) is **terminal for that day's
  D+1 recon**. The day's EOD CSV cannot be regenerated because the
  authoritative source is gone; the wire trade tape (UMDF) is
  consumer-managed and is not a substitute. Operators are expected
  to back up `audit/` to off-host storage at least once per
  trading day; this ADR does not specify the backup mechanism
  (rsync, snapshotting filesystem, object-storage upload — all are
  acceptable; none are bundled with the simulator).
- The RUNBOOK's "Post-trade audit log" section is the operational
  surface for this guidance. This ADR is the architectural
  justification for why retention sits on the operator's side of
  the boundary and not inside the writer.

## Consequences

- **Positive:**
  - Fixed-width binary with per-record CRC + per-channel daily file
    is the simplest representation that satisfies every committed
    consumer (EOD CSV projection, post-crash replay, offline
    diagnostics).
  - The watermark protocol gives the WAL a clean truncation
    contract that does not require it to know about post-trade
    internals — it just reads a `long`.
  - Schema version on the file header gives an upgrade path
    (ADR 0008 amendment events, future positional aggregate, etc.)
    without forcing immediate format breakage.
  - Dispatch-thread synchronous writes preserve the single-writer
    property the engine relies on; back-pressure has a coherent
    story all the way back to the gateway.
- **Negative / accepted trade-offs:**
  - The audit log is unreadable without `AuditLogReader`. We rely
    on the EOD CSV for human inspection.
  - Fills-only forecloses spoofing/layering surveillance until a
    future ADR extends the stream.
  - Synchronous dispatch-thread writes mean a slow disk degrades
    matching latency directly. Acceptable for a simulator;
    production would deserve a different policy.
  - Operator loses a `.log` file → that day's recon is
    unrecoverable. We accept this rather than building a
    duplicated WAL-of-the-audit-log inside the writer.

## Alternatives considered

- **SBE-framed audit log.** Rejected — see §1. SBE belongs at the
  wire-format boundaries.
- **NDJSON / delimited text.** Rejected — see §1. Loses O(1)
  offset addressing and complicates CRC placement.
- **Carry order add/modify/cancel/phase events on the same stream.**
  Deferred — see §2. Will be reconsidered if/when ADR 0006 is
  written.
- **Bounded in-memory queue with drop-or-spill back-pressure.**
  Rejected — see §4. Drop breaks the audit contract; spill is a
  second audit log.
- **fsync per record.** Rejected — would dominate the
  dispatch-thread latency budget. The watermark protocol gives the
  WAL a coarser but sufficient gate.
- **Coalesce audit fsync into the snapshot writer's checkpoint
  cadence (the chosen design)** vs **dedicated audit fsync timer.**
  Chose coalescing because the snapshot writer is already a single
  off-dispatch-thread actor and adding a second timer would
  multiply the configuration surface for no observable benefit.
- **Per-day sequence number.** Rejected — see §3.
- **Record bust events on the same stream now.** Deferred to
  ADR 0008 — see §7.

## Open questions

- Whether to add an operator-callable "audit log integrity scan"
  HTTP endpoint that walks every record and reports the first CRC
  failure. The capability already exists in `AuditLogReader`; what
  is missing is the operational surface and metric. Deferred until
  there is a concrete operator request.
- Whether a future ADR should mandate cryptographic signing of the
  daily `.log` file (e.g. for downstream evidence integrity).
  RFC 0001 §9 flagged signing as deferred globally; this ADR does
  not bring it forward.
- Off-host backup of `audit/` is operator-owned today. A future
  ADR could specify a bundled "drop to object storage on `Checkpoint()`"
  hook; not committed here.
