# Persistence: property-based capture/restore tests

`tests/B3.Exchange.Persistence.Tests/CaptureRestorePropertyTests.cs`
uses [FsCheck.Xunit](https://fscheck.github.io/FsCheck/) to drive
`MatchingEngine.CaptureState` / `RestoreState` with randomly
generated command sequences and assert round-trip invariants
(issue #273).

## Properties

| Property | Invariant |
| --- | --- |
| `CaptureRestoreCapture_IsIdentity` | `capture → restore → capture` yields the same `EngineStateSnapshot` (every captured field equal — counters, phases, books in FIFO order, stops, halts). |
| `RestartAtAnyPoint_MatchesContinuousRun` | `apply(prefix) → capture → restore → apply(suffix)` matches `apply(prefix ++ suffix)` on a fresh engine, so a restart at any point is observationally indistinguishable from never having restarted. |
| `Regression_262_StopOrdersSurviveSnapshot` (Fact) | Pinned explicit assertion for the historical bug closed by #262 — untriggered stops must survive a snapshot round-trip. Kept as a `Fact` so it cannot hide behind a low `MaxTest` count. |

CI runs each property with `MaxTest = 100`. Tune locally with the
`[Property(MaxTest = N)]` attribute when shrinking a counter-example.

## Generator surface

The generator produces a `TestCmd` discriminated union with four
variants (`NewLimit`, `NewIceberg`, `CancelNth`, `ReplaceNth`). The
weighting keeps the book populated; pure-cancel / pure-replace
sequences would shrink to empty trivially and exercise nothing.

| Variant | Fields |
| --- | --- |
| `NewLimit(IsBuy, PriceTicks, LotMultiple, Ioc)` | `PriceTicks ∈ [100, 10000]` (= `1.00` … `100.00`), `LotMultiple ∈ [1, 10]`, single instrument (`PETR4`, `LotSize = 100`, `TickSize = 0.01`). Always `OrderType.Limit`, `Tif ∈ {Day, IOC}`. |
| `NewIceberg(IsBuy, PriceTicks, LotMultiple, VisibleLotMultiple)` | Same instrument; always `Tif = Day` (the engine rejects IOC + `MaxFloor` per [`Commands.cs`](../src/B3.Exchange.Matching/Commands.cs) `MaxFloor` doc). `VisibleLotMultiple ∈ [1, LotMultiple - 1]` so the visible slice is strictly smaller than the total quantity and the snapshot's `HiddenQuantity` field is exercised. |
| `CancelNth(Index)` | Cancel the `Index mod N`-th currently resting order across both sides (or no-op if the book is empty). |
| `ReplaceNth(Index, NewPriceTicks, NewLotMultiple)` | Same lookup as `CancelNth`; replaces with a freshly generated lot/price. |

Mixed at 7:1:1:1 (`NewLimit` : `NewIceberg` : `CancelNth` : `ReplaceNth`).
`NewIceberg` is rarer because the resting-iceberg state machine is a
narrower surface; the weighting still produces ~10% iceberg orders
across a 100-test run, enough to exercise the
`MaxFloor` / `HiddenQuantity` round-trip path through the snapshot.

`Apply` is the interpreter that materialises a real engine command
from each variant and resolves `CancelNth` / `ReplaceNth` against
the engine's *current* resting orders so most generated sequences
touch the book rather than rejecting on `UnknownOrderId`.

The generator surface is deliberately narrow so the properties have
a high signal rate. Engine rejects (validation failures, unknown
order ids) are valid no-ops — the round-trip invariants must hold
either way — but waste generator budget.

## Shrinking

`Arbs.Cmd()` registers an explicit per-element shrinker via
`Arb.From(gen, shrink)`. The default `Gen.ToArbitrary()` overload
attaches *no* shrinker (FsCheck's API contract: that overload
declares "shrink is not supported for this type"), so a custom
shrinker is required for property failures to be minimized.

The shrinker is intentionally simple: each `TestCmd` variant
walks its numeric fields one step toward their minimum legal value
(`LotMultiple → 1`, `PriceTicks → MinPriceTicks`, `Index → 0`),
flips `Ioc → false` (so the failure foregrounds resting-order
behaviour), and collapses `NewIceberg → NewLimit` so a counter-
example surfaces whether the iceberg state is required to reproduce.
Sequence-level shrinking (shortening the `TestCmd[]`) is provided
by FsCheck's default array shrinker on top of this.

## Extending the generators

To cover a new engine surface (e.g. iceberg, stop-limit, mass
cancel, cross orders, halts, trading-phase transitions):

1. Add a new variant to the `TestCmd` discriminated union (the
   nested `public abstract record TestCmd` near the top of the
   test file).
2. Write a `Gen<TestCmd>` factory for it in `Arbs` and wire it into
   the `Gen.Frequency` mix. Keep the new variant rare enough that
   it does not crowd out `NewLimit` — the book needs to be
   populated for the other variants to do anything.
3. Extend `Apply` with a case that materialises the new engine
   command. Resolve any order-id lookups via `ResolveResting` (or a
   new helper) so generators stay decoupled from monotonic id
   allocation.
4. If the new variant exercises a field that `EngineStateSnapshot`
   captures but `SnapshotsEqual` does not yet compare (e.g. a new
   field added to `RestingOrderRecord`), extend `OrderEqual` /
   `StopEqual` / `SnapshotsEqual` so the properties actually
   notice round-trip drift.
5. Add a `[Fact]` regression for any historical bug rediscovered
   by shrinking, following `Regression_262_StopOrdersSurviveSnapshot`
   as the template — pin the minimal reproducer so a future
   refactor cannot quietly regress the behaviour.

If you need a generator surface broader than a single instrument
(multiple `securityId`s, different lot sizes, currencies), parameterise
`Petr4` into a list of `Instrument`s, expand `NewLimit` with a
`SecurityId` selector, and adjust `ResolveResting` to iterate over
every book.

## Why this lives in `Persistence.Tests` rather than `Matching.Tests`

The properties exercise the **persistence boundary** — they fail
when capture or restore loses information, not when matching is
wrong (matching invariants are covered by `B3.Exchange.Matching.Tests`).
`MatchingEngine.CaptureState` / `RestoreState` is the
matching-engine half of the snapshot contract; the persister
(`FileChannelStatePersister`) is the other half and is covered by
the existing `*BinaryChannelStateSnapshotCodec*` and
`FileChannelStatePersister*Tests` suites. Property-based snapshot
codec round-trip would be a natural follow-up.
