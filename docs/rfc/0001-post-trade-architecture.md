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
| Future surveillance / clearing      | their own simulators / stubs                                                  | post-trade drops; possibly an intraday feed (decision: ADR 0006)        |

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
| **Per-trade audit log** (file, internal)             | matching → post-trade    | durable record of every trade for D+0/D+1 artifacts  | ADR 0001 §2; implementation tracked in #329. Future ADR 0002 will formalize shape. |
| **EOD CSV drop** (`fills.csv` + `.done`)             | post-trade → consumers   | D+1 recon between trading host and matching          | ADR 0001 §3; implementation tracked in #330.                  |
| **EOD BVBG XML envelope** (`fills.xml` + `.done`)    | post-trade → consumers   | wire-compatible with real BVBG ingesters             | Deferred. Future ADR 0003.                                    |
| **Settlement instruction file** (per-firm, daily)    | post-trade → clearing    | feed for a clearing simulator (if it exists)         | Deferred. Future ADR 0004 (may also decide *not* to ship).    |
| **Position aggregate dump** (per firm, per date)     | post-trade → consumers   | shortcut for downstream that does not want to fold trades into positions itself | Deferred. Future ADR 0007. |
| **Surveillance feed** (intraday or EOD batch)        | post-trade → regulator   | regulatory monitoring (manipulation, spoofing, ramping) | Deferred. Future ADR 0006.                                  |
| **Late corrections / bust artifact**                 | post-trade → consumers   | propagate a post-EOD bust to consumers that already consumed the drop | Deferred. Future ADR 0008.                  |
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
  `tradeId` + `ts` is unique.
- **ADR 0004** (if we ever model settlement) would need to define
  `settlementId` and its relation to `tradeId` (1:1 in the simple
  case, 1:N if netting is modelled).

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
  São-Paulo-business-date; **ADR 0004** would resolve that.

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
| Future ADR 0004 (settlement) introduces fail-to-deliver scenarios                             | Out of scope of this RFC; deferred entirely.                                                                                  | ADR 0004 if/when written. |

## 8. ADR roadmap

Living catalog. Each entry is a planned follow-up ADR; status
updates as ADRs land or are explicitly rejected.

| #         | Title                                                  | Blocks / unblocks                                                          | Status                          |
|-----------|--------------------------------------------------------|----------------------------------------------------------------------------|---------------------------------|
| ADR 0001  | Post-trade boundary + EOD file export                  | (already accepted; first child of this RFC)                                | **Accepted** (pre-RFC)          |
| ADR 0002  | Per-trade audit log shape                              | Blocks #329 implementation; unblocks ADR 0008.                             | Planned, **blocks #329**.       |
| ADR 0003  | BVBG XML envelope: when and how                        | Optional alternative format for the EOD drop. Non-blocking; written when a consumer asks for XML. | Planned, not blocking.          |
| ADR 0004  | Settlement cycle model (or lack of)                    | Decides whether the simulator ever emits settlement-instruction artifacts. Most likely outcome: *no, out of scope* — but recorded as an explicit decision. | Planned, low priority.          |
| ADR 0005  | Clearing boundary                                      | Decides whether a `B3.Exchange.Clearing` module exists. Coupled with ADR 0004. | Planned, low priority.          |
| ADR 0006  | Surveillance / regulatory feed shape                   | Decides intraday-stream vs EOD-batch surveillance feed; format, retention. | Planned, no immediate consumer. |
| ADR 0007  | Position aggregate per firm                            | Optional shortcut for consumers that do not want to derive positions from the fills drop themselves. | Planned, no immediate consumer. |
| ADR 0008  | Late corrections / bust propagation                    | How a post-EOD trade bust is propagated; amendment file format. Critical the moment the trade-bust endpoint is exercised post-EOD. | Planned, **blocks any real D+1 recon flow that handles busts**. |

**Sequencing:**

1. Land ADR 0002 first (blocks #329, which blocks #330).
2. ADR 0008 next, because the moment #330 ships and is used in
   anger, late corrections become a real concern.
3. ADRs 0003–0007 are written when a concrete consumer asks
   for them. Until then, they remain "Planned" with no PR.

## 9. Explicitly out of scope of this RFC

These are *not* in the post-trade architecture this RFC describes.
Listed so future contributors do not assume they were missed.

- **Active clearing module.** No novation simulation, no margin
  computation, no default management, no clearing-house
  obligations. Deferred to ADR 0005 (which may decide to ship
  nothing).
- **Custody / CSD integration.** No simulated securities depository,
  no DvP, no settlement fails. Deferred to ADR 0004.
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
