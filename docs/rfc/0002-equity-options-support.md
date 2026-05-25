# RFC 0002 — Equity options support (MVP, umbrella)

- **Status:** Draft (living once accepted — the *ADR roadmap* and
  *Issue roadmap* sections evolve as follow-up ADRs land and issues
  close)
- **Date:** 2026-05-25
- **Supersedes:** —
- **Related ADRs:**
  [ADR 0012 — Exchange-day boundary](../adr/0012-exchange-day-boundary.md)
  (defines what is in scope for the venue tier),
  [ADR 0005 — Clearing boundary out of scope](../adr/0005-clearing-boundary-out-of-scope.md)
  (defines what is delegated to the clearing simulator),
  [ADR 0009 — Single-writer threading model](../adr/0009-single-writer-threading-model.md)
  (per-channel isolation guarantee leveraged by the options channel).
- **Related issues:** umbrella issue *(to be filed alongside this RFC)*
  tracking OPT-01..OPT-10 / OPT-12 below.
- **Related docs:**
  [`docs/B3-ENTRYPOINT-COMPLIANCE.md`](../B3-ENTRYPOINT-COMPLIANCE.md)
  (where new GAP-31 / GAP-32 rows will land),
  [`docs/RUNBOOK.md`](../RUNBOOK.md) (where MM session tuning
  guidance lands).

## 1. Context and motivation

`B3MatchingPlatform` today models equities (channel EQT) and a
synthetic derivatives stub (channel DRV, issue #225). Equity options
— the listed call/put series of the form `PETRH320`,
`VALEC380`, etc., which on the real B3 venue trade on a dedicated
PUMA market data channel — are not represented at all:

- `Instrument.cs` has `SecurityType` but no `StrikePrice`,
  `ExpirationDate`, `PutOrCall`, `ExerciseStyle`,
  `UnderlyingSecurityId`, or `ContractMultiplier`.
- `UmdfWireEncoder.WriteSecurityDefinitionFrame()` does not emit any
  of the option fields that the UMDF schema v2.2.0
  `SecurityDefinition_12` template defines (`strikePrice`,
  `putOrCall`, `exerciseStyle`, `noUnderlyings`, `contractMultiplier`,
  `optPayoutType`, `maturityMonthYear`,
  `securityValidityTimestamp`).
- The `PhaseScheduler` and `DailyResetScheduler` have no notion of
  `ExpirationDate` — a configured option series whose expiry has
  passed would continue cycling through trading phases forever.

The schema layer is **already ready**: every wire field needed for
options is defined in the vendored
`schemas/b3-market-data-messages-2.2.0.xml`
(`SecurityDefinition_12`, `ExerciseStyle`, `PutOrCall`,
`OptPayoutType`, the `noUnderlyings` group). The vendored
EntryPoint schema v8.4.2 has no `MassQuote` template (the
`QuoteRequest`/`Quote`/`QuoteCancel` templates it carries are for
Termo/Forward, not options market-making), so market-makers in the
MVP submit orders via the regular `SimpleNewOrder` /
`NewOrderSingle` / `OrderCancelReplaceRequest` paths with a
calibrated per-session throttle.

The architectural question this RFC answers is therefore not "how
do we extend the engine?" — the engine is instrument-agnostic
(matching by `SecurityId`, single-writer per channel per ADR 0009)
and works for options out of the box. It is "**what metadata,
config, and lifecycle plumbing do we add so a real options
consumer (e.g. `B3MarketDataPlatform` or a downstream analytics
stack) can build an option chain and observe a realistic options
day**".

## 2. Scope (and explicitly out of scope)

### In scope (ADR 0012, venue side, equity options only)

- **Instrument model** — strike, expiry, P/C, exercise style,
  underlying SecurityId / Symbol, contract multiplier, payout type
  on `Instrument` + `InstrumentLoader` JSON.
- **UMDF `SecurityDefinition_12` emission** — populate the option
  fields already defined in schema v2.2.0 so consumers can build
  the option chain.
- **Dedicated UMDF channel `OPT`** — separate `ChannelDispatcher`
  with its own multicast group, mirroring B3's real topology
  (options ride a different multicast channel than the underlying
  equities). Full state isolation (ADR 0009).
- **Expiry-aware phase scheduling** — series with
  `ExpirationDate < today` are auto-Closed at startup; series whose
  last-trading-day is reached during the session are auto-Closed at
  the end of that day's `Close` phase via a `SecurityStatus_3`
  `CLOSE` event.
- **Quantity in contracts** — `LotSize` semantics for an option
  series is "1 contract" with `ContractMultiplier` carried as
  metadata for downstream consumers. The book matches in contracts;
  the multiplier is informational on the venue side (notional
  computation lives in clearing / analytics — ADR 0005, ADR 0012).
- **Market-maker session tuning** — MM sessions use the existing
  per-session `SessionPolicyConfig.MaxOrderRatePerSecond`
  (`HostConfig.cs:215-237`) to admit a higher inbound rate. No new
  code in the matching engine or gateway is required; this is a
  configuration + documentation deliverable.
- **`PriceBand_22` emission** for both EQT and OPT channels (split
  out as a companion trail; see §6).

### Out of scope (delegated elsewhere)

Per ADR 0012's exchange-day rule, every concern below is **not** in
this repo. The cell below names the companion repo / module that
owns it.

| Concern | Belongs to |
| --- | --- |
| Pricing / greeks / implied vol / theoretical price | Hypothetical `B3OptionsAnalytics` consuming UMDF |
| Exercise (auto-exercise of ITM at expiry), assignment, exercise notice | Clearing simulator (ADR 0005) |
| Expiry-day settlement (cash or physical) | Clearing simulator (ADR 0005) |
| Position keeping, margin, guarantee fund | Clearing simulator (ADR 0005) |
| Index options on IBOV, options on futures (DI, dólar) | Future RFC — equity options first per Q1 (see §7) |
| `MassQuote` / `QuoteRequest` market-making protocol | Deferred — blocked on B3 publishing a `MassQuote` template in a future EntryPoint schema release. Tracked as OPT-08 research issue. |
| Market-maker contract enforcement (spread, presence, minimum quantity per side) | Analytics / surveillance consumer (out per ADR 0012 — the venue accepts orders; obligation checking is a post-trade analytical concern) |
| Series naming convention encoder (`PETRH320` ↔ `PETR4`/2026-08/call/32.00) | Convenience tooling — out of scope for the venue. Consumers parse the symbol or read `SecurityDefinition`. |
| OCO / brackets / option spreads / trailing stops | `B3TradingPlatform` (broker tier, ADR 0012) |
| User-Defined Spreads (UDS, GAP-29) | Out of MVP — separate issue if/when demand surfaces |

## 3. Architecture

### 3.1 Channel topology

Add a third channel group, `OPT`, alongside the existing `EQT`
(issue #225 baseline) and `DRV` (synthetic futures). The host's
existing `channels[]` array in `exchange-simulator.json`
configures the new channel with its own multicast group/port/TTL
and its own instrument file (`config/instruments-opt.json`). The
`ChannelDispatcher` is fully isolated per ADR 0009:

- Own `MatchingEngine` (single-threaded by construction).
- Own `OrderRegistry`, `_openOrdersByFirm`, `_lastTradePriceBySecurity`.
- Own inbound `Channel<WorkItem>` + dispatch thread.
- Own `IUmdfPacketSink` (distinct multicast group).
- Own WAL + snapshot + persister.
- Own `ChannelMetrics`.

This means options trading cannot perturb equities trading and
vice versa — no shared mutable state across channels.

**Stop orders and cross-channel pricing:** stops in the current
engine fire on the last trade of the **same** `SecurityId` they
were submitted on, via the channel-local
`_lastTradePriceBySecurity` map. That semantics is preserved for
options: a stop on `PETRH320` fires on the last trade of
`PETRH320`, not on the last trade of the underlying `PETR4`
(which lives on a different channel and is therefore inaccessible
from this dispatcher). Cross-channel triggers (e.g. "stop in
option series triggered by underlying crossing X") are an
explicit non-goal for the MVP — they would require either a
bridge component or merging option and equity channels, both of
which violate ADR 0009's single-writer guarantee. If a concrete
consumer needs that, it is a separate follow-up RFC.

### 3.2 Instrument model extensions

Add to `Instrument` and `InstrumentLoader`:

| New field | JSON name | Type | Required when | Notes |
| --- | --- | --- | --- | --- |
| `StrikePrice` | `strikePrice` | `decimal` | `securityType ∈ {OPT, INDEXOPT, FOPT}` | Stored in human units; encoded to UMDF `Price` mantissa at emit time per existing convention. |
| `ExpirationDate` | `expirationDate` | `DateOnly` (`YYYY-MM-DD`) | option types | Last trading day; auto-Close at end of this day's Close phase. |
| `PutOrCall` | `putOrCall` | enum `{Put, Call}` | option types | Maps to UMDF enum (PUT=0, CALL=1). |
| `ExerciseStyle` | `exerciseStyle` | enum `{European, American}` | option types | Maps to UMDF enum (EUROPEAN=0, AMERICAN=1). Informational on venue side; clearing handles exercise. |
| `UnderlyingSecurityId` | `underlyingSecurityId` | `long` | option types | Numeric SecurityID of the underlying (must match a configured equity instrument on the EQT channel). |
| `UnderlyingSymbol` | `underlyingSymbol` | `string` | option types | Convenience ticker (e.g. `PETR4`). |
| `ContractMultiplier` | `contractMultiplier` | `decimal` | option types | Default 100 for equity options. Carried as metadata only; the book matches in contracts. |
| `OptPayoutType` | `optPayoutType` | enum `{Vanilla, Capped, Binary}` | option types, optional | Defaults to `Vanilla`. |

Validation in `InstrumentLoader`:

- `minPx` may be `0` or below the equity-style tick for near-zero
  options (deep OTM); current `minPx > 0` check is relaxed for
  option types. See OPT-05.
- `expirationDate` must be a future date at load time (or rejected
  / auto-Closed per OPT-03 policy).
- `underlyingSecurityId` is **not** cross-validated against the EQT
  channel at load time (channels are independent); it is metadata
  for consumers.
- `contractMultiplier > 0`.

`MatchingEngine` is unchanged. The engine treats an option as any
other instrument indexed by `SecurityId`. No bump of
`EngineStateSnapshot.CurrentVersion` is required — the engine
holds no option-specific state (the option-ness is metadata on
the instrument, which is not part of engine state).

### 3.3 SecurityDefinition encoder

Extend `UmdfWireEncoder.WriteSecurityDefinitionFrame()` and the
matching `WireOffsets` constants to populate the option fields
already declared in the schema:

- `strikePrice` (FIX 202)
- `putOrCall` (FIX 201)
- `exerciseStyle` (FIX 1194)
- `maturityMonthYear` (FIX 200) — derived from `ExpirationDate`
- `securityValidityTimestamp` (FIX 6938) — derived from
  `ExpirationDate` end-of-day in venue timezone
- `contractMultiplier` (FIX 231)
- `optPayoutType` (FIX 1482) — emit only for option types
- `noUnderlyings` group (FIX 711) with `underlyingSecurityID` (309)
  and `underlyingSymbol` (311)

The vendored UMDF schema is **not** modified — every field above
is already defined. `InstrumentDefinitionPublisher` is updated to
pass the new fields through.

### 3.4 Expiry-aware phase scheduling

Two scenarios:

1. **Series already expired at host startup.** `InstrumentLoader`
   (or a new `ExpiryFilter` invoked from `ExchangeHost`) detects
   `ExpirationDate < today` and marks the instrument as
   permanently `Close`. The matching engine never accepts orders
   for these `SecurityId`s, and the `PhaseScheduler` does not
   cycle them through `Open`. A `SecurityStatus_3 CLOSE` is
   emitted once at boot (so consumers building chains from the
   live feed see the terminal state).

2. **Series expires during a running session.** Today's
   `DailyResetScheduler` (`src/B3.Exchange.Host/DailyResetScheduler.cs`)
   gets a follow-up hook that, at the end of the
   day's `Close` phase, checks every option series with
   `ExpirationDate == today` and emits the terminal
   `SecurityStatus_3 CLOSE`, marking the series as expired so
   tomorrow's startup follows the path in (1).

Per ADR 0012, the simulator stops at the last `SecurityStatus`.
Auto-exercise, assignment, expiry settlement, and exercise
notices are all out of scope.

### 3.5 Quantity semantics: contracts, not shares

Real B3 options trade in contracts where 1 contract conventionally
represents 100 underlying shares (`contractMultiplier`). The book
publishes prices per share and quantities in contracts. The MVP
adopts that semantics literally:

- `Instrument.LotSize` for an option series defaults to `1`
  (1 contract minimum / round-lot increment).
- `ContractMultiplier` is metadata only — not used by the
  matching engine. Notional value of a fill (price × quantity ×
  multiplier) is the consumer's responsibility (clearing /
  analytics).
- The wire-level `Quantity` and `Price` encodings on UMDF MBO /
  Trade are unchanged — the multiplier is conveyed once, via
  `SecurityDefinition_12`.

This avoids touching `InstrumentTradingRules.LotSize` semantics
for non-option instruments and keeps the matching engine
oblivious to instrument type.

### 3.6 Market-maker session tuning

Real-world MM throughput on an active options day can be
1000s of order events per second per session (Q2 — see §7 — for
why this matters even without `MassQuote`). The gateway already
supports per-session throttle calibration:

- `SessionPolicy.MaxOrderRatePerSecond` (`Firm.cs:21-28`) is per
  `SessionCredential`.
- `SessionPolicyConfig.MaxOrderRatePerSecond`
  (`HostConfig.cs:215-237`) exposes this in `exchange-simulator.json`
  under `sessions[].policy.maxOrderRatePerSecond`.
- The throttle lives in `FixpSession.HeaderValidation` — entirely
  in the gateway, before `ChannelDispatcher.Enqueue`. The matching
  engine never sees throttle state.

The MVP deliverable is **documentation** (OPT-12): a RUNBOOK section
describing recommended throttle settings for MM sessions vs regular
client sessions (typical: MM ~5000-10000/s; client ~200/s) and
guidance on per-firm `maxOpenOrdersPerFirm` (GAP-21a) calibration.

No new code in the matching engine. No new code in the gateway.

### 3.7 Persistence and snapshot

`BinaryChannelStateSnapshotCodec` (`ChannelStateSnapshot.CurrentVersion`)
is **not bumped**. Rationale: the engine state for an option
series is the same shape as for any other instrument (resting
orders, stops, sequence numbers); option-ness lives entirely in
the per-channel instrument catalog, which is loaded from
`instruments-opt.json` at boot, not from the snapshot.

WAL replay is unchanged — `SimpleNewOrder` / `NewOrderSingle` /
`OrderCancelReplaceRequest` / `MassCancel` for option `SecurityId`s
serialize the same way as for equities.

### 3.8 Documentation alignment

`docs/B3-ENTRYPOINT-COMPLIANCE.md` lines 153-156 currently read:

> Following ADR 0012 — Exchange-day boundary, order-handling
> protections (STPC, Market Protections) and matching-side
> extensions (Sweep & Cross) are in-scope; pricing / greeks /
> settlement of options are out-of-scope (delegated to companion
> repos).

This conflates *pricing/greeks/settlement* (which ADR 0012 does
exclude) with *options as instruments on the venue* (which ADR
0012 includes). The wording is rewritten as part of OPT-06 to
say:

> Following ADR 0012 — Exchange-day boundary, order-handling
> protections (STPC, Market Protections), matching-side extensions
> (Sweep & Cross), and **options as listed instruments — order
> entry, matching, market data emission, expiry-driven
> SecurityStatus** — are in-scope. Pricing / greeks / settlement /
> exercise / assignment of options are out-of-scope (delegated to
> companion repos: hypothetical `B3OptionsAnalytics` for
> pricing, clearing simulator per ADR 0005 for exercise and
> settlement). Market-maker contract enforcement (spread,
> presence, minimum quantity) is out — the venue accepts orders;
> contract supervision is a post-trade analytics concern.

Two new GAP rows are added in the same edit:

- **GAP-31** — `SecurityDefinition_12` does not emit option fields.
  Severity: high. Issue: OPT-02.
- **GAP-32** — Expiring option series are not automatically moved
  to `Close` based on `ExpirationDate`. Severity: medium. Issue:
  OPT-03.

## 4. Trail decomposition (fleet plan — AGENTS.md §3)

The work decomposes into trails per AGENTS.md fleet rules. A
trail is paralellizable when it does **not** touch
`Commands.cs` enums, `EngineStateSnapshot.cs`, or vendored SBE
schemas. Below, T-1 unlocks every other trail; T-2..T-5 then run
in parallel.

| # | Trail | Touches | Depends on | Parallelizable with |
| --- | --- | --- | --- | --- |
| T-1 | Instrument model + validation (OPT-01, OPT-05, OPT-09) | New fields on `Instrument` and `InstrumentLoader`; validator relaxation; loader unit tests | — | — (gates everything) |
| T-2 | UMDF SecurityDefinition encoder (OPT-02) | `UmdfWireEncoder`, `WireOffsets`, `InstrumentDefinitionPublisher`; encoder unit tests | T-1 | T-3, T-4, T-5, T-7, T-10 |
| T-3 | Expiry-aware phase scheduling (OPT-03) | `PhaseScheduler`, `DailyResetScheduler`, startup hook in `ExchangeHost` | T-1 | T-2, T-4, T-5, T-7, T-10 |
| T-4 | OPT channel wiring + sample config (OPT-04) | `config/instruments-opt.json`, `config/exchange-simulator.json` channel entry, host integration test | T-1 | T-2, T-3, T-5, T-7, T-10 |
| T-5 | Compliance doc rewrite + GAP-31/32 (OPT-06) | `docs/B3-ENTRYPOINT-COMPLIANCE.md` only — pure docs | — | T-1..T-4, T-7, T-10 |
| T-7 | PriceBand encoder + emission (OPT-07) | `UmdfWireEncoder`, `WireOffsets`, an emission policy on the host | — | T-1..T-5, T-10 |
| T-8 | Research issue: clarify B3 `MassQuote` schema availability and update ADR 0012 wording (OPT-08) | Research only; no code | — | All |
| T-9 | `InstrumentTradingRules` exposes `ContractMultiplier` + `ExpirationTimestamp` (OPT-09) | `InstrumentTradingRules`; folded into T-1 unless it grows. | T-1 | T-2..T-5, T-7, T-10 |
| T-10 | RUNBOOK MM tuning guidance (OPT-12) | `docs/RUNBOOK.md` only | — | All |

Trails **not** in the MVP:

- **T-6 (MassQuote)** — blocked indefinitely on schema publication.
  OPT-08 is the research tracker.
- **T-11 (`SettlementPrice_28` emission)** — listed as OPT-10 but
  deferred until a concrete consumer requests it; ADR 0001 (EOD
  drop) covers the bulk of the post-bell artifact need.

## 5. Issue roadmap

To be filed alongside this RFC as a single umbrella issue
(`epic: equity options support (MVP)`) linking the children below:

| Id | Title | Severity | Trail | Depends on |
| --- | --- | --- | --- | --- |
| OPT-01 | `feat(instruments): add option-specific fields to Instrument + InstrumentLoader` | high | T-1 | — |
| OPT-02 | `feat(umdf): emit option fields in SecurityDefinition_12` | high | T-2 | OPT-01 |
| OPT-03 | `feat(host): expiry-aware phase scheduling — auto-Close expiring option series` | medium | T-3 | OPT-01 |
| OPT-04 | `chore(config): add OPT channel + instruments-opt.json sample` | medium | T-4 | OPT-01 |
| OPT-05 | `fix(instruments): allow minPx ≤ tick for near-zero options` | medium | T-1 | OPT-01 |
| OPT-06 | `docs(compliance): rewrite options paragraph; add GAP-31/32` | medium | T-5 | — |
| OPT-07 | `feat(umdf): PriceBand_22 encoder + emission for EQT and OPT channels` | medium | T-7 | — |
| OPT-08 | `research: B3 MassQuote schema availability + ADR 0012 wording alignment` | low | T-8 | — |
| OPT-09 | `feat(matching): InstrumentTradingRules exposes ContractMultiplier + ExpirationTimestamp` | low | T-9 (folded into T-1) | OPT-01 |
| OPT-10 | `feat(umdf): SettlementPrice_28 for option series at end of last trading day` | low (deferred) | — | OPT-03 |
| OPT-12 | `docs(runbook): MM session tuning — throttle and maxOpenOrdersPerFirm` | low | T-10 | — |

(OPT-11 — "expose `SessionPolicy.MaxOrderRatePerSecond` in
`HostConfig`" — was removed from the roadmap during scoping:
`SessionPolicyConfig.MaxOrderRatePerSecond` already exists at
`src/B3.Exchange.Host/HostConfig.cs:215-237`. The capability is
already there.)

## 6. ADR roadmap

Decisions that warrant their own ADR (one ADR per decision, per
the RFC vs ADR convention in `docs/rfc/README.md`):

- **ADR-NN — Option series quantity semantics (contracts vs shares).**
  Codify §3.5: `LotSize=1` (contracts), `ContractMultiplier`
  metadata-only, no engine awareness.
- **ADR-NN — Channel topology for options.** Codify §3.1:
  options ride a dedicated channel; no cross-channel state;
  cross-channel stop-trigger is an explicit non-goal.
- **ADR-NN — Option series expiry lifecycle on the venue.**
  Codify §3.4: startup filter + end-of-Close-phase auto-Close;
  exercise / settlement explicitly out per ADR 0005 and ADR 0012.

These ADRs land as the corresponding trails (T-?, T-3, T-4)
implement them. The RFC may be re-edited to update this section
as ADRs are written.

## 7. Open questions resolved during scoping

| # | Question | Resolution |
| --- | --- | --- |
| Q1 | Scope of instrument — equity options only, or include index options (IBOV) and options on futures (DI / dólar)? | **Equity options only** for the MVP. The schema fields are shared, so a follow-up RFC adding INDEXOPT / FOPT mostly reuses the same infrastructure. |
| Q2 | MassQuote in phase 1, phase 2, or never? | **Never in the public-schema sense**, until B3 publishes a `MassQuote` template in the EntryPoint schema. MMs use the regular order-entry path with a calibrated per-session throttle (§3.6). Research issue OPT-08 tracks any future schema update. |
| Q3 | SecurityDefinition via continuous UMDF feed or static JSON? | **Both** — the static `instruments-opt.json` is the configuration source of truth at boot; the existing `InstrumentDefinitionPublisher` emits the live UMDF `SecurityDefinition_12` so consumers can build / refresh chains from the wire. No new design needed; just plumb the new fields through. |
| Q4 | Expired series at restart? | **Auto-Close at boot.** `InstrumentLoader` (or an explicit `ExpiryFilter` invoked from `ExchangeHost`) marks expired series permanently `Close` and emits a single terminal `SecurityStatus_3 CLOSE` at startup. No engine state for them. |
| Q5 | Quantity in shares or contracts? | **Contracts.** `LotSize=1`; `ContractMultiplier` is metadata. See §3.5 and the ADR proposed in §6. |
| Q6 | Emit `PriceBand_22` for options? | **Yes, for both EQT and OPT channels** — split out as OPT-07 (T-7). Not strictly a blocker for the options MVP, but a small enough adjacent gap to fold in. |

## 8. Risks and consequences

- **Schema coupling with `SbeB3UmdfConsumer`.** No schema is
  modified — only encoder output. Consumers compatible with UMDF
  v2.2.0 receive the new fields transparently. **Action:** no-op,
  but confirm in the encoder PR that the companion repo's parser
  reads the new fields.
- **Topology change visible to deployment.** Adding the OPT
  channel requires operators to extend
  `config/exchange-simulator.json` with a new multicast group and
  to subscribe consumers (e.g. `B3MarketDataPlatform`) to that
  group. **Action:** RUNBOOK update (OPT-12) covers this.
- **Series-naming convention is not enforced.** The simulator
  trusts the operator's `instruments-opt.json` for symbol naming.
  Operators wanting `PETRH320`-style symbols generate them
  externally. The venue does not parse `PETRH320 → PETR4 + 2026-08
  + call + 32.00` — that is consumer-side convenience tooling.
- **Cross-channel correlation gaps.** Stops referencing the
  underlying's last trade do not work (§3.1). Surfacing this
  limitation is part of OPT-04's sample config (a comment in
  `instruments-opt.json`) and the RUNBOOK.
- **Compliance doc drift fixed in flight.** The contradiction
  between ADR 0012 (in scope) and the compliance doc line 156
  (out of scope) is resolved by OPT-06 in the same PR window as
  this RFC.

## 9. Trigger to revisit

- A consumer (e.g. `B3MarketDataPlatform`, `B3OptionsAnalytics`)
  requires a venue-side behaviour currently marked out of scope
  here and not obtainable from another companion repo.
- B3 publishes a `MassQuote` template in a future EntryPoint
  schema release.
- A decision is made to extend support to index options or
  options on futures — likely a follow-up RFC, not an amendment to
  this one.
- A market-protection ADR (GAP-28) lands and reshapes how
  cross-instrument price triggers should be handled — at which
  point §3.1's cross-channel non-goal may be revisited.
