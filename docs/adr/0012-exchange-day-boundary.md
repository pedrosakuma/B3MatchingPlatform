# 0012 — Exchange-day boundary: what the simulator does and what it doesn't

- **Status**: Accepted
- **Date**: 2026-05-25
- **Related ADRs**: [ADR 0004 — Settlement cycle out of scope](0004-settlement-cycle-out-of-scope.md),
  [ADR 0005 — Clearing boundary: no clearing module](0005-clearing-boundary-out-of-scope.md),
  [ADR 0010 — Post-trade orchestration boundary](0010-post-trade-orchestration-boundary.md).
- **Related docs**: [`README.md`](../../README.md) (family of repositories),
  [`docs/B3-ENTRYPOINT-COMPLIANCE.md`](../B3-ENTRYPOINT-COMPLIANCE.md).

## Context

ADRs 0004 and 0005 explicitly carved settlement and clearing out of this
repo, and ADR 0010 fenced post-trade orchestration. But the **positive**
side of the question — *what does this simulator model?* — was implicit,
spread across the README's family-of-repositories table, scattered
"non-goal" tags in the compliance doc, and tribal knowledge in PR
discussions.

The lack of an explicit scope line repeatedly produced the same
conversation: someone asks "should we add X?" (greeks; OCO; trailing
stop; auto-exercise of options at expiry; position keeping; ...) and the
answer has to be reconstructed every time from the negative ADRs plus
context about the companion repos. This ADR collapses that recurring
discussion into a single rule the team can point at.

## Decision

The simulator models everything **a real B3 venue does during a trading
session**, from the opening auction (`Reserved` / `Open`) through the
closing auction (`FinalClosingCall` / `Close`). Concretely, in scope:

- **Order matching** — Limit / Market / StopLoss / StopLimit /
  MarketWithLeftover order types; Day / IOC / FOK / GTC / GTD / AtClose
  (MOC) / GoodForAuction (MOA) time-in-force; iceberg (`MaxFloor`);
  `MinQty`; cross orders.
- **Session protocol** — FIXP Negotiate / Establish / Terminate;
  retransmission; Cancel-on-Disconnect; mass cancel; reattach + resync.
- **Market data publication** — UMDF MBO / Trade / SecurityStatus
  increments, snapshot recovery, auction top.
- **Auction mechanics** — accumulation, uncross, AuctionTopChanged,
  AuctionPrint, phase-driven TIF eligibility.
- **Order-handling protections that are exchange-side** — Self-Trading
  Prevention (STPC, GAP-27), market protections (price collars / fat
  finger / max value, GAP-28), inbound throttling.
- **Market-making protocols** — *future scope, not yet implemented.*
  `MassQuote` / an options-side `QuoteRequest` (distinct from the existing
  Termo/Forward `QuoteRequest`, template id=401) are necessary for any
  non-trivial options or futures venue simulation; they live inside this
  boundary even though no concrete consumer has driven implementation yet.
  Tracked as **OPT-08** in `docs/rfc/0002-equity-options-support.md`;
  blocked until B3 publishes a `MassQuote` template in a future EntryPoint
  schema release (the vendored v8.4.2 carries none — its `Quote*` templates
  are Termo/Forward only).
- **Operational surface needed to run the venue 24/7** — phase
  scheduling, daily reset, halt/resume, durable WAL+snapshot, EOD fills
  drop, per-trade audit log (ADRs 0001/0002).

Out of scope — these belong to **other repos in the family**, not to
this one:

| Concern | Belongs to |
| --- | --- |
| Economic modelling of instruments (pricing, greeks, implied vol, theoretical price) | A separate analytics repo (e.g. a hypothetical `B3OptionsAnalytics`) that consumes UMDF and publishes its own feed, symmetric to `B3MarketDataPlatform`. |
| Post-pregão lifecycle of derivatives (exercise, assignment, expiry settlement, physical delivery) | A separate clearing simulator (ADR 0005). The exchange just stops trading the expiring series at the right time and emits the final `SecurityStatus`. |
| Position keeping, margin, VaR, guarantee fund | Clearing simulator (ADR 0005). |
| Settlement cycle (D+1 / D+2, BVMF settlement files) | Out (ADR 0004). |
| Broker-side composite orders (OCO, brackets, trailing stops, time-sliced VWAP/TWAP execution algos) | `B3TradingPlatform` (the OMS-like repo). These never reach the exchange wire — they are composed by the broker and decomposed back before the participant sends EntryPoint frames. |
| End-client onboarding, KYC, custodial relationships | Outside the family entirely. |
| Surveillance *enforcement* | Out — ADR 0006 only emits a feed. Enforcement is a regulator/CCP concern. |

### The rule

A future "should we add X?" question is answered with a single test:

> Does a real B3 venue do X **between the opening bell and the closing
> bell**, inside the matching engine or the EntryPoint/UMDF gateway?
>
> - **Yes** → in scope; file an issue here.
> - **No** → out of scope; the work belongs in another repo
>   (`B3TradingPlatform` for broker-side, `B3MarketDataPlatform` for
>   subscriber-side, hypothetical clearing/analytics repos for the rest).
> - **It's exchange-side but only after the bell** → out (the post-trade
>   audit log and EOD drop are the only after-bell artifacts this repo
>   owns; further extension is gated on a future ADR).

## Consequences

- **`docs/B3-ENTRYPOINT-COMPLIANCE.md`** stays as the authoritative gap
  tracker for in-scope items. Rows whose work would cross this boundary
  are marked `out-of-scope (ADR 0012)` rather than `missing`.
- **Issue triage** can point at this ADR to close suggestions that
  belong elsewhere, instead of leaving them open as "low priority" debt
  that never gets done.
- **The companion repos in the README's family table become the
  positive expression of this boundary**: every "out" cell in the table
  above maps to either an existing companion repo or a hypothetical
  one. If a new "out" concern appears that doesn't map to any companion,
  that's a signal to either spin up a new companion repo or revisit
  this ADR.

## Trigger to revisit

- A concrete consumer needs venue behaviour that is currently marked
  out-of-scope here and **cannot be obtained from a companion repo
  consuming this repo's artifacts**.
- The B3 spec evolves to fold a previously-clearing or
  previously-broker concern into the exchange tier itself.

Reopening this ADR usually means either (a) carving a new companion
repo or (b) amending the boundary; it does not mean silently growing
this repo's responsibilities.
