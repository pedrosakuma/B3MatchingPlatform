# ADR 0001 — Post-trade boundary and EOD file export

- **Status:** Proposed
- **Date:** 2026-05-16
- **Supersedes:** —
- **Superseded by:** —
- **Related issues:** #327 (closed as wrong-shape by this ADR),
  B3TradingPlatform#274 (downstream consumer), #321 (phase scheduler,
  provides the session-close trigger), #322 (single-stock halt API,
  companion operator surface).

## Context

`B3MatchingPlatform` is a stateful B3 exchange simulator: it takes B3
EntryPoint SBE orders on TCP and emits UMDF over UDP multicast plus
`ExecutionReport_Trade` back to the originating session. Trades exist on
the wire (`ER_Trade`, UMDF `Trade_53`) and are recovered through the
WAL on restart, but they are **not persisted as a queryable audit log**
— the runbook explicitly states *"the simulator does not retain a
per-trade audit log — the operator supplies the echo fields the consumer
audits."* See `docs/RUNBOOK.md` (`/channel/{ch}/trade-bust/{tradeId}`
section) and the comment in `src/B3.Exchange.Core/ChannelDispatcher.Operator.cs`.

Downstream `B3TradingPlatform` (Q2.7,
[#274](https://github.com/pedrosakuma/B3TradingPlatform/issues/274))
needs an EOD export of fills per firm for D+1 reconciliation against its
own `/statement/{date}.csv`. The initial issue opened on this repo
([#327](https://github.com/pedrosakuma/B3MatchingPlatform/issues/327))
specified the export as a synchronous REST endpoint:

```
GET /admin/reconciliation/fills?date=YYYY-MM-DD&firm=FIRM01
```

Two problems with that shape became clear during review:

1. **Wrong protocol surface.** Production B3 reconciliation flows are
   **file-based**, not REST. The exchange drops EOD files in a
   well-known directory (BVBG-family in production: BVBG.028 trade
   confirmations, BVBG.043 instruments, BVBG.086 prices, etc. — XML
   envelopes with well-defined headers); downstream consumers
   (clearing, brokers, surveillance, recon tools) pick them up
   asynchronously. A synchronous REST endpoint conflates
   **operational admin** (e.g. `/admin/daily-reset`,
   `/admin/channels/{ch}/snapshot`) with **post-trade clearing
   artifacts** and bakes the wrong coupling into the architecture —
   it implies the trading host has live network reach to the matching
   admin surface at recon time, and that the matching simulator is on
   the critical path for D+1 statement generation. Neither is true in
   the production analog.

2. **No post-trade module exists.** Anything EOD/recon-shaped requires
   a new persisted artifact whose lifecycle is **different from the
   WAL**:
   - WAL = recovery state. Retention is minimal (kept just long
     enough to replay across restart / snapshot rotation).
     Lifecycle is dictated by the engine's internal needs.
   - Audit / clearing artifacts = regulatory record. Retention is
     measured in days-to-years, controlled by compliance, with
     immutability and (eventually) cryptographic signing.

   Conflating these on the same store means the WAL's retention
   policy gets dragged toward audit retention, which destroys its
   recovery-cost properties.

Both problems point at the same root: the simulator does not yet have a
**post-trade boundary**, and #327 was trying to bolt on a post-trade
artifact via the admin HTTP surface without that boundary existing.
This ADR draws the boundary before any code is written.

## Decision

We adopt **three concentric decisions**:

### 1. Post-trade is a separate concern from matching

- The matching engine (`B3.Exchange.Matching`,
  `B3.Exchange.Core`, `B3.Exchange.Gateway`, etc.) owns the order
  book, matching, and the authoritative `tradeId` stream emitted as
  `ER_Trade` and UMDF `Trade_53`. That responsibility ends at the
  moment the trade leaves the dispatch thread.
- Post-trade artifacts (audit log, EOD drops, statements, reference
  data dumps, future clearing files) live in a **new project**:
  `B3.Exchange.PostTrade` (to be created alongside the first
  implementing issue — this ADR does not introduce it yet).
- Post-trade components **subscribe to** matching engine events
  (e.g. via a new `IPostTradeSink` registered on the
  `ChannelDispatcher`); they never reach into matching state nor
  share lifecycle with the WAL.
- Operational HTTP under `/admin/*` exposes only **triggers and
  status** for post-trade (e.g. "run EOD export now for date X"),
  never **queries over the data**. Querying post-trade data is the
  consumer's job, against the files that post-trade produces.

### 2. Per-trade audit log as the post-trade source of truth

- A new append-only **per-trade audit log** is written synchronously
  on the dispatch thread at the same point the engine emits
  `ER_Trade` / UMDF `Trade_53`. Writing on the dispatch thread is
  intentional: it preserves the existing single-writer property and
  guarantees the log order matches the published trade order.
- File pair per channel per trading date:
  `audit/<channel>/fills-YYYY-MM-DD.log` (records) and `.idx`
  (sparse index by firm — both buy and sell side, so a record is
  indexed twice when the firms differ; once when a firm crosses
  itself internally).
- Header carries a schema version so columns can be added without
  rewriting historical days; old days are read with their original
  schema.
- Crash-safety: CRC32C per record; `fsync` on rotation boundary and
  on operator-triggered checkpoints. We do **not** fsync per record
  — the audit log is allowed to lose the last few in-flight trades
  in a hard crash, the same window the WAL already accepts. Recovery
  on restart replays the WAL's trade events into the audit log to
  close the gap before opening the next day's file.
- Retention is **independent of WAL retention**, controlled by
  `config.postTrade.audit.retentionDays` (default to a sane
  compliance-friendly value — exact number TBD; the open question is
  recorded below).
- Trading date is the **UTC business date at trade time**, which is
  deterministic and easy to recover across restarts. (We are not
  modelling B3's local-time session boundaries in this ADR; if
  needed later, a follow-up ADR can swap the business-date function
  without changing the file format.)

### 3. EOD file drop, BVBG-like

- Once a trading session closes (operator-triggered via a new
  `/admin/post-trade/eod-export?date=YYYY-MM-DD` endpoint, or
  scheduler-driven via the phase scheduler from #321 when the
  configured close phase fires), the per-trade log for that date is
  projected into one or more **drop files** in a configured
  directory `config.postTrade.dropDir`.
- **Format v1 is CSV.** Header + rows, columns frozen at:
  ```
  tradeId, ts, symbol, side, qty, price, buyClOrdId, sellClOrdId, buyFirm, sellFirm
  ```
  This matches #327's column list verbatim so the downstream recon
  script's expectations carry over unchanged. `ts` is ISO-8601 UTC
  with microsecond precision. `side` is omitted from the file and
  always derivable from `buyFirm` / `sellFirm` — but kept here for
  symmetry with the consumer-side statement and to avoid forcing
  the recon script to compute it.
- **Format v2 (deferred):** wrap the CSV rows inside an XML envelope
  that mimics a BVBG header (sender, receiver, business date, file
  sequence, hash) so downstream consumers that ingest real BVBG can
  reuse their parser. Not built until a consumer demands it; the
  file-name convention reserves the path
  `<dropDir>/<YYYYMMDD>/fills.csv` so adding `fills.xml` later is
  non-breaking.
- **No REST query endpoint.** Downstream tools poll the drop
  directory (or subscribe to a filesystem notification). The
  matching simulator is not on the live recon path; it produces
  artifacts and is done.

### D+0 vs D+1 cycle

- **D+0 (intraday close):** EOD export runs after the session-close
  trigger. The file lands in `<dropDir>/<YYYYMMDD>/`.
- **D+1 (next morning):** trading-host's recon tool reads
  matching's D+0 drop and diffs against its own statement. Match =
  same trade count, same notional sum to the cent, no orphan
  `tradeId`s. Mismatch surfaces immediately, before the day's
  trading reopens.
- **Reprocessing:** the export is idempotent — pointing the operator
  endpoint at a past date rewrites the drop file deterministically
  from the audit log. Useful when the consumer wants a regenerated
  copy or the file was lost downstream.

## Consequences

### Positive

- The architecture stops conflating operator admin with post-trade
  clearing. Adding more clearing artifacts later (instrument dumps,
  price files, position aggregates) is a matter of dropping more
  files in `dropDir`, not adding more REST endpoints.
- Recon decouples from matching uptime: a recon tool can run the
  next morning while matching is down for maintenance, because the
  artifact is on disk.
- The audit log's retention is decoupled from WAL retention, so
  compliance retention does not bloat the recovery path.
- Future cryptographic signing (HMAC, PKI) can wrap the drop file
  without touching matching code.

### Negative / costs

- One more on-disk artifact (the audit log) with its own retention,
  rotation, and CRC story to maintain.
- Downstream `B3TradingPlatform` recon must learn to poll a
  directory instead of calling a REST endpoint — slightly more
  setup than `curl`, but matches how real recon flows work.
- We are not solving multi-host clustering of the audit log in this
  ADR (the simulator is single-host today). If a multi-host
  scenario ever lands, a follow-up ADR will need to address
  partition/merge of the per-host audit logs.

### Issue housekeeping triggered by this ADR

- **Close #327** as `not planned` — wrong shape. Link this ADR.
- **Open A — per-trade audit log:** infra-only, no HTTP, no export.
  Implements section 2 above. Blocks B.
- **Open B — EOD fills file export (BVBG-like):** projects A into
  `dropDir`. Implements section 3 above. Closes
  B3TradingPlatform#274 via the file drop.
- Both labelled `area:post-trade` so the new boundary is visible.

## Alternatives considered

- **Synchronous REST endpoint as originally filed in #327.**
  Rejected for the reasons in *Context* — wrong protocol surface,
  no underlying store, conflates admin with clearing.

- **Project trades from the WAL on demand instead of writing a
  separate audit log.** Rejected: WAL retention is too short for an
  audit log, and extending it to multi-day forces a retention
  policy on the recovery path that contradicts its purpose. Also
  couples the post-trade boundary to the WAL's internal format,
  which we explicitly want freedom to evolve.

- **Write directly to BVBG XML and skip CSV.** Rejected for v1.
  CSV is what the downstream recon script already wants, BVBG XML
  is a heavier commitment (schemas, namespaces, sender/receiver
  identity) without a first consumer. Section 3 keeps the door
  open without paying the cost now.

- **Make the audit log a downstream consumer of UMDF rather than a
  dispatch-thread sink.** Tempting (it would put zero pressure on
  the matching hot path), but loses ordering guarantees relative to
  `ER_Trade` and requires the audit log to deal with UMDF packet
  loss / reordering and snapshot recovery. Writing on the dispatch
  thread alongside the existing sinks keeps things simple and
  correct.

## Open questions (explicit non-decisions)

These are deliberately deferred. Each will be answered by a follow-up
ADR or RFC when a concrete consumer or compliance requirement forces
the decision:

- **Signing.** HMAC with a shared secret (cheap, symmetric, works
  for a single trusted downstream) vs PKI signature (heavier, lets
  multiple consumers verify without sharing a secret). No signing in
  v1 — files are produced and consumed on the same trust boundary.
- **XML BVBG envelope.** Format v2 above. Deferred until a consumer
  asks for it.
- **Multi-firm split files vs one file with firm column.** Current
  decision: one file per channel per date with `buyFirm`/`sellFirm`
  columns. Splitting per-firm is a future optimisation if a
  consumer wants firm isolation at the file boundary.
- **Compression.** Plain CSV in v1. GZIP/Zstd is a non-breaking
  add-on (filename suffix).
- **Schema registry for the audit log.** The header carries a
  schema version, but no separate registry exists. If we ever need
  cross-language readers, a sidecar JSON-Schema-style descriptor
  next to the file is the lightweight path; a heavy registry is
  out of scope.
- **Exact retention defaults.** Pinned by the implementing issue
  with input from compliance assumptions; this ADR only says
  "independent of WAL retention".
- **Business-date boundary.** UTC midnight in v1. A future ADR may
  swap in B3 session-time boundaries if needed; the file format
  does not constrain that choice.

## References

- `docs/RUNBOOK.md` — current operator endpoints; the `/admin/*`
  surface this ADR draws a line around.
- `docs/EXCHANGE-SIMULATOR.md` — `wal` config block; this ADR keeps
  it untouched.
- `src/B3.Exchange.Core/ChannelDispatcher.Operator.cs` — comment
  noting the lack of a per-trade store, which motivated this ADR.
- B3 BVBG family (production analog), e.g. BVBG.028 trade
  confirmations; not normative for this simulator, used as the
  shape model for the file-drop pattern.
