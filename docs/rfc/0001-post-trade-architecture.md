# RFC 0001 — Post-trade architecture (umbrella)

- **Status:** Draft (living once accepted — the *ADR roadmap*
  section evolves as follow-up ADRs land or new decisions surface)
- **Date:** 2026-05-16
- **Supersedes:** —
- **Related ADRs:** [ADR 0001](../adr/0001-post-trade-boundary-and-eod-file-export.md)
  (first child of this RFC; written before this RFC and folded
  into it retroactively).
- **Related issues:**
  [#329](https://github.com/pedrosakuma/B3MatchingPlatform/issues/329)
  (per-trade audit log infra),
  [#330](https://github.com/pedrosakuma/B3MatchingPlatform/issues/330)
  (EOD fills file export),
  [B3TradingPlatform#274](https://github.com/pedrosakuma/B3TradingPlatform/issues/274)
  (consumer-side D+1 recon tracking).

## 1. Context and motivation

`B3MatchingPlatform` simulates the B3 exchange's matching engine:
EntryPoint SBE inbound on TCP, UMDF over UDP multicast outbound,
ExecutionReports back to originating sessions. That is everything
the engine *broadcasts*. Once a trade leaves the dispatch thread,
the simulator's responsibility for it ends — there is no persistent
trade record beyond the WAL (which is an input-command log for
recovery, not an audit log) and no concept of clearing, settlement,
or reconciliation artifacts.

The downstream stack (`B3TradingPlatform`, plus future consumers
like a clearing simulator, surveillance tool, or back-office
service) needs more than the live UMDF/EntryPoint streams to do
its job. Live streams are inherently:

- **Lossy / consumer-managed.** Multicast can be dropped; sessions
  can disconnect; recovery is the consumer's problem and is
  bounded by UMDF snapshot retention.
- **Unbounded.** A consumer that needs "all trades of day D" has
  no end-of-day handshake on the stream side that says "this set
  is closed and immutable".
- **Without a compliance contract.** Matching does not currently
  promise retention, immutability, or replayability for anything
  it has published.

[ADR 0001](../adr/0001-post-trade-boundary-and-eod-file-export.md)
addressed *one* slice of that gap (EOD fills export) and, in doing
so, introduced the principle that **post-trade is a separate
concern** from matching. This RFC ratifies that principle, draws
the full boundary, and catalogs the decisions that still need to
be made before the post-trade module is complete.

The motivation for writing this as a single umbrella document
(rather than letting ADRs accumulate ad-hoc) is that several of
those decisions are coupled — the audit log shape constrains the
clearing artifact shape; the surveillance feed competes with the
audit log for the dispatcher-thread write budget; the BVBG XML
choice influences whether per-firm split files make sense — and
making them in isolation will produce contradictions.

## 2. Domain model: lifecycle of a trade in the post-trade world

In production B3, a trade goes through (simplified, equities cash):

1. **Execution** — orders cross; matching engine emits trade
   confirmation. *This is everything the simulator currently models.*
2. **Confirmation** — both parties' OMS acknowledge the fill;
   counterparty data is established.
3. **Allocation** — block trades broken down to end clients;
   `allocationId` assigned. *Not relevant for retail order flow.*
4. **Clearing** — central counterparty (B3 BVMF Clearing) novates
   the trade; sides become obligations against the clearing house;
   margin requirements computed.
5. **Settlement** — money and security swap (`DvP` — Delivery
   versus Payment), conventionally T+2 (was T+3 pre-2018, going
   T+1 in some markets). `settlementId` distinct from `tradeId`.
6. **Custody update** — securities land in custody account;
   positions update.
7. **Reporting** — regulatory feeds (CVM, B3 surveillance), tax
   withholding, broker statement generation.
8. **Archive** — long-term retention for audit and dispute
   resolution.

A pragmatic simulator does **not** need to model all of this. The
**minimal viable post-trade module** for this codebase covers
steps 1, partial 7 (statement/audit artifacts), and 8 (retention).
Steps 2–6 are either upstream/sideline of what the simulator
exposes (no real CSDs, no real money) or are downstream
consumers' job (the trading host computes its own positions,
clears against its own simulated clearing facility, etc.).

This RFC's working scope is therefore:

- **In scope:** authoritative per-trade record, EOD artifacts,
  intraday/EOD regulatory feeds (shape only), late corrections /
  bust propagation, retention policy. Everything the simulator
  needs to be a credible source-of-truth for downstream recon.
- **Out of scope (deferred to future RFCs if ever needed):**
  active clearing module (novation, margin, default management),
  custody/CSD integration, settlement simulation (DvP, fails),
  corporate actions impact, tax/withholding, FX, multi-market.

## 3. Boundary diagram

```
                 ┌─────────────────────────────────────────────────┐
                 │              B3MatchingPlatform                 │
                 │                                                 │
   EntryPoint    │   ┌──────────────┐         ┌──────────────────┐ │
   TCP (SBE) ───►│   │  Gateway     │ commands│   ChannelDispatcher
                 │   │  (session    │────────►│   (per channel)  │ │
                 │   │   layer)     │         │                  │ │
                 │   └──────────────┘         │  ┌────────────┐  │ │
                 │           ▲                │  │  Matching  │  │ │
        ER_*     │           │ ER             │  │   Engine   │  │ │
        TCP ◄────│───────────┘                │  └─────┬──────┘  │ │
                 │                            │        │         │ │
                 │                            │  emits │ trades  │ │
                 │                            │        ▼         │ │
                 │                            │  ┌────────────┐  │ │
                 │                            │  │  Sinks     │  │ │
                 │                            │  │ (UMDF, ER, │  │ │
                 │                            │  │  POST-TRADE│◄─┼─┼─── NEW: IPostTradeSink
                 │                            │  └─────┬──────┘  │ │
                 │                            └────────┼─────────┘ │
                 │                                     │           │
                 │                                     ▼           │
                 │                  ┌────────────────────────────┐ │
                 │                  │   B3.Exchange.PostTrade    │ │
   UMDF UDP ◄────│──────────────────│                            │ │
   multicast    │                  │  • per-trade audit log     │ │
                 │                  │  • EOD CSV drop (ADR 0001) │ │
                 │                  │  • clearing artifacts (?)  │ │
                 │                  │  • surveillance feed (?)   │ │
                 │                  └─────────────┬──────────────┘ │
                 │                                │ files / streams│
                 └────────────────────────────────┼────────────────┘
                                                  │
                                                  ▼ filesystem drop /
                                                    optional stream
                            ┌──────────────────────┴──────────────────┐
                            │                                         │
                            ▼                                         ▼
                  ┌──────────────────────┐               ┌───────────────────────┐
                  │  B3TradingPlatform   │               │  surveillance tool    │
                  │  (trading host)      │               │  / clearing simulator │
                  │  • position keeping  │               │  / regulator stub     │
                  │  • risk              │               │  • out of this repo   │
                  │  • own statement     │               │                       │
                  │  • recon vs drop file│               └───────────────────────┘
                  └──────────────────────┘
```

### Ownership table

| Component                           | Owns                                                                          | Talks to                                                                |
|-------------------------------------|-------------------------------------------------------------------------------|-------------------------------------------------------------------------|
| Matching engine                     | order book, matching, authoritative `tradeId` stream                          | Sinks (UMDF, ER, post-trade) on dispatch thread                         |
| Gateway / EntryPoint                | session lifecycle, SBE encode/decode, per-session ER delivery                 | clients (TCP), `ChannelDispatcher`                                      |
| `ChannelDispatcher`                 | per-channel single-writer thread; orchestrates sinks; durability watermarks  | matching engine; sinks; WAL; post-trade sink                            |
| **`B3.Exchange.PostTrade`** (new)   | persisted post-trade artifacts (audit log, drops, eventual feeds)             | filesystem (drops); optional streams; admin HTTP triggers               |
| Trading host (`B3TradingPlatform`)  | position keeping, blotter, intraday risk, consumer-side statement, D+1 recon  | UMDF (live tape); EntryPoint (live ER); post-trade drop directory       |
| Future surveillance / clearing      | their own simulators / stubs                                                  | post-trade drops; **no** simulator-side intraday feed ([ADR 0006](../adr/0006-surveillance-feed.md)); clearing not modelled ([ADR 0005](../adr/0005-clearing-boundary-out-of-scope.md)) |

### Boundary rules (normative)

- Matching engine never reads from post-trade. Information flows
  one way: matching → post-trade.
- Post-trade never blocks the dispatch thread on I/O beyond its
  in-memory append + (optionally) coalesced `fsync`. Heavy work
  (CSV projection, file rotation, signing) happens off the dispatch
  thread, fed by the audit log.
- `/admin/*` HTTP **never** exposes queries over post-trade data.
  It exposes only **triggers and status** for post-trade jobs
  (e.g. "run EOD export now", "status of last export"). Querying
  the data is the consumer's job, against the files post-trade
  produces. *(Rule established by ADR 0001 §1; carried forward.)*
- The post-trade module owns its retention policy independently
  of the WAL. WAL truncation is gated by post-trade's durability
  watermark (ADR 0001 §2, addresses gpt-5.5 review finding).

## 4. Integration channel matrix

The end-to-end picture has more than just "matching emits, trading
consumes". This matrix names every channel by which the matching
side communicates post-trade information to anyone, what each
channel is for, and which RFC decision (if any) is still pending.

| Channel                                              | Direction                | Purpose                                              | Status                                                        |
|------------------------------------------------------|--------------------------|------------------------------------------------------|---------------------------------------------------------------|
| **UMDF** UDP multicast (`Trade_53` and friends)      | matching → all consumers | live public tape, snapshot recovery                  | Exists. Source of truth for *live* tape.                      |
| **EntryPoint `ER_Trade`** TCP                        | matching → owning session| per-session fill notification                        | Exists. Source of truth for *live* per-firm fills.            |
| **Per-trade audit log** (file, internal)             | matching → post-trade    | durable record of every trade for D+0/D+1 artifacts  | Exists. [ADR 0001 §2](../adr/0001-post-trade-boundary-and-eod-file-export.md) (boundary); shape formalised by [ADR 0002](../adr/0002-per-trade-audit-log-shape.md) (fills-only v1); schema-v2 bust + reject-attempt records by [ADR 0008](../adr/0008-late-corrections-and-bust-propagation.md). |
| **EOD CSV drop** (`fills.csv` + `.done`)             | post-trade → consumers   | D+1 recon between trading host and matching          | Exists. [ADR 0001 §3](../adr/0001-post-trade-boundary-and-eod-file-export.md); implementation shipped via #330 series. |
| **EOD BVBG XML envelope** (`fills.xml` + `.done`)    | post-trade → consumers   | wire-compatible with real BVBG ingesters             | Not emitted. [ADR 0003](../adr/0003-bvbg-xml-envelope.md) (Deferred, no consumer). |
| **Settlement instruction file** (per-firm, daily)    | post-trade → clearing    | feed for a clearing simulator (if it exists)         | Not emitted. [ADR 0004](../adr/0004-settlement-cycle-out-of-scope.md) (Rejected — out of scope). |
| **Position aggregate dump** (per firm, per date)     | post-trade → consumers   | shortcut for downstream that does not want to fold trades into positions itself | Not emitted. [ADR 0007](../adr/0007-position-aggregate.md) (Deferred, no consumer). |
| **Surveillance feed** (intraday or EOD batch)        | post-trade → regulator   | regulatory monitoring (manipulation, spoofing, ramping) | No separate artifact. [ADR 0006](../adr/0006-surveillance-feed.md) — surveillance reads `fills.csv` + `amendments.csv`; trade-only detection in scope, spoofing-class out of scope until audit log is extended. |
| **Late corrections / bust artifact**                 | post-trade → consumers   | propagate a post-EOD bust to consumers that already consumed the drop | Spec'd. [ADR 0008 §4](../adr/0008-late-corrections-and-bust-propagation.md) — `amendments.csv` + `.done`, regenerated in full from the audit log; implementing PR pending. |
| **Operator trade-correction / bust command**         | operator/admin → dispatcher → post-trade | authoritative inbound channel for trade busts; matching engine is unaware of busts otherwise, so they must enter post-trade through this explicit path and be appended as correction events on the audit stream | Spec'd. [ADR 0008 §2](../adr/0008-late-corrections-and-bust-propagation.md) — new `POST /admin/post-trade/bust` endpoint; legacy `/channel/{ch}/trade-bust/{tradeId}` kept as replay-only. Implementing PR pending. |
| **Reference-data / instrument master drop** (per date) | post-trade/instruments → consumers | day-locked security metadata so D+1 consumers can interpret `securityId` ↔ symbol consistently with the matching side that produced the fills | Deferred. Carried by [ADR 0003](../adr/0003-bvbg-xml-envelope.md) §2.3 if BVBG ships; otherwise a future ADR. |
| **Admin HTTP triggers**                              | operator → post-trade    | "run EOD now", "regen date X", "report status"       | Will land alongside #330; covered by ADR 0001 §3.             |

The matrix is **deliberately not exhaustive of all possible
artifacts.** Anything not listed is currently out of scope for the
simulator (corporate actions, tax, FX, etc.) and would need its
own RFC entry before any implementation.

## 5. Identity and numbering

The simulator emits `tradeId` and `orderId`; everything else is
either derived or absent.

| Identifier        | Source                                                                | Used where                                                                |
|-------------------|-----------------------------------------------------------------------|---------------------------------------------------------------------------|
| `orderId`         | engine monotonic per channel                                          | engine internals, ER, audit log (for `buyClOrdId` / `sellClOrdId` cross-ref) |
| `tradeId`         | engine monotonic per channel                                          | UMDF `Trade_53`, ER, audit log, EOD drop, trade-bust endpoint             |
| `enteringFirm`    | gateway / firm registry                                               | UMDF, ER, audit log (`buyFirm` / `sellFirm`)                              |
| `ClOrdID`         | client-supplied (namespaced by firm)                                  | ER, audit log                                                             |
| `allocationId`    | **not modelled**                                                      | n/a (block trades / allocations are not simulated)                        |
| `settlementId`    | **not modelled**                                                      | n/a (settlement is not simulated)                                         |
| `confirmationId`  | **not modelled**                                                      | n/a (post-execution confirmation step is not simulated)                   |

Decisions deferred to ADRs:

- **ADR 0002** must decide whether the audit log carries any
  derived identifier beyond `tradeId` (e.g. a per-day sequence
  number useful for sparse indexing). Probably not necessary if
  `tradeId` + `ts` is unique. *(Decided in [ADR 0002 §1](../adr/0002-per-trade-audit-log-shape.md#1-record-shape):
  no extra derived identifier; `tradeId` + `transactTimeNanos` is
  sufficient.)*
- ~~**ADR 0004** (if we ever model settlement) would need to define
  `settlementId` and its relation to `tradeId` (1:1 in the simple
  case, 1:N if netting is modelled).~~ *Closed by
  [ADR 0004](../adr/0004-settlement-cycle-out-of-scope.md):
  settlement is out of scope; `settlementId` stays unallocated.*

## 6. Time model

ADR 0001 §2 picked **UTC business date** as the trading-date
boundary for the audit log and the EOD drop. This RFC ratifies
that choice as the project-wide default for all post-trade
artifacts (i.e. all future EOD files, all retention windows, all
filename `YYYY-MM-DD` placeholders mean UTC midnight to UTC
midnight).

Rationale and trade-offs:

- **Pro:** deterministic, recoverable across restart without
  needing to remember "what session were we in", no DST trouble.
- **Pro:** simpler tests; tests can drive the clock with `UtcNow`.
- **Con:** does not match B3's real session boundary
  (`America/Sao_Paulo` ≈ UTC-3). A consumer that compares
  matching's `fills-2026-05-15.csv` against real B3 BVBG files
  for the same business date will find a few hours of
  morning/evening trades on the "wrong" side. Acceptable for a
  simulator; real-fidelity is out of scope.
- **Con:** future settlement T+2 logic, if added, would need to
  decide whether `T` is UTC-business-date or
  São-Paulo-business-date; ~~**ADR 0004** would resolve that.~~
  *Moot: closed by [ADR 0004](../adr/0004-settlement-cycle-out-of-scope.md)
  — no settlement is modelled.*

If at any point B3 session-time boundaries become important
(e.g. a downstream consumer is doing strict same-day diff
against real BVBG drops), a future ADR can switch the
business-date function. The file format does not constrain that
choice.

## 7. Failure modes the post-trade module must handle

This section enumerates the failure modes the post-trade module
**must** be designed against. Each one is a constraint on the
relevant follow-up ADR.

| Failure                                                                                       | Constraint                                                                                                                   | Owning ADR     |
|-----------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------|----------------|
| Host crashes between trade emission and audit fsync                                           | Audit-durability watermark must gate WAL truncation; restart replays beyond-watermark commands into audit-only mode (no ER republish). | ADR 0001 §2 (existing); ADR 0002 formalizes the protocol. |
| Disk full during EOD export                                                                   | Staging file + atomic rename; previous successful drop preserved; failure surfaces via HTTP, not via partial file.            | ADR 0001 §3 (existing). |
| Audit log CRC mismatch discovered during projection                                           | Export aborts; no file overwritten; operator gets error pointing at offending offset. Manual remediation: read up to bad offset + report gap. | ADR 0001 §3 (existing); ADR 0002 to spell out remediation tools. |
| Late correction: trade bust arrives *after* the EOD drop was published                        | Need an amendment artifact (e.g. `amendments-2026-05-15.csv` with a `cancelTradeId` column) and a rule for how the consumer reconciles it against the original `fills.csv`. **Open: amendment file vs full rewrite of the day's drop with a new sequence number.** | ADR 0008 (new). |
| Audit log file lost (operator error, filesystem corruption beyond CRC recovery)               | Day's drop cannot be regenerated. Operator must restore from backup. The retention/backup story is part of the audit-log design. | ADR 0002.      |
| Post-trade sink falls behind matching's emission rate (back-pressure on the dispatch thread) | Sink must have a bounded in-memory queue; if it fills, the choice is (a) block the dispatch thread (back-pressure flows back to the engine, ultimately to the gateway throttle), (b) drop audit records (unacceptable for an audit log), or (c) spill to a temporary disk buffer. **Decision pending.** | ADR 0002.      |
| Day rolls over at UTC midnight while a trade is in flight                                     | Trade lands in the day matching considered current at `OnTrade` invocation time (resolved by the dispatcher's monotonic clock). Tie-breaking is deterministic. | ADR 0002.      |
| Trading host's recon finds a mismatch                                                         | Recon failure surfaces on the trading-host side; matching's responsibility ends at producing the deterministic `fills.csv`. Cross-repo coordination: a future RFC in `B3TradingPlatform` covers what trading does with mismatches. | n/a (out of repo). |
| Pre-EOD trade bust: operator busts a trade *before* the EOD drop is published                 | ~~Bust must enter post-trade through the operator/admin channel (see §4) and be appended as a correction event on the audit stream **before** the EOD projection runs; the projection then folds corrections in so the published `fills.csv` reflects the corrected state and no amendment file is needed for same-day busts.~~ **Constraint reversed by [ADR 0002 §7](../adr/0002-per-trade-audit-log-shape.md#7-correction-events-out-of-scope-for-v1-reverses-rfc-7-pre-eod-constraint):** v1 audit log is fills-only with no correction-event variant; pre-EOD busts produce a UMDF `TradeBust_57` frame only and are not folded into the EOD CSV. Re-promoted to ADR 0008 along with post-EOD bust semantics. | ~~ADR 0002 (audit shape must carry correction events); ADR 0008 (semantics).~~ ADR 0008 (both shape extension and semantics, when written). |
| Duplicate bust / bust of unknown or already-busted trade                                      | Operator command must be idempotent on `tradeId`: repeated bust of the same trade is a no-op (logged), bust of an unknown or already-busted trade is rejected at the admin layer with a clear error. The audit log must record the *attempt* so the operator audit trail is complete even when the command was a no-op. | ADR 0008. |
| Concurrent / repeated EOD export for the same date (scheduler fires while operator rerun is in flight, or two operator reruns overlap) | Export must be single-flight per `(channel, date)`; second concurrent request is rejected with HTTP 409 rather than racing the first. The `.done` sentinel must additionally bind to the exact data file via a generation token or content hash, so consumers that see a `.done` cannot read a `fills.csv` from a different run. | ADR 0001 §3 (refinement); ADR 0002 if the generation token is derived from the audit watermark. |
| ~~Future ADR 0004 (settlement) introduces fail-to-deliver scenarios~~ | ~~Out of scope of this RFC; deferred entirely.~~ *Closed by [ADR 0004](../adr/0004-settlement-cycle-out-of-scope.md): no settlement modelled, so no fail-to-deliver scenarios exist.* | [ADR 0004](../adr/0004-settlement-cycle-out-of-scope.md). |

## 8. ADR roadmap

Living catalog. Each entry is a planned follow-up ADR; status
updates as ADRs land or are explicitly rejected.

| #         | Title                                                  | Blocks / unblocks                                                          | Status                          |
|-----------|--------------------------------------------------------|----------------------------------------------------------------------------|---------------------------------|
| ADR 0001  | Post-trade boundary + EOD file export                  | (already accepted; first child of this RFC)                                | **Accepted** (pre-RFC)          |
| [ADR 0002](../adr/0002-per-trade-audit-log-shape.md) | Per-trade audit log shape                              | Implementation already on `main` (#329 series). Choice: **fills-only** in v1 — order-lifecycle extension deferred to a follow-up ADR if/when ADR 0006 needs it. Unblocks ADR 0008. | **Accepted** (retroactive).     |
| [ADR 0003](../adr/0003-bvbg-xml-envelope.md) | BVBG XML envelope: when and how                        | Optional alternative format for the EOD drop. Non-blocking; deferred until a consumer asks for XML. May also subsume the reference-data drop row in §4 if revisited. | **Deferred** (no consumer).     |
| [ADR 0004](../adr/0004-settlement-cycle-out-of-scope.md) | Settlement cycle model (or lack of)                    | Records as explicit decision that no settlement cycle is modelled. Aligned with §9. Coupled with ADR 0005. | **Rejected** (out of scope).    |
| [ADR 0005](../adr/0005-clearing-boundary-out-of-scope.md) | Clearing boundary                                      | Records as explicit decision that no `B3.Exchange.Clearing` module exists. Coupled with ADR 0004. | **Rejected** (out of scope).    |
| [ADR 0006](../adr/0006-surveillance-feed.md) | Surveillance / regulatory feed shape                   | No separate artifact; surveillance reads `fills.csv` + `amendments.csv`. v1 scope is **trade-only** detection (wash trades, self-trades, price-impact); spoofing / layering / quote-stuffing detection deferred to a follow-up ADR that extends the audit-log shape from ADR 0002. | **Accepted**.                   |
| [ADR 0007](../adr/0007-position-aggregate.md) | Position aggregate per firm                            | Optional convenience artifact; deferred until a named consumer asks. Pure projection of `fills.csv` + `amendments.csv`, no new authoritative state. | **Deferred** (no consumer).     |
| [ADR 0008](../adr/0008-late-corrections-and-bust-propagation.md) | Late corrections / bust propagation                    | How a post-EOD trade bust is propagated; amendment file format. Critical the moment the trade-bust endpoint is exercised post-EOD. Now also owns the audit-log schema-v2 extension (correction record variant), since ADR 0002 deferred that to here. | **Accepted** (spec; no implementing PR yet). |

**Sequencing:**

1. Land ADR 0002 first (blocks #329, which blocks #330). *(Done.)*
2. ADR 0008 next, because the moment #330 ships and is used in
   anger, late corrections become a real concern. *(Done as spec;
   implementing PR pending.)*
3. ADRs 0003 / 0004 / 0005 / 0006 / 0007 have all landed as
   explicit *Accepted / Deferred / Rejected* decisions so the
   roadmap is no longer a backlog of unanswered questions. None
   ships code in this repo today; if a consumer materialises, the
   relevant ADR is superseded by a new implementing ADR.

## 9. Explicitly out of scope of this RFC

These are *not* in the post-trade architecture this RFC describes.
Listed so future contributors do not assume they were missed.

- **Active clearing module.** No novation simulation, no margin
  computation, no default management, no clearing-house
  obligations. Decided by [ADR 0005](../adr/0005-clearing-boundary-out-of-scope.md)
  (rejected — explicit non-feature).
- **Custody / CSD integration.** No simulated securities depository,
  no DvP, no settlement fails. Decided by [ADR 0004](../adr/0004-settlement-cycle-out-of-scope.md)
  (rejected — explicit non-feature).
- **Corporate actions.** No dividends, splits, mergers, rights
  issues impacting positions or trade prices. Would need a
  separate RFC if ever in scope.
- **Tax / withholding.** No IR-Fonte calculation, no DARF
  generation, no broker-side tax reporting.
- **FX / multi-currency settlement.** Single-currency assumed.
- **Multi-host clustering of the post-trade module.** Same
  single-host assumption as the rest of the simulator. If
  multi-host ever lands, a successor RFC will need to address
  partitioning / merging of audit logs.
- **Cryptographic signing of drop files.** Listed as a deferred
  open question in ADR 0001 §"Open questions"; not promoted here.
  A future ADR may add it without changing this RFC's structure.
- **Real-time queryable trade history.** Antithetical to the
  file-based boundary. Operator HTTP exposes triggers and
  status, never queries (ADR 0001 §1; reiterated in §3 of this RFC).
- **Bilateral matching against real B3 BVBG files.** The
  simulator is not trying to be wire-compatible with a real
  clearing house. UTC business date + CSV-first format reflects
  that explicitly.

## Consequences

### Positive

- All future post-trade work has a single document to consult
  for "is this in scope" and "where does this decision belong".
- ADRs that would otherwise have been written in isolation now
  share a coherent set of constraints (failure modes §7,
  ownership table §3, time model §6).
- Downstream `B3TradingPlatform` has a roadmap, not just a
  one-off file format spec, to plan against.
- The "matching never reads from post-trade" rule is now
  normative; PR reviewers can cite it directly.

### Negative / costs

- One more document to keep in sync. The *ADR roadmap* table
  (§8) and the *integration channel matrix* (§4) will drift
  if not updated when ADRs land. Mitigation: PRs that land an
  ADR under this RFC must also update §4 and §8 in the same
  commit.
- The RFC will need to be revisited (status → Living) the first
  time a planned-deferred decision (clearing, settlement,
  corporate actions) actually has to happen, because the §9
  Out-of-scope list is part of the contract.

## References

- [ADR 0001](../adr/0001-post-trade-boundary-and-eod-file-export.md) —
  first child of this RFC; rationale for file-based EOD export.
- `docs/RUNBOOK.md` §2 — operational admin surface; post-trade
  is explicitly *not* on that surface.
- `docs/EXCHANGE-SIMULATOR.md` — engine configuration; the
  `postTrade` config block will land alongside #329 / #330.
- `src/B3.Exchange.Core/ChannelWriteAheadLog.cs` — WAL coverage
  and exclusions, cited throughout §3 and §7.
- `src/B3.Exchange.Core/ChannelDispatcher.Persistence.cs` —
  current snapshot/truncation flow; the audit-durability
  watermark will hook in here.
- B3 BVBG family (production analog) — shape model for §4
  artifacts; not normative.
