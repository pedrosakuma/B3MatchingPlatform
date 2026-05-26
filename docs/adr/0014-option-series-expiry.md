# 0014 — Option series expiry: two-path scheduling at the day boundary

- **Status**: Accepted
- **Date**: 2026-05-25
- **Related ADRs**: [ADR 0005 — Clearing boundary out of scope](0005-clearing-boundary-out-of-scope.md),
  [ADR 0009 — Single-writer per channel threading model](0009-single-writer-threading-model.md),
  [ADR 0012 — Exchange-day boundary](0012-exchange-day-boundary.md).
- **Related**: [RFC 0002 — Equity options support](../rfc/0002-equity-options-support.md)
  (§3.4 "Expiry handling", §7 Q4); umbrella issue #463; child issue #477
  (OPT-03).

## Context

RFC 0002 introduced first-class support for equity options. Trail
T-1 of that RFC taught `InstrumentLoader` how to validate and persist
option-only fields (strike, expirationDate, putOrCall, etc.). T-1 also
rejects any option whose `expirationDate` has already passed at boot
time — but RFC 0002 §3.4 also requires the simulator to handle the
intra-day case: an option series whose final trading day is *today*
should not stay open across the closing bell, and no new orders should
land on it after the trading day ends.

ADR 0012 is the relevant scope guardrail: the simulator owns the wall
between the opening and closing bells. **Exercise, assignment,
settlement, clearing, and any "after the bell" workflow on an expired
series remain explicitly out of scope** (the family-of-repositories
table in `README.md` assigns those to hypothetical clearing /
analytics repos). The question this ADR settles is therefore narrow:
"when an option series reaches its `expirationDate`, what should the
matching engine + UMDF gateway do *between bells* and at the
end-of-trading-day rollover?"

A second open question, recorded as RFC 0002 §7 Q4, was whether the
end-of-day expiry sweep should be at the boot-time loader, at the
runtime daily-reset boundary, or at both. We landed on both: each
covers a failure mode the other can't.

## Decision

Option-series expiry is handled by **two complementary paths** that
together cover the lifecycle:

1. **Boot-time rejection (loader path, T-1, already shipped)** —
   `InstrumentLoader.LoadFromFile` refuses to admit any option whose
   `expirationDate` is strictly before today (UTC). The host fails
   `StartAsync` with an `InstrumentConfigException` carrying the
   message *"expirationDate must be today or later"*. This catches
   operator drift in the instruments JSON and forces stale series out
   of the dictionary before any TCP session can place an order.

2. **End-of-trading-day auto-Close sweep (runtime path, T-3,
   this ADR)** — an `OptionExpirySweeper` host component runs at the
   start of `ExchangeHost.TriggerDailyReset` (the codebase's
   end-of-trading-day boundary). For every loaded option series whose
   `ExpirationDate ≤ today` (UTC), the sweeper enqueues a new
   `OperatorExpireSecurity` work item on the channel that owns the
   series. The dispatcher processes that work item under its
   single-writer invariant (ADR 0009):

   1. Snapshot every resting `orderId` for the security from
      `OrderRegistry`.
   2. Call `MatchingEngine.MassCancel(...)` — each cancelled order
      surfaces as a per-session `ER_Cancel` on the EntryPoint TCP path
      and as a `DeleteOrder_MBO_51` frame in the UMDF buffer.
   3. Call `MatchingEngine.SetTradingPhase(securityId, Close, now)` —
      a terminal `SecurityStatus_3` frame with `SecurityTradingStatus
      = 3 (CLOSE)` is buffered alongside the cancellations.
   4. Flush the buffered frames as one UMDF packet so consumers see
      "everything died → CLOSE" in a single sequence-number step.

   The sweeper synchronously waits on `TaskCompletionSource`
   completions before returning so that `TriggerDailyReset` does not
   call `TerminateAllSessions` until every `ER_Cancel` has been
   handed to the outbound encoder. Once `Close` is set the engine
   rejects further admissions through the existing trading-phase
   gate; the series is then re-rejected by `InstrumentLoader` on the
   next boot.

### Calendar-day interpretation

`ExpirationDate` is interpreted as a **UTC calendar day**. The
boot-time check (`InstrumentLoader`) and the runtime sweep both use
`DateOnly.FromDateTime(DateTime.UtcNow)`. Local-time / exchange-time
conversion is intentionally out of scope here — the rest of the
codebase already operates in UTC for instrument metadata and
audit-log day boundaries, and adding a per-exchange timezone here
would cross the line into clearing/settlement workflow (out of scope
per ADR 0005). The intentional consequence is that operators who
configure series with broker-local expiration dates must convert
them to UTC.

### Threading

The sweep itself runs on the caller's thread (the operator-trigger
thread of `TriggerDailyReset` or the daily-reset timer). It never
touches any `MatchingEngine` directly — every transition happens on
the dispatcher's owner thread via the work-item queue, preserving
ADR 0009's single-writer-per-channel invariant. The bounded inbound
channel is the natural backpressure mechanism: a saturated channel
will reject the enqueue and surface a warn log, which is the same
fail-mode as every other operator command.

## Scope boundary

In line with ADR 0012, the runtime path **stops at the closing
trading phase**. The simulator deliberately does **not**:

- compute settlement prices, mark-to-market PnL, or assignment
  allocations for ITM expiring options;
- generate exercise notices, broker-side margin calls, or any
  CCP-bound workflow;
- model the post-bell auction / closing-print mechanics specific to
  options;
- remove the instrument from the loaded dictionary or strip it from
  subsequent UMDF `SecurityDefinition_12` frames — the next-day
  boot rejection is responsible for that pruning.

Anything in that list belongs in a (hypothetical) clearing /
analytics companion repo, not here. If a contributor wants
auto-exercise or settlement, the answer per ADR 0012 is to file the
work against that repo, not to extend this one.

### Known gap (acceptable for MVP): parked stops

`MatchingEngine.MassCancel` walks `_booksById` only and does not
touch `_stopsBySymbol`. An expiring option series that has parked
(non-triggered) stop orders will leave those stops in memory until
the next-day boot rejection prunes the instrument. This parallels
the existing operator `OrderMassActionRequest` behaviour and was
deemed acceptable for the MVP — options stop-order workflows are
themselves out of the equity-options RFC's first pass. If we later
extend stops to options as first-class, this ADR will be amended
to require `OperatorExpireSecurity` to walk both books.

## Consequences

- The two paths compose. A series expiring during a multi-day
  scheduled outage gets caught by the boot rejection on
  the next start, even if the auto-Close sweep never ran. A series
  expiring during normal operation gets the in-process auto-Close
  with the burst-of-cancels + `Close` framing consumers expect.
- The auto-Close runs inside the existing single-writer
  per-channel invariant. No new locks, no `async` in the dispatch
  loop, no cross-thread state.
- Operators who want to keep an option series alive past its
  `expirationDate` for testing purposes must bump the date in the
  instruments JSON — there is no override knob, by design.
- The sweep's wait-for-completion is bounded by a 15-second timeout.
  A wedged dispatcher (already a fatal-failure scenario in this
  codebase) logs a warning rather than holding the daily-reset
  thread forever.

## Alternatives considered

- **Loader-only path (RFC §7 Q4 option A)**: rejected. Operators
  who never restart the host would never see an expiring series
  close — the engine would keep accepting orders on a stale series
  for arbitrarily long, contradicting RFC 0002 §3.4.
- **Runtime-only path (RFC §7 Q4 option B)**: rejected. A host that
  starts up the morning after a series expired would silently load
  an expired security and rely on the next daily-reset window to
  close it. Failing fast at boot is cheaper and louder.
- **Hooking into `PhaseScheduler` end-of-day transition**: rejected.
  `PhaseScheduler` is a per-channel cron-style scheduler with no
  notion of "end of trading day for the whole host"; it would have
  required either a new scheduler-level callback or duplicating the
  HH:mm config per group. `TriggerDailyReset` already exists, is
  already the codebase's "end of trading day" boundary, and is
  already wired to the operator HTTP endpoint + the daily-reset
  timer.
- **Cancelling all orders on the series without a phase transition**:
  rejected. Without the terminal `Close` transition, the engine
  would still accept new orders on the series between the sweep
  and the next boot, defeating the purpose.
