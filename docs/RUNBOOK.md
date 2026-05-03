# Operator runbook

This runbook is the day-2 reference for running the **B3MatchingPlatform**
exchange simulator. Architecture and protocol details live in
[`B3-ENTRYPOINT-ARCHITECTURE.md`](./B3-ENTRYPOINT-ARCHITECTURE.md) and
[`EXCHANGE-SIMULATOR.md`](./EXCHANGE-SIMULATOR.md); this document focuses
on the operator-facing surface: bringing the host up, watching it, tuning
it, and triggering the recovery scenarios that consumers care about.

---

## 1. Bringing the host up

### 1.1 Local (dotnet)

```bash
# Build (warnings-as-errors).
dotnet build SbeB3Exchange.slnx -c Release

# Run with the default config.
dotnet run --project src/B3.Exchange.Host -c Release -- config/exchange-simulator.json
```

Expected output (truncated):

```
info: B3.Exchange.Host[...] starting host: tcp=0.0.0.0:9876 http=0.0.0.0:8080 channels=1
info: B3.Exchange.Core.ChannelDispatcher[...] channel 84 ready (instruments=...)
info: B3.Exchange.Gateway.EntryPointListener[...] listening on 0.0.0.0:9876
```

The default config (`config/exchange-simulator.json`) declares:

* TCP listener on `0.0.0.0:9876` (EntryPoint).
* Operator HTTP on `0.0.0.0:8080`.
* One channel (`84`) publishing UMDF incremental on `224.0.20.84:30084`,
  snapshots on `224.0.20.184:30184`, and `SecurityDefinition_12` on
  `224.0.21.84:31084`.
* Two firms, four pre-provisioned `(firmId, sessionId, accessKey)` tuples.

### 1.2 Docker

```bash
docker build -t sbeb3exchange:latest .
docker run --rm --network host \
    -v "$(pwd)/config:/app/config" \
    sbeb3exchange:latest
```

`--network host` is required so the multicast publisher and the EntryPoint
TCP listener are reachable on the host LAN. The mounted volume lets you
edit `config/*.json` without rebuilding the image.

### 1.3 Companion synthetic trader

The repo also ships a synthetic trader that drives continuous order flow
against the simulator (market-maker + noise-taker on PETR4 by default).

```bash
# Side-by-side via compose:
docker compose -f docker-compose.synthtrader.yml up
```

See §4 for tuning recipes.

---

## 2. Operator surface

### 2.1 Liveness & readiness

| Endpoint | Method | Status code | Body | Use it for |
| --- | --- | --- | --- | --- |
| `/health/live` | GET | `200` healthy / `503` stale dispatchers | `OK channels=N` or `DOWN <details>` | Kubernetes liveness probe. |
| `/health/ready` | GET | `200` once every probe is ready / `503` otherwise | `READY probes=N` or `NOT_READY <names>` | Kubernetes readiness probe; gates traffic until the listeners and channels are wired. |

`livenessStaleMs` (default `5000` ms in `http.livenessStaleMs`) is the
threshold for "dispatcher heartbeat is stale". Bump it if you run on a
loaded host or with very long snapshot rotation cadences.

```bash
curl -fsS http://127.0.0.1:8080/health/live
curl -fsS http://127.0.0.1:8080/health/ready
```

### 2.2 Metrics — `/metrics`

Prometheus 0.0.4 text exposition. Hand-rolled (no `prometheus-net`
dependency). Key counters / gauges:

| Metric | Type | What |
| --- | --- | --- |
| `exch_session_established_total` | counter | FIXP sessions that reached `Established` (initial + rebind). |
| `exch_session_suspended_total` | counter | Transport drops while `Established` (issue #69a). |
| `exch_session_rebound_total` | counter | Successful re-attaches via `Establish` on a fresh TCP. |
| `exch_session_reaped_total` | counter | Suspended sessions closed by the reaper after `SuspendedTimeoutMs`. |
| `exch_session_cancel_on_disconnect_fired_total` | counter | CoD timer fired and a session-scoped mass-cancel was enqueued (issue #54 / GAP-18). |
| `exch_throttle_accepted_total` | counter | Inbound app messages allowed through the per-session sliding-window throttle (#56 / GAP-20). |
| `exch_throttle_rejected_total` | counter | Inbound app messages rejected with `BusinessMessageReject("Throttle limit exceeded")`. |
| `exch_session_state` | gauge | `0`=Idle, `1`=Negotiated, `2`=Established, `3`=Suspended, `4`=Terminated. |
| `exch_session_attached` | gauge | `1` while a TCP transport is attached, `0` while Suspended. |

```bash
curl -fsS http://127.0.0.1:8080/metrics | head -40
```

### 2.3 Session diagnostics

| Endpoint | Method | Returns |
| --- | --- | --- |
| `/sessions` | GET | JSON array of every registered FIXP session (Established + Suspended). |
| `/sessions/{id}` | GET | Per-session detail: state, attached/idle timers, retx depth, last activity, throttle counters. `404` if unknown. |
| `/firms` | GET | JSON list of `(id, name, enteringFirmCode)` tuples loaded from `firms[]`. |

Useful one-liners:

```bash
# Who is connected right now?
curl -sS http://127.0.0.1:8080/sessions | jq '.[] | {id: .sessionId, state: .state, attached: .isAttached, retx: .retxBufferDepth}'

# Drill into one session.
curl -sS http://127.0.0.1:8080/sessions/conn-2a | jq

# Firms.
curl -sS http://127.0.0.1:8080/firms | jq
```

### 2.4 Channel operator endpoints

All channel endpoints dispatch the work onto the channel's inbound queue
and return `202 Accepted` immediately; `404` on unknown channel; `503`
when the bounded queue is full (`DropWrite`).

| Endpoint | Method | What |
| --- | --- | --- |
| `/channel/{ch}/snapshot-now` | POST | Force the next snapshot rotation tick to fire. Useful for triggering a recovery-bootstrap window for a freshly connected MD consumer. |
| `/channel/{ch}/bump-version` | POST | Advance the channel's `SessionVerId` (snapshot generation), forcing consumers to re-bootstrap. |

### 2.5 Daily reset — `/admin/daily-reset`

```bash
curl -sS -X POST http://127.0.0.1:8080/admin/daily-reset
# accepted daily-reset terminated=N
```

Per-spec daily rollover (#GAP-09 / #47):

* Every live FIXP session is sent `Terminate(FINISHED)`.
* Clients reconnect with `Negotiate` + `Establish(nextSeqNo=1)`.
* Sequence numbers reset; retransmission buffers are cleared.

The same code path is wired to the optional scheduled trigger (set
`hostConfig.dailyReset` to enable; absent ⇒ admin-trigger only).

---

## 3. Multi-firm setup

The default config ships **two firms × two sessions each**:

```jsonc
"firms": [
  { "id": "FIRM01", "name": "Alpha Corretora", "enteringFirmCode": 100 },
  { "id": "FIRM02", "name": "Beta Corretora",  "enteringFirmCode": 200 }
],
"sessions": [
  { "sessionId": "10101", "firmId": "FIRM01", "accessKey": "dev-key-1" },
  { "sessionId": "10102", "firmId": "FIRM01", "accessKey": "dev-key-2" },
  { "sessionId": "20201", "firmId": "FIRM02", "accessKey": "dev-key-3" },
  { "sessionId": "20202", "firmId": "FIRM02", "accessKey": "dev-key-4" }
]
```

Rules (enforced by `FirmRegistry`):

* `sessionId` MUST parse as a decimal `uint32` (FIXP wire `SessionID`).
* `enteringFirmCode` is stamped on every outbound SBE business header.
* When `auth.devMode = false`, the peer's `Negotiate.access_key` MUST
  exactly match the configured `accessKey` for that `sessionId`; mismatch
  → `NegotiationReject(CREDENTIALS)`.
* The host picks a "fallback default tenant" used to stamp
  `EnteringFirm` on TCP accept *before* `Negotiate` completes — that
  choice is the **lexicographically-first** `sessionId` string. With
  numeric sessionIds of equal width (the format the JSON enforces), the
  lex order matches numeric order; mixing widths (e.g. `"2"` vs
  `"10101"`) silently changes the fallback. Keep them aligned.

To add a third firm:

1. Append a `firms[]` entry with a unique `enteringFirmCode`.
2. Append one or more `sessions[]` entries pointing at that `firmId`.
3. Restart the host.

There is no hot reload today (intentional — the FirmRegistry is read
once at startup so the inbound recv thread has a fixed identity table).

---

## 4. Synthetic trader recipes

`config/synthetic-trader.json` is the bundled sample. Five strategies
ship today (per `SyntheticTraderConfig.cs`):

| Strategy `kind` | Knobs | Behaviour |
| --- | --- | --- |
| `marketMaker` | `levelsPerSide`, `quoteSpacingTicks`, `replaceDistanceTicks`, `quantity` | Posts `levelsPerSide` resting quotes either side of the mid; replaces when the mid drifts more than `replaceDistanceTicks`. |
| `noiseTaker` | `orderProbability`, `maxLotMultiple`, `crossTicks` | On each tick, with `orderProbability`, sends an aggressive marketable order up to `maxLotMultiple` × lot, priced `crossTicks` past the touch. |
| `meanReverting` | `alpha`, `entryThresholdTicks`, `crossTicks`, `lotsPerOrder` | Tracks an EWMA of the mid (`alpha` ∈ (0,1]); when the live mid deviates by ≥ `entryThresholdTicks`, sends a marketable IOC of `lotsPerOrder × lotSize` in the *contrarian* direction. |
| `momentum` | `triggerTicks`, `crossTicks`, `lotsPerOrder` | Compares the mid to the previous tick; when the per-tick step is ≥ `triggerTicks`, sends a marketable IOC *in the direction of the move*. |
| `sweeper` | `triggerProbability`, `sweepLots`, `crossTicks` | Per-tick fire (uniform side) of a large marketable IOC sized `sweepLots × lotSize`, priced `crossTicks` past the mid. Configure `crossTicks ≥ N × marketMaker.quoteSpacingTicks` to actually walk N levels. |

Tuning recipes:

* **Quiet book (smoke test):** one `marketMaker` with `levelsPerSide=1`,
  `quoteSpacingTicks=1`, `quantity=100`. Drop the `noiseTaker`.
* **Continuous trade flow:** `marketMaker` with `levelsPerSide=3` plus
  one `noiseTaker` at `orderProbability=0.3`. (This is the default.)
* **Trend / mean-reversion mix:** add a `momentum` strategy
  (`triggerTicks=2`, `lotsPerOrder=1`) plus a `meanReverting`
  (`alpha=0.1`, `entryThresholdTicks=5`) — momentum amplifies short
  mid-drift bursts and mean-revert fades extended drifts; together they
  produce more interesting trade prints than `noiseTaker` alone.
* **Trade-through / sweep exercise:** add a `sweeper` with
  `triggerProbability=0.02`, `sweepLots` ≥ 5×`marketMaker.quantity`/`lotSize`,
  and `crossTicks ≥ levelsPerSide × quoteSpacingTicks` so the sweep walks
  the full visible book — exercises consumer-side trade-through fills
  and snapshot recovery.
* **Burst load (throttle exercise):** raise the synthetic trader's
  `tickIntervalMs` down to `10–20` and bump `noiseTaker.orderProbability`
  to `0.9` — the per-session throttle (`exch_throttle_rejected_total`)
  starts firing once the configured budget is exceeded.
* **Multi-firm flow:** copy `synthetic-trader.json` per firm, pointing
  each at a distinct `firm` code (matching the host's `enteringFirmCode`)
  and a distinct EntryPoint `port` if you split the host. The sample
  uses `firm: 1` (the legacy single-tenant fallback).

---

## 5. Triggering recovery scenarios

The simulator exists to test consumers' recovery semantics. Each scenario
below pairs an operator action with the expected consumer-visible
behaviour.

### 5.1 MD consumer cold-start / lost packet recovery

Goal: force a snapshot rotation so a consumer that just joined (or
detected a UMDF gap) can rebuild its book.

```bash
curl -X POST http://127.0.0.1:8080/channel/84/snapshot-now
```

Expected: the next `SnapshotFull_*` frame on `224.0.20.184:30184` is
emitted within one cadence tick. Watch the consumer's snapshot-applied
counter, then verify the incremental stream on `224.0.20.84:30084` flows
without further gaps.

### 5.2 Force a UMDF channel rebootstrap

Goal: invalidate every consumer's cached book and force a full re-bootstrap.

```bash
curl -X POST http://127.0.0.1:8080/channel/84/bump-version
```

Expected: `SessionVerId` on emitted frames increments; conformant
consumers detect the version bump and replay snapshot → incremental.

### 5.3 EntryPoint Suspend → re-attach (FIXP)

Goal: prove the FIXP `Suspended` state buffers ER frames and that a
re-`Establish` on a fresh TCP replays them with `PossResend=1`.

1. With a client connected and Established, kill the TCP socket
   (`tcpkill`, firewall drop, `iptables -A OUTPUT -p tcp ...`, or just
   close the client process).
2. Watch `/sessions/{id}` — `state` flips from `2` (Established) to `3`
   (Suspended); `isAttached` flips from `1` to `0`. `retxBufferDepth`
   keeps growing as Core emits ER frames against the resting orders the
   session left on the book.
3. Reconnect the same client and send a fresh `Establish` with the same
   `sessionId` and the next-expected `nextSeqNo`. The session's
   `state` returns to `Established`; on a `RetransmitRequest` the
   buffered ER frames are replayed with `eventIndicator.PossResend=1`.

### 5.4 Cancel-on-Disconnect (CoD)

Goal: prove a CoD-armed session has its resting orders cancelled when
the client doesn't reconnect inside the grace window.

1. Open a session whose `Establish` claimed
   `cancelOnDisconnectType ∈ { CANCEL_ON_DISCONNECT_ONLY,
   CANCEL_ON_DISCONNECT_OR_TERMINATE }` and a non-zero
   `codTimeoutWindow` (e.g. `5000` ms).
2. Place a couple of resting orders and verify they appear on the book
   (UMDF MBO).
3. Drop the TCP transport. The session goes Suspended.
4. Wait past `codTimeoutWindow`.
5. Watch `exch_session_cancel_on_disconnect_fired_total` increment by
   `1`. The consumer side (UMDF MBO) shows `Inc_DeleteOrder` events for
   each resting order belonging to that session.

If the client reconnects inside the window, the CoD timer is disarmed
and no mass-cancel fires.

### 5.5 Daily reset

Goal: rehearse the trading-day rollover.

```bash
curl -X POST http://127.0.0.1:8080/admin/daily-reset
```

Expected: every connected FIXP session receives `Terminate(FINISHED)`
and the TCP connection is closed. Consumers re-`Negotiate` and
`Establish(nextSeqNo=1)`. UMDF channels keep flowing (separate plane).
`exch_session_reaped_total` does **not** advance — daily reset is a
proper Terminate, not a reap.

### 5.6 Throttle exercise

Goal: trigger the per-session sliding-window throttle (#56 / GAP-20).

1. Configure the synthetic trader (or a custom client) to send orders
   faster than the per-session budget (`throttleMessagesPerSecond` in
   FixpSessionOptions, default in `FixpSessionOptions.Default`).
2. Watch `exch_throttle_rejected_total` start incrementing; the client
   receives `BusinessMessageReject("Throttle limit exceeded")` (template
   206) for each rejected message.
3. Stop the burst; the rejection counter freezes and the accept counter
   resumes growing.

---

## 6. Common tuning + debugging

### 6.1 "Liveness probe is flapping"

* Check `livenessStaleMs` against your slowest channel's actual tick
  cadence; if a channel has no instruments, its dispatcher only ticks
  on inbound work. Bump `livenessStaleMs` or add an instrument.
* `LastTickUnixMs == 0` is *not* counted as stale — that's the
  startup-tolerance branch in `HttpServer.MapGet("/health/live", ...)`.

### 6.2 "Suspended sessions are filling up"

* Tune `SuspendedTimeoutMs` (FixpSessionOptions) so the reaper closes
  abandoned sessions promptly; track `exch_session_reaped_total` to
  confirm.
* CoD timer is independent of (and should be **shorter than**) the
  reaper timeout — see §5.4. CoD must complete before the session is
  reaped or the cancel ER frames can't reach retx.

### 6.3 "Consumer never sees my replay"

* Check `/sessions/{id}.retxBufferDepth` — if it's `0`, the buffered
  frames may have been evicted (FIFO, bounded by
  `RetransmitBufferCapacity`). Bump capacity if your consumer's
  reconnect time is large.
* `RetransmitRequest` for frames outside the retained range gets
  `RetransmitReject(OUT_OF_RANGE)` — that's the protocol-level signal,
  not silence.

### 6.4 "Multicast packets are not arriving"

* `--network host` on Docker (Linux): mandatory for multicast sockets
  to reach LAN consumers.
* TTL: `channels[*].ttl` defaults to `1`. Bump to `2+` if your consumer
  is on a different L2 segment.
* Verify with `tcpdump -i <iface> -n udp port 30084`.

### 6.5 "Negotiate keeps getting rejected"

* `auth.devMode = false` requires the client's `access_key` to match
  the configured one **exactly** (byte-equal). Quote/encode carefully.
* `sessionId` MUST parse as `uint32`. A non-numeric or out-of-range
  value never finds a registered session →
  `NegotiationReject(SESSION_BLOCKED)`.

### 6.6 "Find which session owns a stuck order"

```bash
# 1) sessionId ↔ enteringFirm map (snapshot).
curl -sS http://127.0.0.1:8080/sessions | jq '.[] | {id: .sessionId, firm: .enteringFirm}'

# 2) Cross-check with /firms.
curl -sS http://127.0.0.1:8080/firms | jq
```

Internally, the gateway-side `OrderOwnershipMap` (post-#66) is the
authority; it routes passive ER + CoD mass-cancels back to the owning
session's retx buffer.

---

## 7. Where to look in the code

| Subsystem | Path |
| --- | --- |
| Operator HTTP surface | `src/B3.Exchange.Host/HttpServer.cs` |
| Host wiring + config schema | `src/B3.Exchange.Host/ExchangeHost.cs`, `src/B3.Exchange.Host/HostConfig.cs` |
| FIXP session lifecycle | `src/B3.Exchange.Gateway/FixpSession.cs`, `FixpStateMachine.cs` |
| Mass-cancel / CoD plumbing | `src/B3.Exchange.Gateway/OrderOwnershipMap.cs`, `FixpSession.cs` (CoD) |
| Per-channel matching | `src/B3.Exchange.Core/ChannelDispatcher.cs`, `src/B3.Exchange.Matching/MatchingEngine.cs` |
| UMDF wire encoders | `src/B3.Umdf.WireEncoder/` |
| Synthetic trader strategies | `src/B3.Exchange.SyntheticTrader/MarketMakerStrategy.cs`, `NoiseTakerStrategy.cs` |

For protocol semantics see
[`B3-ENTRYPOINT-COMPLIANCE.md`](./B3-ENTRYPOINT-COMPLIANCE.md) (gap
status table) and
[`B3-ENTRYPOINT-ARCHITECTURE.md`](./B3-ENTRYPOINT-ARCHITECTURE.md)
(threading model and component boundaries).
