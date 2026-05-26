# 0013 — Channel topology for equity options

- **Status**: Accepted
- **Date**: 2026-06-04
- **Issue**: [#475](https://github.com/pedrosakuma/B3MatchingPlatform/issues/475)
  (T-4 / OPT-04 — channel + sample config)
- **Umbrella**: [#463](https://github.com/pedrosakuma/B3MatchingPlatform/issues/463)
  (RFC 0002 — equity options MVP)
- **Part of**: [RFC 0002 — equity options support](../rfc/0002-equity-options-support.md)
  §3.1, §6
- **Related ADRs**:
  [ADR 0009](0009-single-writer-threading-model.md) (single-writer
  per channel),
  [ADR 0012](0012-exchange-day-boundary.md) (exchange-day boundary
  governs scope).

## Context

Real B3 publishes equity options on a UMDF channel distinct from
cash equities and from derivatives. The simulator must reflect that
topology so consumers (e.g. `B3MarketDataPlatform`, the
`SbeB3UmdfConsumer` companion repo) can validate per-group ring
and cross-group fan-in against a shape that matches production.

Two designs were considered while drafting RFC 0002:

1. **Dedicated OPT channel.** Add a third `channels[]` entry in
   `exchange-simulator.json` with its own multicast group, its own
   `ChannelDispatcher`, and a dedicated `instruments-opt.json`.
2. **Co-tenant on the existing EQT channel.** Carry option series
   on the same dispatcher as their underlyings, partitioned only
   by SecurityID inside the same MatchingEngine.

Option 2 would simplify cross-channel correlation (notably stops
that reference the underlying's last trade) but would collapse two
distinct economic books onto a single single-writer dispatcher and
multicast group, deviating from the production topology and
breaking the per-channel isolation that ADR 0009 codifies.

## Decision

Options ride a **dedicated OPT channel** (option 1):

- One additional entry in `channels[]` (`channelNumber: 78` in the
  bundled `config/exchange-simulator.json`), with multicast group
  `224.0.20.78:30078`, snapshot `224.0.20.178:30178`, and
  instrument-definition `224.0.21.78:31078`. Endpoints are
  disjoint from the existing EQT (84) and DRV (72) channels.
- A dedicated `config/instruments-opt.json` carrying option-only
  instruments. SecurityIDs use the `902xxx` high-bit prefix so
  `HostRouter` can partition cleanly by SecurityID without overlap
  with EQT (`900xxx`) or DRV (`901xxx`).
- The OPT `ChannelDispatcher` is fully isolated per ADR 0009 — its
  own `MatchingEngine`, `OrderRegistry`, `_openOrdersByFirm`,
  `_lastTradePriceBySecurity`, `_stopsBySymbol`, inbound
  `Channel<WorkItem>`, dispatch thread, `IUmdfPacketSink`, WAL,
  snapshot persister, and `ChannelMetrics`. No mutable state is
  shared with EQT or DRV.

### Consequences accepted

- **Cross-channel stop triggers are an explicit non-goal.** Stops
  in the current engine fire on the just-executed trade price via
  the per-security `_stopsBySymbol` list inside `MatchingEngine`,
  keyed by `SecurityId` and channel-local by construction. A stop
  on `PETRL220` fires on the last trade of `PETRL220`, **not** on
  the last trade of the underlying `PETR4` (which lives on the EQT
  dispatcher and is therefore inaccessible from the OPT
  dispatcher). Implementing cross-channel triggers would require
  either a bridge component or merging the option and equity
  channels — both violate ADR 0009's single-writer guarantee.
  Folded into RFC 0002 §3.1 and the inline comment in
  `instruments-opt.json`. If a concrete consumer needs it, a
  follow-up RFC reopens the question.
- **Operators must extend their consumer subscriptions.** Adding
  the OPT channel requires `B3MarketDataPlatform` and any other
  UMDF subscriber to join the new multicast group. Tracked by
  OPT-12 (RUNBOOK update).
- **No engine awareness of `ContractMultiplier`.** Per RFC §3.5
  the OPT channel matches in contracts (`LotSize = 1`); the
  share-side economic size is reconstructed at the clearing /
  broker tier from `ContractMultiplier × matched contracts`. The
  multiplier is metadata-only on the venue and is not part of any
  matching invariant. Codified separately by the §3.5 ADR planned
  in RFC 0002 §6.

## Alternatives considered

- **Co-tenant on EQT (option 2 above).** Rejected because it
  conflicts with ADR 0009 (single-writer per channel collapses to
  cross-economic-book contention), deviates from production
  topology, and would require non-trivial routing logic to keep
  per-economic-book metrics and persistence separable.
- **One channel per option series expiry.** Considered for parity
  with B3's historical per-expiry channels, rejected as
  premature: the simulator's `channels[]` array supports it
  trivially if a consumer ever needs that granularity, but the
  MVP single OPT channel meets every consumer obligation
  identified during RFC 0002 scoping (RFC §7 Q1).

## Trigger to revisit

- A concrete consumer requires cross-channel stop triggers or
  another behaviour that depends on shared state between the OPT
  and EQT dispatchers.
- B3 publishes guidance changing the production topology (e.g.
  per-expiry channels, options-on-futures on yet another channel)
  in a way that the simulator should mirror end-to-end.
