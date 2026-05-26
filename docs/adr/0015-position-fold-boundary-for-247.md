# 0015 — Position/cash fold boundary for 24/7 simulation environments

- **Status**: Accepted
- **Date**: 2026-05-26
- **Related ADRs**: [ADR 0004 — Settlement cycle out of scope](0004-settlement-cycle-out-of-scope.md),
  [ADR 0005 — Clearing boundary out of scope](0005-clearing-boundary-out-of-scope.md),
  [ADR 0007 — Position aggregate per firm (deferred)](0007-position-aggregate.md),
  [ADR 0012 — Exchange-day boundary](0012-exchange-day-boundary.md).
- **Related RFC**: [RFC 0002 — Equity options support](../rfc/0002-equity-options-support.md).
- **Related repos**: `B3TradingPlatform` (broker/participant-side consumer).

## Context

ADRs 0004 / 0005 / 0012 establish that this repo does **not** model
settlement, clearing, position keeping, or cash balances. The exchange
is matching-pure: it emits `ExecutionReport` to TCP sessions, `Trade`
frames on UMDF, and a `fills.csv` post-trade artifact (ADR 0001). What
happens to that fill downstream — does the buyer's broker block cash?
does the option get "delivered"? — is out of scope.

This works cleanly when the deployment model mirrors a real exchange
day: trade T, settle T+2 via clearing, custody loads at SOD on T+3.
But the family of repositories also targets **24/7 simulation
environments** (continuous operation, no formal opening or closing
bell, no overnight clearing window, no custody load at SOD). In that
shape:

- There is no SOD/EOD boundary to load starting positions or cash.
- There is no clearing process to settle trades overnight.
- The consumer (`B3TradingPlatform`) cannot wait for a SOD custody
  load that will never arrive.
- Users still need to "see" what they bought, and the broker still
  needs to refuse a buy when they're out of cash.

The recurring question becomes: **how does a downstream consumer
derive positions and cash from what this exchange exposes, without
us having to model clearing?**

## Decision

The exchange exposes three artifacts that, together, are sufficient
for a consumer to maintain positions and cash via continuous fold —
no clearing module required:

1. **`ExecutionReport`** stream on the FIXP TCP session
   (in-session, low-latency feedback to the order's owner).
2. **`Trade_201`** frames on UMDF multicast (out-of-band,
   broadcast to anyone subscribing to the channel).
3. **`fills.csv`** post-trade EOD/snapshot drop (durable, replayable,
   contains every executed fill — see ADR 0001 §3).

The recommended downstream model for 24/7 environments is
**continuous T+0 fold over the audit log**:

```
position(account, security) =
    Σ(fill.qty where side=Buy and account matches)
  − Σ(fill.qty where side=Sell and account matches)

cash(account) =
    bootstrap_cash(account)
  − Σ(buy.qty × buy.price × contract_multiplier + fees)
  + Σ(sell.qty × sell.price × contract_multiplier − fees)
```

Where `bootstrap_cash` is a consumer-side configuration ("each new
session starts with R$ 1M fictitious cash") — the exchange does not
allocate or track it.

This model has three properties that make it well-suited to 24/7
deployments:

- **No day boundary.** Position and cash are derived state at any
  instant `t` from the fills up to `t`. There is no SOD/EOD reset to
  coordinate.
- **Idempotent recovery.** The consumer can rebuild full state by
  replaying `fills.csv` from byte zero. No separate position snapshot
  is needed (which is also why ADR 0007 stays deferred).
- **Multiplier-aware.** Options carry a `contract_multiplier` (100
  for equity options on B3; see RFC 0002 §3.3 / `InstrumentTradingRules`).
  Cash settlement of an option fill is `qty × price × multiplier`, not
  just `qty × price`. The consumer reads this from the
  `SecurityDefinition_12` UMDF frame.

## Consequences

**For B3MatchingPlatform:**
- No new code or schema. We continue to expose exactly what we expose
  today (ExecutionReport + Trade + fills.csv + SecurityDefinition_12).
- The post-trade audit log (`B3.Exchange.PostTrade`) becomes the
  canonical replay source — protect its on-disk format (ADRs 0002,
  0008) accordingly.
- Option auto-exercise at expiry (via `OptionExpirySweeper`) emits a
  cancel for unexpired resting orders but **does not** emit a synthetic
  "exercise fill" for in-the-money holdings. That's a broker/clearing
  responsibility; the consumer detects ITM status from market data at
  expiry and folds the cash settlement into its own state.

**For B3TradingPlatform (downstream consumer):**
- Implements the fold described above. The state machine is small —
  essentially `Dictionary<(Account, SecurityId), long>` for positions
  and `Dictionary<Account, decimal>` for cash, folded from fills.
- Bootstraps each new account/session with configurable fictitious
  cash. No starting position (positions begin at zero).
- Pre-trade risk check ("can this account afford this buy?") runs
  against the folded state before the order is sent to the exchange.
- For options, fold uses `contract_multiplier` from
  `SecurityDefinition_12` when settling cash.

**For test/dev environments:**
- A 24/7 simulator running months at a time accumulates fills
  monotonically. Consumers should periodically checkpoint their
  folded state (their concern, not ours).
- Resetting the simulated world is a coordinated truncate of
  `fills.csv` + consumer state — the README runbook documents this
  as a developer action, not an exchange-day event.

**Why this stays out of `B3MatchingPlatform`:**
- It is consumer-specific (different consumers want different
  bootstrap rules, fee schedules, multi-account aggregation).
- It is account-level, not session-level — and the exchange does not
  model end-customer accounts (only firms + sessions).
- ADR 0012's rule applies: clearing/settlement happens after the bell
  (or in a 24/7 sim, *outside* the matching loop) → it does not live
  here.

## Alternatives considered

1. **Build a position/cash aggregate in this repo.** Rejected. ADR 0007
   already deferred this for the EOD aggregate file; the 24/7 case
   makes it worse (no natural day boundary to roll up against). Would
   pull broker-side concerns (bootstrap rules, fee schedules,
   account-level aggregation) into the exchange.

2. **Have the exchange "settle" trades into a built-in cash ledger.**
   Rejected. Duplicates ADR 0005 (no clearing module). Would require
   modeling end-customer accounts, which we explicitly don't.

3. **Defer the question entirely and let each consumer figure it out.**
   Rejected. The fold approach is small enough that it's worth
   recording the recommended shape so consumers don't independently
   invent diverging schemes. Boundary docs are cheap; lost
   interoperability is expensive.

4. **Build an online-clearing component as a sibling repo
   (`B3ClearingPlatform`).** Deferred (not rejected). Would mirror
   real-world T+2 settlement more faithfully but adds substantial
   complexity (margin call, default fund, settlement window) that is
   not justified by any current consumer. May be revisited if a
   consumer needs to simulate clearing-specific scenarios (default
   waterfall, margin breach, etc.). For now, the broker-side fold is
   the simpler model.
