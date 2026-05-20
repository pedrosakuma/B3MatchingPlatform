# 0011 — `B3.Exchange.Contracts` is a shared-primitives layer; Matching events are the canonical Core→Gateway model

- **Status**: Accepted
- **Date**: 2026-05-21
- **Issue**: [#379](https://github.com/pedrosakuma/B3MatchingPlatform/issues/379)
- **Supersedes**: the *"deferred to a follow-up PR"* commitment buried in
  `ICoreOutbound`'s XML doc (originally added with the #66 transport-removal
  PR).
- **Amends**: nothing — re-decides an obligation that was previously left
  open. The original ADR #66 only required that Core not hold transport
  references; it did not mandate a neutral event family.

## Context

Issue #379 reopened a question that had been hanging since #66 landed:
should the `B3.Exchange.Contracts` assembly carry a **neutral** event /
command family (`ExecutionEvent`, `InboundOrderCommand`, …, plus a
FIX-numbered parallel `Side`/`OrderType`/`TimeInForce` enum family) that
would let `Contracts` drop its `ProjectReference` to
`B3.Exchange.Matching`?

After 18 months on `main`, the situation was:

- The neutral types existed but **had zero consumers** outside the
  assembly they were declared in. `ICoreInbound`, `IGatewayInbound`,
  `InboundOrderCommand`, `ExecutionEvent`, `BusinessRejectEvent` were
  pure dead weight.
- The parallel enum family in `Enums.cs` (`Side`, `OrderType`,
  `TimeInForce`, `ExecType`) was also dead — only `EnumMappingTests`
  referenced them, and `EnumMappingTests` existed *solely* to guard
  against drift between the two parallel enums. A test pinning unused
  code in lockstep with used code is negative value.
- The interface that **is** used at the Core→Gateway boundary —
  `ICoreOutbound` — takes Matching engine event types
  (`OrderAcceptedEvent`, `TradeEvent`, `OrderCancelledEvent`,
  `OrderRejectedEvent`) as parameters and would require non-trivial
  field expansion in the neutral records (Side, SecurityId, RptSeq,
  InsertTimestampNanos, …) to switch.
- The "deferred to a follow-up PR" admission was 18 months stale; no
  PR had been opened, no acceptance criteria had been written, and no
  downstream consumer had ever asked for the neutral payload.

The dual architectural review (Opus 4.7 + GPT-5.5) flagged this as the
single largest source of "dead seam" weight in the codebase.

## Decision

1. **Matching's engine event records are the canonical Core→Gateway
   boundary model.** `ICoreOutbound` (in `B3.Exchange.Contracts`)
   continues to take `B3.Exchange.Matching.OrderAcceptedEvent`,
   `TradeEvent`, `OrderCancelledEvent`, `OrderRejectedEvent` etc. as
   parameters. There is no second event family.
2. **`B3.Exchange.Contracts` is a "shared primitives + outbound port
   surface" layer**, not a neutral seam. It hosts:
   - Value types shared by Core + Gateway + Persistence (`SessionId`,
     `FirmId`, `Durability` records, `Metrics` records, `Time/` helpers,
     `ExchangeTelemetry`).
   - The outbound port surface that defines the Core→Gateway contract
     (`ICoreOutbound`, plus the small alias `MatchingSide` /
     `MatchingRejectEvent` to keep WAL-replay call sites readable).
3. **The upward `ProjectReference` from `B3.Exchange.Contracts` →
   `B3.Exchange.Matching` is retained and documented.** It exists
   because `ICoreOutbound` legitimately carries Matching types as
   payload; this is *not* an architectural inversion, because Matching
   is the canonical domain model and Contracts publishes a port over
   it.
4. **The dead neutral surface is deleted in this PR:**
   - `ICoreInbound.cs`, `IGatewayInbound.cs`
   - `Commands/InboundOrderCommand.cs`, `Commands/InboundCancelCommand.cs`,
     `Commands/InboundModifyCommand.cs`
   - `Events/ExecutionEvent.cs`, `Events/BusinessRejectEvent.cs`,
     `Events/RejectEvent.cs`
   - `Enums.cs` (FIX-numbered parallel enum family)
   - `tests/B3.Exchange.Core.Tests/EnumMappingTests.cs`

## Consequences

- **No more "deferred neutral migration" obligation.** The XML doc on
  `ICoreOutbound` that referenced an `ExecutionEvent` family is
  rewritten to cite this ADR.
- **No second event family to keep in sync.** Adding a field to an
  engine event (e.g. a new reason code) no longer requires a parallel
  edit in Contracts.
- **`EnumMappingTests` is gone.** Its sole purpose was to guard a
  family that is no longer there. The FIX-numbered enums it pinned
  lived only to feed wire encoders that never adopted them; the actual
  wire encoders (`EntryPointFrameReader`, ExecutionReport encoder,
  UMDF encoders) already use either raw `byte` constants or the
  Matching enums directly.
- **Future direction if a neutral seam *does* become needed.** A
  concrete downstream consumer (e.g. an out-of-process risk service
  speaking gRPC) would re-open this question. At that point a new ADR
  would (a) define the neutral types alongside the field set the
  consumer actually needs and (b) decide whether to translate at the
  Core→Gateway boundary or at a new Core→Risk boundary. This ADR does
  not pre-judge that decision.

## Alternatives considered

- **(b) Finish the neutral-seam migration** — the alternative listed in
  issue #379. Rejected because: (1) no consumer is waiting on it; (2)
  the field expansion needed in the neutral records would constitute a
  redesign, not a refactor; (3) it adds another layer of mapping on the
  Core→Gateway hot path with no demonstrated isolation benefit.
- **Move `ICoreOutbound` out of `Contracts` into `Core`.** Tempting —
  it would let Contracts drop the Matching `ProjectReference` entirely.
  Rejected because `ICoreOutbound` is *implemented* in Gateway and
  *called* from Core; placing it in Core would create a Gateway→Core
  ProjectReference (the inversion we already eliminated in #66). Its
  current home — a peer assembly that both Core and Gateway reference —
  is the correct topology.
