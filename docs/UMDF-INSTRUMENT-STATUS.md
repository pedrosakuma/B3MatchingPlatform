# UMDF administrative instrument status

Schema `b3-market-data-messages-2.3.0.xml` (schema id 2, version 17) adds
`InstrumentStatus_58` as the authoritative administrative halt contract.
The 28-byte fixed block contains:

| Field | Tag | Offset | Meaning |
|---|---:|---:|---|
| `securityID` | 48 | 0 | Instrument identifier. |
| `matchEventIndicator` | 37035 | 8 | `RecoveryMsg` is set on snapshot publication. |
| `tradingSessionID` | 336 | 9 | Required enum; currently `REGULAR_TRADING_SESSION=1`. |
| `securityTradingStatus` | 326 | 10 | Underlying trading phase preserved across the overlay. |
| `administrativeHaltState` | 37781 | 11 | `ACTIVE=0`, `HALTED=1`. |
| `administrativeTransitionKind` | 37782 | 12 | Optional: `HALT=1`, `RESUME=2`; null in snapshots. |
| `haltReason` | 37783 | 13 | Optional: regulatory=1, volatility=2, news=3, pending disclosure=4. |
| `transactTime` | 60 | 16 | Live transition time; halted-at time in snapshots; zero for active snapshots. |
| `rptSeq` | 83 | 24 | Live per-instrument sequence; null/zero in snapshots. |

Live halt/resume packets retain the legacy `SecurityStatus_3` marker as the
first frame, then carry `InstrumentStatus_58` with the same `RptSeq`. This
keeps existing consumers operational while upgraded consumers use the new
typed fields. The incremental retransmission buffer retains both frames.

Every per-instrument snapshot sets `totNumStats=1` and includes one
recovery-marked `InstrumentStatus_58` after `SnapshotFullRefresh_Header_30`.
It carries current state and reason, with no transition kind, so late
subscribers bootstrap without replaying the original halt.

## B3MarketDataPlatform integration

For issue `B3MarketDataPlatform#73`:

1. Replace the UMDF schema with `b3-market-data-messages-2.3.0.xml` and build
   to regenerate `B3.Umdf.Mbo.Sbe.V17`.
2. Dispatch template id 58 for schema id 2.
3. Treat `administrativeTransitionKind` as the live transition and
   `administrativeHaltState` as the resulting/current state.
4. Map `haltReason` values 1..4 directly to the four operator reasons; require
   a reason when state is `HALTED`, and expect null when `ACTIVE`.
5. Apply recovery-marked snapshot updates as bootstrap state without emitting
   a synthetic live transition. Continue decoding legacy template 3 during
   rolling deployment.
