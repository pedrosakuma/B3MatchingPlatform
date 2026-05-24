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
landed — see issues #407..#416 for the resulting partial-status narrowings.
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
  cycle. (See [#19](https://github.com/pedrosakuma/SbeB3Exchange/issues/19).)
- **FIXP session resync persistence** ([#405](https://github.com/pedrosakuma/SbeB3Exchange/issues/405)).
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
| <a id="gap-01"></a>GAP-01 | 4.4 | **Simple Open Framing Header (SOFH)** — 4 bytes (`messageLength` LE + `encodingType=0xEB50`) prepended to every SBE frame; total wire header is **12 bytes**, not 8. | done | Implemented in `EntryPointFrameReader`; `messageLength ≤ 512` enforced. | critical | [#39](https://github.com/pedrosakuma/SbeB3Exchange/issues/39) (closed) |
| <a id="gap-02"></a>GAP-02 | 3.5, 4.6.5 | **Variable-length data trailing the fixed block** (`memo`, `credentials`, `clientIP`, `clientAppName`, `clientAppVersion`). Each is `length(uint8)` + `varData`. Total frame size is `SOFH.messageLength`, not `8 + BlockLength`. | partial | Inbound varData parsing done; **outbound ExecutionReport/BMR encoders do not emit varData trailer** (memo not echoed). | high | [#40](https://github.com/pedrosakuma/SbeB3Exchange/issues/40) (closed), narrowed by [#411](https://github.com/pedrosakuma/SbeB3Exchange/issues/411) |
| <a id="gap-03"></a>GAP-03 | 4.5.7, 4.10 | **`Terminate` with `terminationCode`** on framing/decoding errors (`16=INVALID_SOFH`, `17=DECODING_ERROR`, `15=UNRECOGNIZED_MESSAGE`, `11=INVALID_SESSIONID`, `13=INVALID_TIMESTAMP`, …). Also enforce SOFH `messageLength ≤ 512` per spec. | partial | Decode errors emit Terminate with correct codes; **but all server Terminate frames encode `sessionVerID=0` regardless of session state** (spec §4.5.7 requires the active SessionVerId). | critical | [#41](https://github.com/pedrosakuma/SbeB3Exchange/issues/41) (closed), narrowed by [#407](https://github.com/pedrosakuma/SbeB3Exchange/issues/407) |

### FIXP session lifecycle

| # | Spec § | Item | Status | Gap | Severity | Issue |
| --- | --- | --- | --- | --- | --- | --- |
| <a id="gap-04"></a>GAP-04 | 4.5.2 | **`Negotiate` / `NegotiateResponse` / `NegotiateReject`** — JSON `credentials` blob (`auth_type`, `username`, `access_key`), `sessionVerID`, daily reset, and the seven reject codes (incl. `ALREADY_NEGOTIATED` carrying `currentSessionVerID`). | partial | Full handshake + monotonicity persisted (#405). **Unknown sessionID is rejected as `CREDENTIALS` instead of `INVALID_SESSIONID`** (spec §4.5.2). | high | [#42](https://github.com/pedrosakuma/SbeB3Exchange/issues/42) (closed), narrowed by [#413](https://github.com/pedrosakuma/SbeB3Exchange/issues/413) |
| <a id="gap-05"></a>GAP-05 | 4.5.3 | **`Establish` / `EstablishAck` / `EstablishReject`** with `keepAliveInterval`, `nextSeqNo`, `cancelOnDisconnect{Type,TimeoutWindow}` and the eight reject codes. | partial | Handshake/validation/EstablishmentAck.RECOVERABLE implemented. **`keepAliveInterval` lower bound is 1ms** in `EstablishValidator`; schema requires 1000–60000ms. | high | [#43](https://github.com/pedrosakuma/SbeB3Exchange/issues/43) (closed), narrowed by [#408](https://github.com/pedrosakuma/SbeB3Exchange/issues/408) |
| <a id="gap-06"></a>GAP-06 | 4.5.4, 4.5.7 | **`Sequence` (heartbeat + reset)** + **`Terminate`** message (with codes from `TerminationCode` enum — see schema lines 384–404). Spec recommends terminating after 3× `keepAliveInterval` of silence. | partial | Heartbeat / Sequence / TestRequest implemented. **Idle-timeout closes silently (no `Terminate(KEEPALIVE_INTERVAL_LAPSED=10)`)** and threshold is `1.5×keepAlive + grace` instead of 3×. | high | [#44](https://github.com/pedrosakuma/SbeB3Exchange/issues/44) (closed), narrowed by [#409](https://github.com/pedrosakuma/SbeB3Exchange/issues/409) |
| <a id="gap-07"></a>GAP-07 | 4.5.5, 4.6.2 | **Inbound `MsgSeqNum` tracking + `NotApplied`** — gap detection emits `NotApplied(fromSeqNo, count)` and updates expected next seq. Outbound seq is per-`SessionID/SessionVerID`, reset daily. | done | Inbound seq tracking + `NotApplied` implemented; outbound seq per-session persisted (#405). Daily reset is its own gap (GAP-09). | high | [#45](https://github.com/pedrosakuma/SbeB3Exchange/issues/45) (closed) |
| <a id="gap-08"></a>GAP-08 | 4.5.6 | **`RetransmitRequest` / `Retransmission`** with `RetransmitRejectCode` enum (schema 406–416). Max 1000 messages per request, single in-flight. | done | Implemented (`FixpRetransmitController`); journal-backed cold-read after #405; 1000-cap, single-in-flight, all RetransmitRejectCodes covered. | high | [#46](https://github.com/pedrosakuma/SbeB3Exchange/issues/46) (closed) |
| <a id="gap-09"></a>GAP-09 | 4.5.1 | **Daily reset** — outbound + inbound seq reset to 1 at the start of each trading day. | partial | `DailyResetScheduler` runs at boundary, but **`TerminateAllSessions` closes with `CloseKind.HostShutdown` which preserves persisted seq state under #405** — next reconnect rehydrates yesterday's seqs. **Regression introduced by #405.** | medium | [#47](https://github.com/pedrosakuma/SbeB3Exchange/issues/47) (closed), narrowed by [#416](https://github.com/pedrosakuma/SbeB3Exchange/issues/416) |

### Application-level header

| # | Spec § | Item | Status | Gap | Severity | Issue |
| --- | --- | --- | --- | --- | --- | --- |
| <a id="gap-10"></a>GAP-10 | 4.6.3.1 | **Inbound `sessionID` validation** — spec §4.10 mandates `BusinessMessageReject(33003, "Wrong sessionID in businessHeader")` on mismatch. | partial | `sessionID` + `msgSeqNum` validated in `HeaderValidation`. **`sendingTime` (offset 8) is not read or validated** despite spec requirement. | high | [#45](https://github.com/pedrosakuma/SbeB3Exchange/issues/45) (closed), narrowed by [#414](https://github.com/pedrosakuma/SbeB3Exchange/issues/414) |
| <a id="gap-11"></a>GAP-11 | 4.6.4 | **`receivedTime` (tag 35544)** optional field added in schema v3 to ER\_New / ER\_Modify / ER\_Cancel. Carries the gateway-reception timestamp. | partial | `receivedTime` emitted on ER\_New/Modify/Cancel. **Other declared varData trailing fields (`memo` echo, `deskID`, `text`) are not emitted** — see GAP-02. | medium | [#46](https://github.com/pedrosakuma/SbeB3Exchange/issues/46) (closed), narrowed by [#411](https://github.com/pedrosakuma/SbeB3Exchange/issues/411) |
| <a id="gap-12"></a>GAP-12 | 4.6.3.1 | **`marketSegmentID` for routing** — spec says the gateway uses this header field to route to the matching engine. | deviation | Simulator routes by `SecurityID` (1:1 with channel in our config). Working, but a real B3 client that relies on cross-segment instrument codes will be surprised. | low | documented in "Conscious deviations" — keep here for traceability. |
| <a id="gap-13"></a>GAP-13 | 4.6.3.2 | **`OutboundBusinessHeader.eventIndicator`** — bit flags (`PossResend`, `LowPriority`). | done | `PossResend` is now set on journal-replayed business frames by `FixpOutboundEncoder`; `LowPriority` not used by the simulator. | low | implicit via GAP-08 closure. |

### Application-level messages

| # | Spec § | Item | Status | Gap | Severity | Issue |
| --- | --- | --- | --- | --- | --- | --- |
| <a id="gap-14"></a>GAP-14 | 4.6.1 | **`BusinessMessageReject` (template 206)** for application-level rejects (bad sessionID, throttle violations, varData too long, line breaks in deskID/senderLocation/enteringTrader/executingTrader). | done | BMR encoder + dispatch routing implemented. (Sub-issue: BMR's own varData trailer is not emitted — tracked under GAP-02/#411.) | high | [#50](https://github.com/pedrosakuma/SbeB3Exchange/issues/50) (closed) |
| <a id="gap-15"></a>GAP-15 | 4.6.1 | **`NewOrderSingle` (102)** and **`OrderCancelReplaceRequest` (104)** — the full templates with iceberg/stop/strategy fields. Spec lists these as the canonical messages; `Simple*` variants are explicitly the low-feature subset. | partial | Templates 102/104 decode + accept Limit/Market only. **Unsupported sub-features (stop, iceberg, GTC, …) surface as `BusinessMessageReject(33003)` instead of `ExecutionReport_Reject(OrdRejReason=11=UnsupportedOrderCharacteristic)`** per spec. | medium | [#47](https://github.com/pedrosakuma/SbeB3Exchange/issues/47) (closed), narrowed by [#415](https://github.com/pedrosakuma/SbeB3Exchange/issues/415) |
| <a id="gap-16"></a>GAP-16 | 4.6.1 | **`NewOrderCross` (106)**. | done | Cross-order decode + matching implemented. | low | [#48](https://github.com/pedrosakuma/SbeB3Exchange/issues/48) (closed) |
| <a id="gap-17"></a>GAP-17 | 8.2 | **`OrdRejReason` mapping** — codes `1=UnknownSymbol`, `3=OrderExceedsLimit`, `5=UnknownOrder`, `6=DuplicateOrder`, `11=UnsupportedOrderCharacteristic`, etc. | partial | Engine→wire mapping table implemented in `FixpOutboundEncoder`. **`UnsupportedOrderCharacteristic (11)` path not wired** for unsupported sub-features (currently BMR'd — see GAP-15). | medium | [#49](https://github.com/pedrosakuma/SbeB3Exchange/issues/49) (closed), narrowed by [#415](https://github.com/pedrosakuma/SbeB3Exchange/issues/415) |

### Operational / risk features

| # | Spec § | Item | Status | Gap | Severity | Issue |
| --- | --- | --- | --- | --- | --- | --- |
| <a id="gap-18"></a>GAP-18 | 4.7 | **Cancel-on-Disconnect (CoD)** — `CancelOnDisconnectType` (4 modes) + `CODTimeoutWindow` in `Establish`. Cancels non-GT working orders on triggering events; spec §4.7.3 enumerates gateway-forced disconnects that also fire CoD if enabled. | partial | On-disconnect half (modes 1+3) implemented with grace-window timer and per-channel filtering. **On-Terminate half (modes 2+3) still deferred** — peer-Terminate does not trigger CoD. Non-GT filter degenerate (TIF gap #GAP-23). | high | [#50](https://github.com/pedrosakuma/SbeB3Exchange/issues/50) (closed), [#54](https://github.com/pedrosakuma/SbeB3Exchange/issues/54) (closed), narrowed by [#410](https://github.com/pedrosakuma/SbeB3Exchange/issues/410) |
| <a id="gap-19"></a>GAP-19 | 4.8 | **Mass Cancel (`OrderMassActionRequest` 701 / `OrderMassActionReport` 702)** with filters Side / SecurityID / OrdTagID / Asset, plus mass-cancel-on-behalf. | partial | Side + SecurityID filters work. **OrdTagID / Asset / InvestorID (on-behalf) decoded but unconditionally rejected** with free-text reason rather than spec MassActionRejectCode. | high | [#51](https://github.com/pedrosakuma/SbeB3Exchange/issues/51) (closed), narrowed by [#412](https://github.com/pedrosakuma/SbeB3Exchange/issues/412) |
| <a id="gap-20"></a>GAP-20 | 4.9 | **Throttle** — sliding window of N messages / M ms, per session. Reject violations with `BusinessMessageReject "Throttle limit exceeded"`. | done | Sliding-window per-session throttle with BMR emission implemented. | medium | [#52](https://github.com/pedrosakuma/SbeB3Exchange/issues/52) (closed) |
| <a id="gap-21"></a>GAP-21 | 4.10 | **Other basic gateway validations** — `<CR>`/`<LF>` check on deskID/senderLocation/enteringTrader/executingTrader; varData length checks on `memo`/`deskID`. | done | CR/LF rejection + per-field length caps implemented in `HeaderValidation` / `EntryPointVarData`. | medium | [#57](https://github.com/pedrosakuma/SbeB3Exchange/issues/57) (closed) |
| <a id="gap-21a"></a>GAP-21a | 8.2 / pre-trade risk | **Max open orders per EnteringFirm** — configurable simulator risk cap. | done | `maxOpenOrdersPerFirm` rejects new orders that would exceed the firm cap with `OrdRejReason=3 (OrderExceedsLimit)`, the closest documented B3/FIX reason for a risk/admission limit breach. | high | [#430](https://github.com/pedrosakuma/SbeB3Exchange/issues/430) |

### Order types / TIF / advanced functionality

These are tracked here for completeness; most are explicit non-goals today.

| # | Spec § | Item | Status | Severity | Issue |
| --- | --- | --- | --- | --- | --- |
| <a id="gap-22"></a>GAP-22 | 7.1.1–7.1.6 | Stop / StopLimit / Market-with-leftover-as-Limit (`K`) / RLP (`W`) order types | missing | low (non-goal) | [#53](https://github.com/pedrosakuma/SbeB3Exchange/issues/53) |
| <a id="gap-23"></a>GAP-23 | 7.1.7–7.1.14 | `GTC` / `GTD` / `MOC` / `MOA` time-in-force | missing | low (non-goal) | [#54](https://github.com/pedrosakuma/SbeB3Exchange/issues/54) |
| <a id="gap-24"></a>GAP-24 | 7.1.16–7.1.17 | Iceberg / disclosed quantity, MinQty | missing | low (non-goal) | [#55](https://github.com/pedrosakuma/SbeB3Exchange/issues/55) |
| <a id="gap-25"></a>GAP-25 | 7.1.20 | In-flight modification semantics (priority loss rules) | partial | medium | [#56](https://github.com/pedrosakuma/SbeB3Exchange/issues/56) |
| <a id="gap-26"></a>GAP-26 | 8.3 | Daily GTC/GTD restatement | missing | low (depends on GAP-23) | [#57](https://github.com/pedrosakuma/SbeB3Exchange/issues/57) |
| <a id="gap-27"></a>GAP-27 | 15.4 | Self-Trading Prevention | missing | medium | covered by #14 |
| <a id="gap-28"></a>GAP-28 | 15.5 | Market Protections | missing | low (non-goal) | — |
| <a id="gap-29"></a>GAP-29 | 15.1 | User-Defined Spreads (UDS) | missing | low | — |
| <a id="gap-30"></a>GAP-30 | 16.6 | Sweep & Cross | missing | low | — |

## Maintenance notes

- When opening a GitHub issue for any row above, append the issue number to
  the **Issue** column and reference `docs/B3-ENTRYPOINT-COMPLIANCE.md#gap-NN`
  from the issue body so future readers can navigate both directions.
- When a row is fully resolved, leave the row in place but flip its **Status**
  to `done` and link the merged PR.
- When auditing a new spec revision, refresh the *Sources audited* table and
  walk every row — the spec evolves (the change log on page 9 of the PDF is a
  good starting point).
