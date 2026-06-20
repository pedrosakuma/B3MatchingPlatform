# B3 Binary EntryPoint — Compliance Audit

Audit of how `src/B3.Exchange.Gateway` and friends compare to the official
B3 spec. This is the canonical "what's missing / what's wrong vs. the wire
protocol" reference for the simulator — issues that track individual gaps
should link back to a row in the table below by `#gap-NN`.

## Sources audited

| Document | Version | Notes |
| --- | --- | --- |
| [BinaryEntryPoint Messaging Guidelines][guidelines] | 8.4.2.1 (2026-03-20) | Behavioural reference (handshake, COD, throttle, validations). |
| [`schemas/b3-entrypoint-messages-8.4.2.xml`][schema] | 8.4.2 | The SBE schema vendored in this repo. |

[guidelines]: https://www.b3.com.br/data/files/4E/C3/97/F9/2DA1D910A3ABA1D9AC094EA8/BinaryEntryPoint-MessageSpecificationGuidelines-8.4.2.1-enUS.pdf
[schema]: ../schemas/b3-entrypoint-messages-8.4.2.xml

Auditor: initial pass at commit `f36028a` (2026-04-30); re-audit at commit
`edff9c8` (2026-05-24) after PR #405 (FIXP session resync persistence)
landed — the resulting partial-status narrowings (issues #407..#416) have
since all been closed and the affected rows flipped to `done`.
Refresh this section when the table is re-validated against a newer spec
revision.

## How to read this document

- **Severity** — `critical` = real B3 client cannot interop today;
  `high` = wire is OK but mandated behaviour missing;
  `medium` = compliance gap with workaround;
  `low` = realism polish, often already framed as a non-goal.
- **Status** — `missing` (not implemented), `partial` (implemented with caveats),
  `deviation` (implemented differently from spec on purpose), `bug` (implemented
  wrong).
- **Issue** — GitHub issue tracking the fix. Empty if not yet filed.

## Decisions and conscious deviations

These are **intentional** simplifications. They are listed here to keep them
out of the gap table and to make the rationale auditable.

- **Continuous trading only.** No auction phases / TradingSessionStatus
  cycle. (See [#19](https://github.com/pedrosakuma/B3MatchingPlatform/issues/19).)
- **FIXP session resync persistence** ([#405](https://github.com/pedrosakuma/B3MatchingPlatform/issues/405)).
  Outbound business frames are appended to an unbounded per-session
  journal and the FIXP envelope state (SessionVerId, LastIncomingSeqNo,
  outbound MsgSeqNum, CoD parameters) is snapshotted on every
  handshake / suspend / non-terminal close. On boot the host
  rehydrates `SessionClaimRegistry` from the snapshots so a peer
  reconnecting with its original SessionVerId is accepted (no
  `UNNEGOTIATED` reject); subsequent `RetransmitRequest`s for seqs
  beyond the in-memory ring are served from the journal. Channel
  books are still rebuilt from snapshot + WAL (see `B3.Exchange.Persistence`).
- **Routing by `SecurityID`** instead of by `marketSegmentID` from the inbound
  business header. Practical equivalent because every instrument in the
  simulator maps 1:1 to a UMDF channel; spec allows both as long as the
  matching engine receives the message.
- **Single `EnteringFirm` per host** (today) — multi-firm is tracked by #8.
- **No order types beyond Limit / Market**, no GTC/GTD/MOC/MOA, no iceberg,
  no stop. These are explicitly out of scope in the current roadmap (#19),
  but each remains catalogued in the gap table for future re-scoping.

## Canonical session flows (for reference)

The five flows from spec §5. `[OK]` means the simulator reproduces the flow
end-to-end today; `[GAP-NN]` points to the entry in the table that breaks it.

### §5.1 Normal connectivity

```
    Client                                Gateway
      |  ── TCP handshake ────────────►    |
      |  ── Negotiate ───────────────►     |   [GAP-04]
      |  ◄── NegotiateResponse ─────       |   [GAP-04]
      |  ── Establish ───────────────►     |   [GAP-05]
      |  ◄── EstablishAck ──────────       |   [GAP-05]
      |  ── App messages ───────────►      |   (partial — see §4.6 gaps)
      |  ◄── App messages ──────────       |
      |  ── Terminate ──────────────►      |   [GAP-06]
      |  ◄── Terminate ─────────────        |   [GAP-06]
      |  ── TCP FIN ────────────────►       |
```

### §5.2 Invalid credentials in negotiation

Requires `Negotiate` + `NegotiateReject(CREDENTIALS)` + `Terminate`. All three
missing — see [GAP-04].

### §5.3 Loss of connectivity during the session

Requires the client to re-`Establish` with the previously negotiated
`sessionVerID`. Not supported today ([GAP-05], [GAP-07]).

### §5.4 Client-side total failure

Requires `NegotiateReject(ALREADY_NEGOTIATED, currentSessionVerID)` so the
client can recover its `sessionVerID`. Missing ([GAP-04]).

### §5.5 Gateway failures

Requires `Sequence` + retransmission + `BACKUP_TAKEOVER_IN_PROGRESS`
termination code. Missing ([GAP-06], [GAP-08]).

## Gap table

### Wire format (CRITICAL — no real B3 client can interoperate today)

| # | Spec § | Item | Status | Gap | Severity | Issue |
| --- | --- | --- | --- | --- | --- | --- |
| <a id="gap-01"></a>GAP-01 | 4.4 | **Simple Open Framing Header (SOFH)** — 4 bytes (`messageLength` LE + `encodingType=0xEB50`) prepended to every SBE frame; total wire header is **12 bytes**, not 8. | done | Implemented in `EntryPointFrameReader`; `messageLength ≤ 512` enforced. | critical | [#39](https://github.com/pedrosakuma/B3MatchingPlatform/issues/39) (closed) |
| <a id="gap-02"></a>GAP-02 | 3.5, 4.6.5 | **Variable-length data trailing the fixed block** (`memo`, `credentials`, `clientIP`, `clientAppName`, `clientAppVersion`). Each is `length(uint8)` + `varData`. Total frame size is `SOFH.messageLength`, not `8 + BlockLength`. | done | Inbound varData parsing done; the outbound ExecutionReport (New/Modify/Cancel/Reject) and BusinessMessageReject encoders now emit the `memo`/`text` varData trailer via `WriteMemoTrailer`. | high | [#40](https://github.com/pedrosakuma/B3MatchingPlatform/issues/40) (closed), [#411](https://github.com/pedrosakuma/B3MatchingPlatform/issues/411) (closed by [#427](https://github.com/pedrosakuma/B3MatchingPlatform/pull/427)) |
| <a id="gap-03"></a>GAP-03 | 4.5.7, 4.10 | **`Terminate` with `terminationCode`** on framing/decoding errors (`16=INVALID_SOFH`, `17=DECODING_ERROR`, `15=UNRECOGNIZED_MESSAGE`, `11=INVALID_SESSIONID`, `13=INVALID_TIMESTAMP`, …). Also enforce SOFH `messageLength ≤ 512` per spec. | done | Decode errors emit Terminate with correct codes; server Terminate frames now encode the active `SessionVerId` (every `EncodeTerminate` call site passes `SessionVerId`, not 0). | critical | [#41](https://github.com/pedrosakuma/B3MatchingPlatform/issues/41) (closed), [#407](https://github.com/pedrosakuma/B3MatchingPlatform/issues/407) (closed by [#418](https://github.com/pedrosakuma/B3MatchingPlatform/pull/418)) |

### FIXP session lifecycle

| # | Spec § | Item | Status | Gap | Severity | Issue |
| --- | --- | --- | --- | --- | --- | --- |
| <a id="gap-04"></a>GAP-04 | 4.5.2 | **`Negotiate` / `NegotiateResponse` / `NegotiateReject`** — JSON `credentials` blob (`auth_type`, `username`, `access_key`), `sessionVerID`, daily reset, and the seven reject codes (incl. `ALREADY_NEGOTIATED` carrying `currentSessionVerID`). | done | Full handshake + monotonicity persisted (#405). Unknown sessionID now rejects as `INVALID_SESSIONID` (`NegotiationValidator`). | high | [#42](https://github.com/pedrosakuma/B3MatchingPlatform/issues/42) (closed), [#413](https://github.com/pedrosakuma/B3MatchingPlatform/issues/413) (closed by [#421](https://github.com/pedrosakuma/B3MatchingPlatform/pull/421)) |
| <a id="gap-05"></a>GAP-05 | 4.5.3 | **`Establish` / `EstablishAck` / `EstablishReject`** with `keepAliveInterval`, `nextSeqNo`, `cancelOnDisconnect{Type,TimeoutWindow}` and the eight reject codes. | done | Handshake/validation/EstablishmentAck.RECOVERABLE implemented. `keepAliveInterval` bounds in `EstablishValidator` are now 1000–60000ms per schema. | high | [#43](https://github.com/pedrosakuma/B3MatchingPlatform/issues/43) (closed), [#408](https://github.com/pedrosakuma/B3MatchingPlatform/issues/408) (closed by [#419](https://github.com/pedrosakuma/B3MatchingPlatform/pull/419)) |
| <a id="gap-06"></a>GAP-06 | 4.5.4, 4.5.7 | **`Sequence` (heartbeat + reset)** + **`Terminate`** message (with codes from `TerminationCode` enum — see schema lines 384–404). Spec recommends terminating after 3× `keepAliveInterval` of silence. | done | Heartbeat / Sequence / TestRequest implemented. Idle teardown emits `Terminate(KEEPALIVE_INTERVAL_LAPSED=10)`; the probe (Sequence/TestRequest) fires at ~1.5×`keepAliveInterval` and the terminate threshold is ~3×`keepAliveInterval` of silence per spec §4.5.4 (falls back to `IdleTimeoutMs + TestRequestGraceMs` when no keepAlive was negotiated). | high | [#44](https://github.com/pedrosakuma/B3MatchingPlatform/issues/44) (closed), [#409](https://github.com/pedrosakuma/B3MatchingPlatform/issues/409) (closed by [#420](https://github.com/pedrosakuma/B3MatchingPlatform/pull/420)) |
| <a id="gap-07"></a>GAP-07 | 4.5.5, 4.6.2 | **Inbound `MsgSeqNum` tracking + `NotApplied`** — gap detection emits `NotApplied(fromSeqNo, count)` and updates expected next seq. Outbound seq is per-`SessionID/SessionVerID`, reset daily. | done | Inbound seq tracking + `NotApplied` implemented; outbound seq per-session persisted (#405). Daily reset is its own gap (GAP-09). | high | [#45](https://github.com/pedrosakuma/B3MatchingPlatform/issues/45) (closed) |
| <a id="gap-08"></a>GAP-08 | 4.5.6 | **`RetransmitRequest` / `Retransmission`** with `RetransmitRejectCode` enum (schema 406–416). Max 1000 messages per request, single in-flight. | done | Implemented (`FixpRetransmitController`); journal-backed cold-read after #405; 1000-cap, single-in-flight, all RetransmitRejectCodes covered. | high | [#46](https://github.com/pedrosakuma/B3MatchingPlatform/issues/46) (closed) |
| <a id="gap-09"></a>GAP-09 | 4.5.1 | **Daily reset** — outbound + inbound seq reset to 1 at the start of each trading day. | done | `DailyResetScheduler` runs at boundary; `TerminateAllSessions` is invoked with `CloseKind.DailyReset`, which removes persisted seq state and releases the session claim with `forgetLastVersion:true` so the next reconnect starts fresh at seq=1. | medium | [#47](https://github.com/pedrosakuma/B3MatchingPlatform/issues/47) (closed), [#416](https://github.com/pedrosakuma/B3MatchingPlatform/issues/416) (closed by [#424](https://github.com/pedrosakuma/B3MatchingPlatform/pull/424)) |

### Application-level header

| # | Spec § | Item | Status | Gap | Severity | Issue |
| --- | --- | --- | --- | --- | --- | --- |
| <a id="gap-10"></a>GAP-10 | 4.6.3.1 | **Inbound `sessionID` validation** — spec §4.10 mandates `BusinessMessageReject(33003, "Wrong sessionID in businessHeader")` on mismatch. | done | `sessionID`, `msgSeqNum` and `sendingTime` (offset 8) are now read and validated in `HeaderValidation`; mismatches emit BMR. | high | [#48](https://github.com/pedrosakuma/B3MatchingPlatform/issues/48) (closed), [#414](https://github.com/pedrosakuma/B3MatchingPlatform/issues/414) (closed by [#423](https://github.com/pedrosakuma/B3MatchingPlatform/pull/423)) |
| <a id="gap-11"></a>GAP-11 | 4.6.4 | **`receivedTime` (tag 35544)** optional field added in schema v3 to ER\_New / ER\_Modify / ER\_Cancel. Carries the gateway-reception timestamp. | done | `receivedTime` emitted on ER\_New/Modify/Cancel; the `memo` varData trailer is now echoed on ExecutionReport/BMR via `WriteMemoTrailer` (see GAP-02). | medium | [#49](https://github.com/pedrosakuma/B3MatchingPlatform/issues/49) (closed), [#411](https://github.com/pedrosakuma/B3MatchingPlatform/issues/411) (closed by [#427](https://github.com/pedrosakuma/B3MatchingPlatform/pull/427)) |
| <a id="gap-12"></a>GAP-12 | 4.6.3.1 | **`marketSegmentID` for routing** — spec says the gateway uses this header field to route to the matching engine. | deviation | Simulator routes by `SecurityID` (1:1 with channel in our config). Working, but a real B3 client that relies on cross-segment instrument codes will be surprised. | low | documented in "Conscious deviations" — keep here for traceability. |
| <a id="gap-13"></a>GAP-13 | 4.6.3.2 | **`OutboundBusinessHeader.eventIndicator`** — bit flags (`PossResend`, `LowPriority`). | done | `PossResend` is now set on journal-replayed business frames by `FixpOutboundEncoder`; `LowPriority` not used by the simulator. | low | implicit via GAP-08 closure. |

### Application-level messages

| # | Spec § | Item | Status | Gap | Severity | Issue |
| --- | --- | --- | --- | --- | --- | --- |
| <a id="gap-14"></a>GAP-14 | 4.6.1 | **`BusinessMessageReject` (template 206)** for application-level rejects (bad sessionID, throttle violations, varData too long, line breaks in deskID/senderLocation/enteringTrader/executingTrader). | done | BMR encoder + dispatch routing implemented. (Sub-issue: BMR's own varData trailer is not emitted — tracked under GAP-02/#411.) | high | [#50](https://github.com/pedrosakuma/B3MatchingPlatform/issues/50) (closed) |
| <a id="gap-15"></a>GAP-15 | 4.6.1 | **`NewOrderSingle` (102)** and **`OrderCancelReplaceRequest` (104)** — the full templates with iceberg/stop/strategy fields. Spec lists these as the canonical messages; `Simple*` variants are explicitly the low-feature subset. | done | Templates 102/104 decode + accept Limit/Market plus stop, iceberg, GTC and GTD end-to-end. Genuinely unsupported characteristics (RLP `'W'`, Pegged `'P'`) now surface as `ExecutionReport_Reject(OrdRejReason=11=UnsupportedOrderCharacteristic)` per spec rather than `BusinessMessageReject`. | medium | [#51](https://github.com/pedrosakuma/B3MatchingPlatform/issues/51) (closed), [#415](https://github.com/pedrosakuma/B3MatchingPlatform/issues/415) (closed by [#425](https://github.com/pedrosakuma/B3MatchingPlatform/pull/425)) |
| <a id="gap-16"></a>GAP-16 | 4.6.1 | **`NewOrderCross` (106)**. | done | Cross-order decode + matching implemented. | low | [#52](https://github.com/pedrosakuma/B3MatchingPlatform/issues/52) (closed) |
| <a id="gap-17"></a>GAP-17 | 8.2 | **`OrdRejReason` mapping** — codes `1=UnknownSymbol`, `3=OrderExceedsLimit`, `5=UnknownOrder`, `6=DuplicateOrder`, `11=UnsupportedOrderCharacteristic`, etc. | done | Engine→wire mapping table implemented in `FixpOutboundEncoder`; the `UnsupportedOrderCharacteristic (11)` path is wired (`RejectReason.UnsupportedOrderCharacteristic`→`11u`) and emitted for genuinely unsupported characteristics (see GAP-15). | medium | [#53](https://github.com/pedrosakuma/B3MatchingPlatform/issues/53) (closed), [#415](https://github.com/pedrosakuma/B3MatchingPlatform/issues/415) (closed by [#425](https://github.com/pedrosakuma/B3MatchingPlatform/pull/425)) |

### Operational / risk features

| # | Spec § | Item | Status | Gap | Severity | Issue |
| --- | --- | --- | --- | --- | --- | --- |
| <a id="gap-18"></a>GAP-18 | 4.7 | **Cancel-on-Disconnect (CoD)** — `CancelOnDisconnectType` (4 modes) + `CODTimeoutWindow` in `Establish`. Cancels non-GT working orders on triggering events; spec §4.7.3 enumerates gateway-forced disconnects that also fire CoD if enabled. | done | On-disconnect half (modes 1+3) and on-Terminate half (modes 2+3) implemented — peer-`Terminate` now triggers CoD via `FixpSession.CancelOnDisconnect`, with grace-window timer and per-channel filtering. Non-GT filtering relies on the TIF model (GAP-23). | high | [#54](https://github.com/pedrosakuma/B3MatchingPlatform/issues/54) (closed), [#410](https://github.com/pedrosakuma/B3MatchingPlatform/issues/410) (closed by [#422](https://github.com/pedrosakuma/B3MatchingPlatform/pull/422)) |
| <a id="gap-19"></a>GAP-19 | 4.8 | **Mass Cancel (`OrderMassActionRequest` 701 / `OrderMassActionReport` 702)** with filters Side / SecurityID / OrdTagID / Asset, plus mass-cancel-on-behalf. | done | `MassCancelCommand` carries all five filters; `MatchingEngine.MatchesMassCancelFilter` (`src/B3.Exchange.Matching/MatchingEngine.cs:1072-1081`) applies them; the gateway rejects unsupported `MassActionType` values with spec `MassActionRejectReason`. Immediate order entry paths (`SimpleNewOrder` 100, `NewOrderSingle` 102) populate `OrdTagId` / `InvestorId` on the command. Triggered Stop/StopLimit orders propagate `OrdTagId` / `InvestorId` to the resulting resting limit (#453), and the snapshot codec round-trips `OrdTagId` / `Asset` / `InvestorId` on resting orders (#455) and `OrdTagId` / `InvestorId` on resting stops (#453) so restart and replay don't silently miss filters. `OrderCancelReplaceRequest` may also mutate `InvestorID` on the working order per spec §7.4 (#451); `OrdTagID` is preserved (absent from the spec mutation table) and `SimpleModifyOrder` is restricted to `orderQty` / `Price` per spec §7.3.2. | medium | [#412](https://github.com/pedrosakuma/B3MatchingPlatform/issues/412) (closed), [#449](https://github.com/pedrosakuma/B3MatchingPlatform/issues/449) (closed), [#451](https://github.com/pedrosakuma/B3MatchingPlatform/issues/451) (closed), [#453](https://github.com/pedrosakuma/B3MatchingPlatform/issues/453) (closed), [#455](https://github.com/pedrosakuma/B3MatchingPlatform/issues/455) (closed) |
| <a id="gap-20"></a>GAP-20 | 4.9 | **Throttle** — sliding window of N messages / M ms, per session. Reject violations with `BusinessMessageReject "Throttle limit exceeded"`. | done | Sliding-window per-session throttle with BMR emission implemented. | medium | [#56](https://github.com/pedrosakuma/B3MatchingPlatform/issues/56) (closed) |
| <a id="gap-21"></a>GAP-21 | 4.10 | **Other basic gateway validations** — `<CR>`/`<LF>` check on deskID/senderLocation/enteringTrader/executingTrader; varData length checks on `memo`/`deskID`. | done | CR/LF rejection + per-field length caps implemented in `HeaderValidation` / `EntryPointVarData`. | medium | [#57](https://github.com/pedrosakuma/B3MatchingPlatform/issues/57) (closed) |
| <a id="gap-21a"></a>GAP-21a | 8.2 / pre-trade risk | **Max open orders per EnteringFirm** — configurable simulator risk cap. | done | `maxOpenOrdersPerFirm` rejects new orders that would exceed the firm cap with `OrdRejReason=3 (OrderExceedsLimit)`, the closest documented B3/FIX reason for a risk/admission limit breach. | high | [#430](https://github.com/pedrosakuma/B3MatchingPlatform/issues/430) |

### Order types / TIF / advanced functionality

Following ADR 0012 — Exchange-day boundary, order-handling
protections (STPC, Market Protections), matching-side extensions
(Sweep & Cross), and **options as listed instruments — order
entry, matching, market data emission, expiry-driven
SecurityStatus** — are in-scope. Pricing / greeks / settlement /
exercise / assignment of options are out-of-scope (delegated to
companion repos: hypothetical `B3OptionsAnalytics` for
pricing, clearing simulator per ADR 0005 for exercise and
settlement). Market-maker contract enforcement (spread,
presence, minimum quantity) is out — the venue accepts orders;
contract supervision is a post-trade analytics concern.

| # | Spec § | Item | Status | Severity | Issue |
| --- | --- | --- | --- | --- | --- |
| <a id="gap-22"></a>GAP-22 | 7.1.1–7.1.6 | Stop / StopLimit / Market-with-leftover-as-Limit (`K`) / RLP (`W`) / Pegged Midpoint (`P`) order types | partial | low (RLP+Pegged are non-goals) | Stop / StopLimit / `K` are implemented end-to-end (engine trigger book + retired ER + UMDF). RLP (`W`) and Pegged Midpoint (`P`) are decoded but rejected with `OrdRejReason=11 (UnsupportedOrderCharacteristic)`. |
| <a id="gap-23"></a>GAP-23 | 7.1.7–7.1.14 | `GTC` / `GTD` / `MOC` (AtClose) / `MOA` (GoodForAuction) time-in-force | done | — | `GTC`, `MOC` (AtClose), `MOA` (GoodForAuction) and now `GTD` are end-to-end. `GTD` plumbs `ExpireDate` (uint16 days-since-epoch) through the decoder → `NewOrderCommand`/replace, rests like a Day/GTC order, and is expired at the daily-reset boundary by `GtdExpirySweeper` (engine `ExpireGtdOrders`). Snapshot codec v6 persists `ExpireDate`. ([#499](https://github.com/pedrosakuma/B3MatchingPlatform/issues/499), [#503](https://github.com/pedrosakuma/B3MatchingPlatform/pull/503)) Entry-time rejection of already-elapsed `ExpireDate` is a refinement tracked by [#504](https://github.com/pedrosakuma/B3MatchingPlatform/issues/504). |
| <a id="gap-24"></a>GAP-24 | 7.1.16–7.1.17 | Iceberg / disclosed quantity (`MaxFloor`), `MinQty` | done | — | Both implemented with validation (`MaxFloor` in `(0, Quantity]`, multiple of `lotSize`; `MinQty` in `(0, Quantity]`) and rejection paths (`RejectReason.InvalidField` / `RejectReason.MinQtyNotMet`). |
| <a id="gap-25"></a>GAP-25 | 7.1.20 | In-flight modification semantics (priority loss rules) | partial | medium | — |
| <a id="gap-26"></a>GAP-26 | 8.3 | Daily GTC/GTD restatement at session boundary (carry-over + re-emit ER) | done | — | At each daily-reset boundary the exchange emits a private restatement `ExecutionReport_Modify` (`OrdStatus=RESTATED 'R'`, `ExecRestatementReason=GT_RESTATEMENT 1`) for every surviving GTC and unexpired-GTD order, plus parked GTC stop / stop-limit orders. The restatement echoes the order's real `OrdType` / `TimeInForce` / `ExpireDate` / `StopPx`, reports new-trading-day quantities (`CumQty=0`, `LeavesQty == OrderQty == open`, iceberg-aware), and changes no book state (no UMDF frame, no `RptSeq` advance, no eviction). Runs after `GtdExpirySweeper` so past-dated GTD orders are cancelled, not restated. ([#498](https://github.com/pedrosakuma/B3MatchingPlatform/issues/498), [#507](https://github.com/pedrosakuma/B3MatchingPlatform/pull/507)) Day-order boundary expiry is tracked separately by [#506](https://github.com/pedrosakuma/B3MatchingPlatform/issues/506). |
| <a id="gap-27"></a>GAP-27 | 15.4 | Self-Trading Prevention (STPC) | missing | medium (in-scope per ADR 0012) | covered by [#14](https://github.com/pedrosakuma/B3MatchingPlatform/issues/14) |
| <a id="gap-28"></a>GAP-28 | 15.5 | Market Protections (price collars / fat-finger / max value) | partial | medium (in-scope per ADR 0012) | Engine-side static per-instrument price bands, auction-phase TOP collars, per-instrument max order quantity, and per-instrument max order value are implemented with existing ER reject reasons. The gateway dynamic last-trade-relative `priceBandPercent` remains as an outer decode-time guardrail. Outstanding: EntryPoint `protectionPrice` semantics for market orders are deferred. Tracked by [#500](https://github.com/pedrosakuma/B3MatchingPlatform/issues/500). |
| <a id="gap-29"></a>GAP-29 | 15.1 | User-Defined Spreads (UDS) — synthetic multi-leg instruments | missing | low (boundary case; borderline between exchange-side and broker-side) | — |
| <a id="gap-30"></a>GAP-30 | 16.6 | Sweep & Cross | partial | low (in-scope per ADR 0012) | Matching semantics for `CrossType=AgainstBook` are implemented (#218), and sweep-phase UMDF `Trade_53.trdSubType` now emits `SWEEP_TRADE` (109). Remaining wire refinements: residual-cancel `ExecRestatementReason=210` is intentionally out of scope while residuals rest, and EntryPoint ER `crossType` / `crossPrioritization` echo remains pending. |
| <a id="gap-31"></a>GAP-31 | 7.1.19 / UMDF v2.2.0 `SecurityDefinition_12` | `SecurityDefinition_12` does not emit option fields (`strikePrice`, `putOrCall`, `exerciseStyle`, `contractMultiplier`, `noUnderlyings`, `optPayoutType`, `maturityMonthYear`). | missing | high | tracked via [RFC 0002](rfc/0002-equity-options-support.md) issue OPT-02 |
| <a id="gap-32"></a>GAP-32 | 8.3 / lifecycle | Expiring option series are not automatically moved to `Close` based on `ExpirationDate`. | missing | medium | tracked via [RFC 0002](rfc/0002-equity-options-support.md) issue OPT-03 |

## Maintenance notes

- When opening a GitHub issue for any row above, append the issue number to
  the **Issue** column and reference `docs/B3-ENTRYPOINT-COMPLIANCE.md#gap-NN`
  from the issue body so future readers can navigate both directions.
- When a row is fully resolved, leave the row in place but flip its **Status**
  to `done` and link the merged PR.
- When auditing a new spec revision, refresh the *Sources audited* table and
  walk every row — the spec evolves (the change log on page 9 of the PDF is a
  good starting point).
