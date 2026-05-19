# Operator runbook

This runbook is the day-2 reference for running the **B3MatchingPlatform**
exchange simulator. Architecture and protocol details live in
[`B3-ENTRYPOINT-ARCHITECTURE.md`](./B3-ENTRYPOINT-ARCHITECTURE.md) and
[`EXCHANGE-SIMULATOR.md`](./EXCHANGE-SIMULATOR.md); this document focuses
on the operator-facing surface: bringing the host up, watching it, tuning
it, and triggering the recovery scenarios that consumers care about.

> **Single-active-instance.** The simulator is designed to run as a
> single active instance per channel set. Active-active HA is **out of
> scope**: running two instances against the same UMDF multicast group
> would emit duplicate sequence numbers and ER frames, and the engine
> state lives in process memory only. When HA becomes a goal it will
> require external coordination (lease / fencing) plus persistent
> journaling — neither of which exists today. Plan deployments around a
> single primary with manual failover.

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

> **Scope note.** Everything under `/admin/*` here is **operational
> admin** of the running matching engine. Post-trade artifacts
> (audit log, EOD fills export, future clearing files) live on a
> separate boundary and are **file-based**, not REST — see
> [ADR 0001](./adr/0001-post-trade-boundary-and-eod-file-export.md).
> The one exception is the **trigger** for the EOD export
> (`POST /admin/post-trade/eod-export?channel=N&date=YYYY-MM-DD`,
> documented in §7.9 below): it lives under `/admin/*` because it is
> an operator-only action against the running host. The **artifact
> it produces** is a CSV in a configured drop directory, not a REST
> body.

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
| `/channel/{ch}/trade-bust/{tradeId}` | POST | Publish a `TradeBust_57` (trade reversal) on the incremental channel. Required query: `securityId`. Optional: `priceMantissa`, `size`, `tradeDate` (LocalMktDate; days since 1970-01-01, default = today UTC). The bust does not reach back into the post-trade audit log (issue #329) — the operator supplies the echo fields the consumer audits, and operators reconcile against the original `fills-YYYY-MM-DD.log` record offline. The bust frame is stamped with the engine's next `RptSeq` so the per-instrument sequence stays dense. |

### 2.5 Daily reset — `/admin/daily-reset`

```bash
curl -sS -X POST http://127.0.0.1:8080/admin/daily-reset
# accepted daily-reset terminated=N
```

Per-spec daily rollover (#GAP-09 / #47):

* Every live FIXP session is sent `Terminate(FINISHED)`.
* Clients reconnect with `Negotiate` + `Establish(nextSeqNo=1)`.
* Sequence numbers reset; retransmission buffers are cleared.
* **Post-trade rollover (issue [#330](https://github.com/pedrosakuma/B3MatchingPlatform/issues/330)).**
  After session termination, the handler waits for the per-channel
  dispatcher inbound queues to drain (bounded by
  `shutdown.drainGraceMs`, same knob the graceful-shutdown path
  uses), then chains the EOD CSV export for **yesterday UTC** on
  every channel that has `postTradeAudit.eodDropDir` configured.
  See §7.9 for the file layout, atomic publish contract, and
  failure modes.

The same code path is wired to the optional scheduled trigger (set
`hostConfig.dailyReset` to enable; absent ⇒ admin-trigger only), so
both the cron-style scheduler and the operator `curl` produce
identical artifacts.

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
| `newsShock` | `meanIntervalMs`, `jitterMs`, `shockDurationMs`, `fadeDurationMs`, `levelsToSweep`, `burstQtyLots`, `directionBias` | Stateful three-phase sequence (idle → shock → fade). After a randomised idle window (`meanIntervalMs ± jitterMs`), enters a `shockDurationMs` burst emitting one marketable IOC per tick on a side picked by `directionBias` (0 = SELL only, 1 = BUY only, 0.5 = symmetric). Optional `fadeDurationMs` linearly tapers the size to zero before returning to idle. All ms-windows are converted to tick counts using the runner's `tickIntervalMs`. |

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
* **News-shock simulation:** add a `newsShock` strategy alongside a
  `marketMaker` so the shock has a resting book to walk through.
  Defaults (`meanIntervalMs=60000`, `shockDurationMs=1500`,
  `fadeDurationMs=3000`) reproduce a roughly-once-per-minute directional
  burst suitable for stress-testing UMDF burst rates and consumer
  snapshot-recovery paths. Set `directionBias=1.0` (or `0.0`) to force a
  one-sided shock for repeatable scenarios.
* **Burst load (throttle exercise):** raise the synthetic trader's
  `tickIntervalMs` down to `10–20` and bump `noiseTaker.orderProbability`
  to `0.9` — the per-session throttle (`exch_throttle_rejected_total`)
  starts firing once the configured budget is exceeded.
* **Multi-firm flow:** copy `synthetic-trader.json` per firm, pointing
  each at a distinct `firm` code (matching the host's `enteringFirmCode`)
  and a distinct EntryPoint `port` if you split the host. The sample
  uses `firm: 1` (the legacy single-tenant fallback).

#### 4.0.1 FIXP handshake (production-shaped login)

When the host has `auth.requireFixpHandshake=true` the synthetic trader
must perform a full FIXP `Negotiate`+`Establish` before sending business
frames. Add a `fixp` block to the trader config:

```json
"fixp": {
  "sessionId": "100",
  "accessKey": "",
  "keepAliveIntervalMs": 5000,
  "cancelOnDisconnect": false,
  "retransmitOnGap": true
}
```

The `sessionId` MUST be a decimal `uint32` string (no leading zeros, > 0)
that matches a `sessions[].sessionId` declared in the host config. The
companion firm entry's `enteringFirmCode` MUST equal the trader's
`firm` field. With `auth.devMode=true` the `accessKey` is ignored; in
prod-shaped configs set it to the value registered for the session.

When the block is **omitted** the trader falls back to legacy "raw
business frames, no handshake" mode for back-compat with hosts running
`auth.requireFixpHandshake=false`.

The `EntryPointClient` then automatically:
* sends `Negotiate` + `Establish` on connect (throws on reject);
* embeds a monotonic `msgSeqNum` in every outbound business frame;
* emits a `Sequence` heartbeat after `keepAliveIntervalMs/2` of outbound
  silence and disconnects if the gateway is silent for >1.5×keepAlive;
* sends a `Terminate(FINISHED)` on graceful shutdown;
* on detected inbound gaps, fires `RetransmitRequest` when
  `retransmitOnGap=true`.

### 4.1 Deterministic replay (`tools/ScenarioReplay`)

For reproducing bug reports and pinning regression tests, drive the host
with a JSONL "script" instead of a randomised strategy:

```bash
# 1. Run the host with FIXP handshake disabled (single-tenant test mode):
dotnet run -c Release --project src/B3.Exchange.Host -- config/exchange-simulator.json

# 2. In another shell, replay a script and capture ER + multicast to disk:
dotnet run -c Release --project tools/ScenarioReplay -- \
    --host 127.0.0.1 --port 9876 \
    --script tools/ScenarioReplay/example.jsonl \
    --multicast 239.255.42.84:30184 \
    --out tape.jsonl

# 3. Inspect the tape:
jq 'select(.src=="er") | {execType, clOrdId, lastQty, lastPxMantissa}' tape.jsonl
```

Format reference and per-field semantics live in
[`tools/ScenarioReplay/README.md`](../tools/ScenarioReplay/README.md).
Tape files are diff-friendly JSONL — pipe through `jq 'del(.t)'` to mask
timestamps when comparing two runs.

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

### 5.2.1 Replay a trade-bust (trade reversal)

Goal: exercise the consumer's `TradeBust` handler end-to-end. The
simulator does not record per-trade history, so the operator supplies
the trade ID being busted plus the price/size echo fields.

```bash
# tradeId is in the path; securityId is required, the rest is optional.
curl -X POST 'http://127.0.0.1:8080/channel/84/trade-bust/4242?securityId=900000000001&priceMantissa=2505000&size=100'
```

Expected: a single `TradeBust_57` frame on the incremental channel,
stamped with the next available `RptSeq` (so the per-instrument
sequence stays dense). The matching engine itself is not mutated — the
bust is purely a market-data event the consumer's bust path must
process by removing the matching trade from any aggregated counters.

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

### 5.7 Multicast chaos injection (controlled drop / dup / reorder)

To exercise a consumer's gap-detection and snapshot-bootstrap paths without
bringing in `tc`/`netem` or a real flaky network, the host can wrap any
incremental UDP sink with a chaos decorator (issue #119). All probabilities
default to 0 — chaos is opt-in per channel.

Add a `chaos` block to one or more `channels[]` entries in
`config/exchange-simulator.json`:

```jsonc
{
  "channelNumber": 1,
  "incrementalGroup": "239.1.1.1",
  "incrementalPort": 30001,
  "instrumentsFile": "config/instruments.json",
  // ... existing fields ...
  "chaos": {
    "dropProbability": 0.01,        // 1% packet loss
    "duplicateProbability": 0.005,  // 0.5% duplicates
    "reorderProbability": 0.01,     // 1% reordered
    "reorderMaxLagPackets": 3,      // hold reordered packets up to 3 slots
    "seed": 42                      // deterministic across restarts
  }
}
```

Verify the decorator is active (host log line at startup):

```
chaos decorator active: drop=1.00 % dup=0.50 % reorder=1.00 % maxLag=3 seed=42
```

Watch the counters in `/metrics`:

```
umdf_chaos_dropped_total{channel="1"}     <n>
umdf_chaos_duplicated_total{channel="1"}  <n>
umdf_chaos_reordered_total{channel="1"}   <n>
```

Recovery scenario (companion `SbeB3UmdfConsumer`):

1. Start the host with `dropProbability: 0.01` on a channel of interest.
2. Drive load with a synthetic trader.
3. The consumer should observe gaps in `MsgSeqNum`, request a snapshot
   from the snapshot multicast group, and resume processing without losing
   book state.
4. Set `dropProbability: 0` and restart — the channel returns to lossless
   behavior; counters stop incrementing.

Notes:

- Chaos lives **only on the incremental sink** wired in `ExchangeHost`.
  Snapshot and InstrumentDef sinks are unaffected so the consumer always has
  a clean recovery channel to bootstrap from.
- The decorator is **not thread-safe** (matches the contract of the
  underlying sinks): it is invoked exclusively from the channel's
  `ChannelDispatcher` thread.
- Reorder semantics: a reordered packet is held back by a random lag in
  `[1, reorderMaxLagPackets]` Publish-calls, then released **after** a
  later packet so it appears later in the wire stream. Held packets are
  flushed on host shutdown.

### 5.8 Long-haul soak (≥1h) — `.github/workflows/soak.yml`

Detects slow leaks (RSS slope, FD growth, thread growth) that would not
surface in the few-minute smoke soak from issue #4. Issue #120.

Runs nightly at 03:00 UTC with `duration_minutes=60`; can also be
dispatched manually with a custom `duration_minutes` (1–360) and
`synth_clients` count.

Tooling:

- `tools/soak/sample.sh` — bash sampler that appends one CSV row every
  `SAMPLE_INTERVAL_SECONDS` (default 30), capturing process RSS, VmSize,
  thread count, FD count, and a few `/metrics` counters.
- `tools/soak/analyze.py` — computes RSS slope (MB/h, OLS) and asserts
  three thresholds (override via env):
  - `RSS_SLOPE_MAX_MB_PER_H` (default 50)
  - `FD_GROWTH_MAX` (default 100, last vs first)
  - `THREAD_GROWTH_MAX` (default 20, last vs first)
- Final summary is mirrored into `$GITHUB_STEP_SUMMARY`; the full sample
  CSV + host/trader logs are uploaded as `soak-<run_number>` artifact
  (30-day retention).

Manual run:

```bash
gh workflow run soak.yml -f duration_minutes=15 -f synth_clients=2
```

Local equivalent (uses the published Release DLL so the relative
`instruments` path resolves from the repo root):

```bash
dotnet build -c Release SbeB3Exchange.slnx
dotnet src/B3.Exchange.Host/bin/Release/net10.0/B3.Exchange.Host.dll \
  config/exchange-simulator.soak.json &
HOST_PID=$!
HOST_PID=$HOST_PID OUTPUT_CSV=/tmp/samples.csv DURATION_SECONDS=900 \
  METRICS_URL=http://127.0.0.1:8080/metrics \
  bash tools/soak/sample.sh
python3 tools/soak/analyze.py /tmp/samples.csv
```

**Note on the soak config:** `config/exchange-simulator.soak.json` sets
`auth.requireFixpHandshake = false` so the SyntheticTrader's plain-SBE
business protocol can drive sustained client load. Production hosts
must keep the flag at its default (`true`).

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

### 6.7 Distributed tracing (OpenTelemetry, issue #175)

The host emits W3C-trace-context spans on the `B3.Exchange`
`ActivitySource`. Spans:

| Span                | Where                                                | Parent             |
| ------------------- | ---------------------------------------------------- | ------------------ |
| `gateway.decode`    | `FixpSession.DispatchInboundAsync` (per inbound frame) | none (root)        |
| `dispatch.enqueue`  | `ChannelDispatcher.Enqueue*`                          | `gateway.decode`   |
| `engine.process`    | `ChannelDispatcher.ProcessOne` (dispatch thread)      | `dispatch.enqueue` |
| `outbound.emit`     | UMDF packet flush                                    | `engine.process`   |

Cross-thread propagation is explicit: the dispatch.enqueue
`ActivityContext` is stamped on each `WorkItem` so engine.process can
re-parent correctly when the dispatch loop picks the work up on its own
thread.

**Enable export** by setting the standard OTLP endpoint env var:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
export OTEL_SERVICE_NAME=b3-exchange   # default if unset
dotnet run --project src/B3.Exchange.Host -- config/exchange-simulator.json
```

When `OTEL_EXPORTER_OTLP_ENDPOINT` is unset the host logs
`tracing disabled: ...` at startup and the ActivitySource has no
listener — `StartActivity` returns `null` and the instrumented paths
take a zero-overhead branch.

All other `OTEL_*` env vars (protocol, headers, resource attributes,
sampler) are honored by the SDK directly.

---

## 7. State persistence ops

State persistence is **opt-in per channel** via the `persistence` block
in `config/exchange-simulator.json` (see *Persistence* in
[`EXCHANGE-SIMULATOR.md`](./EXCHANGE-SIMULATOR.md) for every
sub-field). When configured, each channel writes a snapshot of its
matching engine + `OrderRegistry` to disk after every command flush
(subject to throttle), and optionally appends each command to a
Write-Ahead Log between snapshots so recovery is nearly RPO-zero.

### 7.1 Boot-time recovery flow

1. Dispatcher cold-starts → `FileChannelStatePersister.TryLoad`
   sniffs each candidate file (`B3SS` magic ⇒ binary, else JSON),
   migrates the JSON tree through `SnapshotMigrationSet` if needed,
   and returns the newest valid snapshot. Corrupt newest slot ⇒
   transparently falls back to an older generation.
2. The dispatcher applies the snapshot, then if a WAL exists it
   replays every record whose sequence number is **greater than**
   `ChannelStateSnapshot.LastAppliedSeq` — so commands that landed
   between the last snapshot and an unclean shutdown are reapplied.
3. Orphaned `OrderOwnerSnapshot` entries (sessionId not in the
   firm/session registry) are handled per
   `persistence.orphanSessionPolicy` — `drop` (default) increments
   `exch_owner_orphans_dropped_total`; `reject` fails the channel
   closed.
4. The first outbound packet is stamped with `SequenceNumber+1`
   under the previous `SequenceVersion`. Consumers that miss the
   gap recover via the snapshot multicast feed.

### 7.2 Admin endpoints

All endpoints below dispatch onto the channel's inbound queue and
return `202 Accepted` immediately (`404` unknown channel, `503`
queue full). They live under `/admin` to keep them out of the
client-facing operator surface from §2.4.

| Endpoint | Method | What |
| --- | --- | --- |
| `/admin/channels/{ch}/snapshot` | GET | JSON summary of the most recently persisted snapshot for the channel (sequence numbers, owner count, file size, mtime). `404` if none exists. |
| `/admin/channels/{ch}/snapshot/force` | POST | Operator alias for `/channel/{ch}/snapshot-now` under the `/admin` namespace — enqueues a `WorkKind.OperatorPersistSnapshot` so the next dispatcher cycle captures and persists. |
| `/admin/channels/{ch}/snapshot/validate` | POST | Loads the most recent snapshot through the same structural validator the boot path uses (duplicate orderId, owners-reference-known-orders, stop-record sanity) WITHOUT restoring it. `200 ok` on success, `422` + reason on validation failure, `404` no snapshot, `503` no persister. |
| `/admin/channels/{ch}/snapshot/reset?force=true` | POST | **Destructive.** Deletes every on-disk snapshot artifact (all rolling generations + legacy file) AND truncates the WAL. The live in-memory engine state is NOT touched — restart the host to actually start the channel empty. The `?force=true` query string is required to acknowledge irreversibility. Use only when an operator has decided to abandon persisted state (e.g., after a forward-version rejection). |

### 7.3 Persistence metrics

Exposed under the existing `/metrics` endpoint (Prometheus 0.0.4
text format, `channel="{N}"` label on every series).

| Metric | Type | What |
| --- | --- | --- |
| `exch_snapshot_saves_total` | counter | Successful snapshot writes (each = one tmp+fsync+rename cycle). |
| `exch_snapshot_save_failures_total` | counter | Persist attempts that threw (disk full, permission, etc). The dispatcher swallows the exception and keeps running. |
| `exch_snapshot_skipped_by_throttle_total` | counter | Snapshots the throttle (`everyN` / `minIntervalMs`) suppressed. Operator force-saves bypass the throttle and never increment this. |
| `exch_snapshot_dropped_by_backpressure_total` | counter | `asyncWriter=true` only. Captures dropped because a newer one superseded them while the writer thread was still serializing the previous one. Last-write-wins is intentional. |
| `exch_snapshot_last_size_bytes` | gauge | Bytes written by the most recent successful save. Useful for sizing volumes and tracking format-flip impact (json↔binary). |
| `exch_snapshot_last_success_unixms` | gauge | Unix-ms timestamp of the most recent successful save. Alert if `(now - this)` exceeds your RPO budget. |
| `exch_snapshot_load_seconds` | gauge | Wall-clock time the boot-time `TryLoad` took (single observation per channel; 0 when no snapshot was loaded). |
| `exch_snapshot_write_seconds` | gauge | Wall-clock time of the most recent successful save (serialize + fsync + rename + dir-fsync). |
| `exch_snapshot_restore_failures_total` | counter | Snapshots that loaded but failed structural validation (bumps `RestoreOutcome=FailedValidation`). |
| `exch_snapshot_validation_failures_total` | counter | `/admin/.../snapshot/validate` calls that returned `422`. |
| `exch_wal_appends_total` | counter | Records appended to the WAL (one per state-mutating command). |
| `exch_wal_bytes_appended_total` | counter | Cumulative bytes appended (use as a rate gauge for IO planning). |
| `exch_wal_replays_total` | counter | Records replayed at boot (one per WAL entry past `LastAppliedSeq`). |
| `exch_wal_truncations_total` | counter | WAL truncations (post-snapshot or admin-reset triggered). |
| `exch_wal_record_corruption_total` | counter | WAL records dropped at replay because the per-record Crc32C did not match (bit-rot or external tampering). |
| `exch_wal_records_legacy_total` | counter | WAL records replayed in pre-#285 (no Crc32C suffix) format. Steady-state should be `0` once every channel has rolled past the upgrade. |
| `exch_wal_append_failures_total` | counter | WAL `Append` calls that threw (issue #286). Counted under both `continue` and `halt` policies; the canonical "WAL is failing" alert. |
| `exch_wal_halt_rejects_total` | counter | Producer-side `Enqueue*` rejections after the channel was WAL-halted (issue #286). Always `0` unless the channel runs with `persistence.wal.onAppendFailure=halt`. |
| `exch_wal_size_bytes` | gauge | Current on-disk size of the channel's WAL file (issue #291). Compare against `persistence.wal.maxBytes` to alert before the cap is reached. A flat-line at the cap under `onFull=halt` indicates a halted channel; a flat-line under `onFull=drop` indicates silent data loss. |
| `exch_wal_drops_on_full_total` | counter | WAL appends silently skipped because `persistence.wal.maxBytes` was reached and `onFull=drop` (issue #291). Distinct from `exch_wal_append_failures_total` so on-call can route a capacity-exhaustion alert separately from a generic IO-fault alert. |
| `exch_owner_orphans_dropped_total` | counter | `OrderOwnerSnapshot` entries whose sessionId was not in the registry at restore time, dropped per `orphanSessionPolicy=drop`. |

### 7.4 On-disk layout & WAL design

```
{dataDir}/
  channel-{N}.snapshot.0     # rolling slot 0 (newest by mtime, usually)
  channel-{N}.snapshot.1     # slot 1
  channel-{N}.snapshot.2     # slot 2 (default 3 generations, configurable)
  channel-{N}.wal            # write-ahead log, single file, append-only
  channel-{N}.wal.tmp        # ephemeral, only present mid-truncate
```

**No separate pointer / index / checkpoint file.** The "WAL cursor"
lives **inside** the snapshot as `ChannelStateSnapshot.LastAppliedSeq`.
This couples the snapshot and the truncation point into a single
atomic `rename(2)`, so the system cannot crash into a state where the
pointer advanced but the snapshot did not (or vice versa).

**Snapshot format:** `B3SS` magic ⇒ binary codec
(`BinaryChannelStateSnapshotCodec`); otherwise JSON. The persister
sniffs the first bytes on load, so both formats coexist while a
fleet rolls between `persistence.format=json` and `binary`.

**WAL format:** JSON-Lines (one `WalRecord` per line). Each line
since [#285](https://github.com/pedrosakuma/B3MatchingPlatform/issues/285)
is `<json>\t<8-hex-Crc32C>\n` — `Append` opens the file in append
mode, computes Crc32C (Castagnoli) over the JSON bytes, writes the
suffix, and (when `fsyncPerWrite=true`, the default) fsyncs.
`ReadAll` validates each record's CRC: a mismatch logs at warn,
bumps `exch_wal_record_corruption_total`, and **skips that record
while continuing replay** so a single bit-rot byte does not silently
truncate the entire log. Records written by pre-#285 hosts have no
suffix, are accepted unchecked (legacy path), and bump
`exch_wal_records_legacy_total`. A torn final write on the legacy
path (last line missing trailing `\n`) still stops replay — the
intended behaviour for unclean shutdown.

**WAL lifecycle:**

1. Each state-mutating command appends a record before execution.
2. On a successful **synchronous** snapshot save, the WAL is
   atomically truncated to empty (`tmp + rename + dir-fsync`).
3. With `asyncWriter=true`, truncation runs from the writer's
   `OnSaved` callback after the rename lands.
4. So at steady state the WAL contains only the commands of the
   current throttle window (typically seconds to a few minutes of
   traffic). It is not a long-term audit log.

**Replay at boot:**

1. Load newest valid snapshot (corrupt newest slot ⇒ fall back to
   an older generation transparently).
2. Open the WAL and read every record; **skip** any with
   `Seq <= snapshotLastAppliedSeq`, **apply** the rest in order.
3. Replay is sequential (no offset/skip-list index) — but because
   the WAL is bounded by the throttle window in normal operation,
   this is O(throttle-window) per channel, well under a second.
4. If the WAL has grown unboundedly (snapshot saves failing, see
   `exch_snapshot_save_failures_total`), replay stays sequential
   but takes proportionally longer. That is a saves-broken alert,
   not a replay-perf concern.

**Append-failure policy** (issue #286): when
`IChannelWriteAheadLog.Append` throws (disk full, EIO, permission
flip), the channel honours `persistence.wal.onAppendFailure`:

| Value | Behavior |
| --- | --- |
| `continue` (default) | Log + bump `exch_wal_append_failures_total`, then run the command. The live consumer view stays consistent, but the command is **not durable** — a host crash before the next snapshot will silently drop it on replay. |
| `halt` | Log + bump `exch_wal_append_failures_total`, **refuse** the command (no engine mutation, no UMDF emission, no ExecutionReport), flip the channel's WAL-halt flag. The host's `wal-halt` readiness probe goes NOT_READY so load balancers drain new connections; subsequent `Enqueue*` calls are short-circuited and counted by `exch_wal_halt_rejects_total`. The halt is sticky — the operator must restart the host after fixing the underlying storage fault. |

**Size cap & on-full policy** (issue
[#291](https://github.com/pedrosakuma/B3MatchingPlatform/issues/291)):
without a cap the WAL grows unbounded between snapshots. If
snapshots stop succeeding (disk-full elsewhere, persister bug,
permission flip) the WAL alone will eventually fill the
filesystem and bring down unrelated subsystems. Set
`persistence.wal.maxBytes` to the operational ceiling for a
single channel's WAL and pick `persistence.wal.onFull`:

| Value | Behavior |
| --- | --- |
| `halt` (default) | The next `Append` that would push past `maxBytes` throws `WalSizeCapExceededException`. The dispatcher catches this exception **specifically** and marks the channel WAL-halted **regardless** of `onAppendFailure` — silent degradation is disallowed once the operator has explicitly opted into a hard cap. The halt is sticky and clears only on host restart (after raising the cap or resolving the snapshot fault). Same readiness-probe + producer-side reject behaviour as `onAppendFailure=halt`. |
| `drop` | Silently skip the WAL write, log at warning, bump `exch_wal_drops_on_full_total`, let the command run. Same trade-off as `onAppendFailure=continue` — the live consumer view stays consistent but the command is **not durable**. Distinct counter so on-call can route a "WAL is full" page differently from a "WAL is throwing" page. |

The cap is checked **before** the append stream is opened, so a
breach never adds even one byte to the file. A successful
snapshot persist truncates the WAL and resets `exch_wal_size_bytes`
to `0`, restoring the full budget.

**Idempotency note:** if the newest snapshot is corrupt and the
persister falls back to an older slot, replay starts from that
slot's `LastAppliedSeq` — meaning records covered by *intermediate*
snapshots (now lost with the corrupt slot) get re-applied. The
engine is deterministic over the recorded `WalRecord` stream, so
this produces the same final state, but operators monitoring
`exch_wal_replays_total` will see a larger-than-usual count after
such a fallback. The invariant — *"snapshot at K + WAL tail (K, N]
≡ apply [1..N] from clean engine"* — is enforced at build time by
`WalReplayIdempotencyTests` (issue
[#287](https://github.com/pedrosakuma/B3MatchingPlatform/issues/287)),
which exercises multiple seeded command streams across several
split points and asserts byte-for-byte snapshot equality.

### 7.5 Session disconnect & reattach — durability of private replies

The persistence story above covers the **public** state (order
book, snapshot multicast). Private replies — `ExecutionReport`s
delivered back to the originating FIXP session over TCP — have a
**parallel, independent** durability story that operators must
understand.

**Design philosophy:** every recovery scenario the FIXP / EntryPoint
protocol exposes a remediation for (`RetransmitRequest`,
`Establish` with `NextSeqNo`, suspend/reattach, Cancel-on-Disconnect)
is meant to be served end-to-end by the simulator. Operational
reconciliation is a fallback for protocol-out-of-spec situations,
not a designed-in recovery path.

**Where private replies live:**

| Layer | Storage | Lifetime |
| --- | --- | --- |
| Live socket write path | OS socket buffer | Until kernel flushes / connection drops |
| `RetransmitBuffer` (per FIXP session) | **In-memory ring**, capacity = `session.outboundRetransmitCapacity` | Until the FIXP session is reaped (`SuspendedTimeoutMs`, default **5 min**) or evicted by a newer record |
| WAL / snapshot | Persisted, but stores **commands**, not the resulting `ExecutionReport`s | Bounded by snapshot cadence |

**The good news (issue [#217](https://github.com/pedrosakuma/B3MatchingPlatform/issues/217)):**
passive `ExecutionReport`s emitted while the owning FIXP session is
in `FixpState.Suspended` (transport disconnected but session still
alive) are appended to that session's `RetransmitBuffer` rather
than dropped. A subsequent `Establish` + `RetransmitRequest`
delivers them with `PossResend = 1` as a normal recovery cycle.

**The known gaps (none of these are silent — alert on them):**

| Scenario | Outcome | Mitigation today | Eventual fix |
| --- | --- | --- | --- |
| Disconnect window ≤ `SuspendedTimeoutMs`, fills fit in ring | All ERs replayed on `Establish` ✅ | — | — |
| Disconnect window ≤ `SuspendedTimeoutMs`, **fills exceed ring capacity** | Oldest ERs evicted; `Establish` with too-low `NextSeqNo` ⇒ `Establishment Reject` | Size `outboundRetransmitCapacity` for worst-case fill rate × `SuspendedTimeoutMs`; alert on `exch_fixp_retransmit_buffer_utilization` ([#288](https://github.com/pedrosakuma/B3MatchingPlatform/issues/288)) | Persisted `RetransmitBuffer` ([#289](https://github.com/pedrosakuma/B3MatchingPlatform/issues/289)) |
| **Host crash** while session is `Suspended` | Ring is in-memory; if `tcp.retransmitPersistenceDir` is unset, all buffered ERs are lost. With persistence enabled, the per-session ring is mirrored to `{retransmitPersistenceDir}/sessions/session-{sessionId:x8}.ring` and rehydrated on the next boot ([#289](https://github.com/pedrosakuma/B3MatchingPlatform/issues/289)). | Set `tcp.retransmitPersistenceDir` on durable storage; alerts unchanged | Covered (issue [#289](https://github.com/pedrosakuma/B3MatchingPlatform/issues/289)) |
| Disconnect window > `SuspendedTimeoutMs` | `TryReapIfSuspended` removes the session from `SessionRegistry`; ring goes to GC | Size `SuspendedTimeoutMs` to the worst-case operational disconnect for the firm; alert on `exch_fixp_sessions_reaped_total` ([#288](https://github.com/pedrosakuma/B3MatchingPlatform/issues/288)) | Tunable per-firm; protocol-level `OrderMassStatus` on next reconnect (Tier 3) |
| Cancel-on-Disconnect (CoD) armed, mode 1/3, window expired | `MassCancel` fires ⇒ no passive fills can happen ⇒ no problem | Configure CoD per firm policy | — |

**Operational implication:** the **dimensioning tuple** that
determines whether a disconnect causes data loss is
`(outboundRetransmitCapacity, SuspendedTimeoutMs, expected fill
rate while disconnected)`. Document the chosen values per firm
and alert on the utilization metrics rather than discovering the
limit when a real disconnect happens.

**What an operator should NOT do:** rely on out-of-band
reconciliation (back-office, manual `OrderStatusRequest` sweep) as
the primary recovery for these scenarios. The protocol offers a
proper remediation (`RetransmitRequest`); the simulator's job is to
make that remediation succeed.

#### 7.5.1 Dimensioning observability — issue [#288](https://github.com/pedrosakuma/B3MatchingPlatform/issues/288)

Four metrics make the `(outboundRetransmitCapacity,
SuspendedTimeoutMs, fill rate)` tuple a measurable property of the
running system instead of a guess:

| Metric | Type | Cardinality | What to alert on |
| --- | --- | --- | --- |
| `exch_fixp_retransmit_buffer_evictions_total` | counter | process | **Any non-zero rate** ⇒ at least one session lost replayable history. Bump `outboundRetransmitCapacity` or shorten `SuspendedTimeoutMs`. |
| `exch_fixp_passive_er_buffered_total` | counter | process | Informational — total `ExecutionReport`s buffered while the owning session was `Suspended`. Sustained growth without matching `exch_session_rebound_total` ⇒ disconnects becoming reaps. |
| `exch_fixp_retransmit_buffer_utilization` | gauge (0..1) | **per-session** (opt-in) | `>0.8` for any session ⇒ undersized ring for that firm's burst pattern. |
| `exch_session_reaped_total` (existing, [#70](https://github.com/pedrosakuma/B3MatchingPlatform/issues/70); also exported as the alias `exch_fixp_sessions_reaped_total` for issue #288) | counter | process | Non-zero ⇒ at least one `Suspended` session crossed `SuspendedTimeoutMs` and was dropped; downstream firm needs `OrderMassStatus` on reconnect. |

The per-session utilization gauge is **off by default** to keep
scrape cardinality bounded on deployments that cycle through many
short-lived FIXP sessions. Opt in via:

```jsonc
{
  "metrics": { "fixpSessionLabelsEnabled": true }
}
```

The aggregate counters above are always emitted regardless of
this flag, so the eviction-rate alert remains low-cost in every
deployment.

### 7.6 Disaster recovery — backup / restore

The persisted state for a channel is everything matching
`{dataDir}/channel-{N}.snapshot.*` plus (if WAL is enabled)
`{dataDir}/channel-{N}.wal`. See §7.4 for the layout — files are
self-describing, there is no out-of-band index to back up.

> **Single-writer fence (issue [#290](https://github.com/pedrosakuma/B3MatchingPlatform/issues/290)).**
> On startup the host acquires an exclusive lock on
> `{dataDir}/.lock` for every distinct configured `dataDir`. A
> second host process pointed at the same directory refuses to
> start with `DataDirLockedException` (the message carries the
> holder's `pid=… startedUtc=… host=…` line written to the lock
> file). The lock is released on graceful shutdown and on process
> exit (the OS drops the file handle), so an ungraceful crash does
> not strand the lock. **Operational implication:** if a stuck pod
> holds the lock after a forced reschedule, the new pod will fail
> fast — `kubectl logs` will surface the holder PID; resolve by
> killing the orphaned process or removing the stale `.lock` file
> only after confirming no live writer remains. Never run two
> hosts against the same `dataDir`.

**Backup recipe (rsync-friendly hot copy):**

```bash
# Hot copy of all persisted state. Files are written via
# tmp+fsync+rename so a concurrent rsync sees a consistent point-in-time
# generation; worst case the destination's newest slot is one revision
# behind the source.
rsync -a --delete /var/lib/b3matching/ /backup/b3matching/$(date +%F)/
```

**Restore recipe:**

1. Stop the host.
2. Replace `dataDir` with the backup contents (preserve filename
   layout — the persister discovers slots by the `channel-{N}.snapshot.{slot}`
   pattern).
3. Start the host. Boot-time recovery (§7.1) takes care of the rest.
4. Confirm with `curl -sS http://127.0.0.1:8080/admin/channels/84/snapshot`
   — the returned `sequenceNumber` should match what the backup captured.

### 7.7 Switching format (json ↔ binary)

The persister auto-detects the format on **load**, so flipping
direction is a config change + restart:

```bash
# 1. Edit config/exchange-simulator.json:
#      "persistence": { ..., "format": "binary" }
# 2. Restart the host. Existing JSON slots remain loadable.
# 3. The next save writes binary; old JSON slots age out as the
#    rolling generations rotate (3 saves by default).
```

Roll-back works identically — set `format: "json"` and restart.
**Caveat:** if you bumped `ChannelStateSnapshot.CurrentVersion`
between the host versions, the older host will reject the snapshot
with a forward-version error. Switch the format *before* the schema
bump, or use `/admin/channels/{ch}/snapshot/reset?force=true` to
abandon the on-disk state.

### 7.8 Post-trade audit log (issue [#329](https://github.com/pedrosakuma/B3MatchingPlatform/issues/329))

Opt-in via the per-channel `postTradeAudit` config block (issue
[#352](https://github.com/pedrosakuma/B3MatchingPlatform/issues/352)):

```jsonc
"channels": [{
  "channelNumber": 1,
  // ... transport, instruments, persistence ...
  "postTradeAudit": {
    "enabled": true,
    "dataDir": "/var/lib/b3matching/audit",
    "retentionDays": 1825   // 5 years; 0 disables auto-prune
  }
}]
```

When the block is absent or `enabled=false`, the dispatcher uses
`NullPostTradeSink` and no audit files are written (preserving the
pre-#352 behaviour).

The post-trade audit log is the legally-authoritative per-trade record
the simulator emits alongside the wire-published `Trade_53` /
`ER_Trade` frames. **It is a separate durability domain from the WAL:**
the WAL exists to recover engine state on restart and is deliberately
short-lived (truncated on every successful snapshot); the audit log is
append-only, daily-rolled by UTC business date, and intended to be
retained for the compliance horizon (typically 5 years for B3 fills).

**On-disk layout** (one set per channel, under
`{auditDir}/{channel}/`):

| File | Purpose |
| --- | --- |
| `fills-YYYY-MM-DD.log` | CRC32-protected, length-prefixed audit records; the schema is versioned in the file header. |
| `fills-YYYY-MM-DD.idx` | Sparse firm index (PR-3, #346): one block entry per N records, keyed by both `buyFirm` and `sellFirm` so per-firm filtered scans skip irrelevant blocks. |
| `audit-watermark.bin` | Per-channel sidecar (20 B): magic `B3PW` + schema v1 + `lastDurableCommandSeq` + CRC32. Persisted atomically (`.tmp` + fsync + rename) on every successful `Checkpoint`. |

**Durability watermark (PRs #347 / #350).** `ChannelDispatcher`
publishes a `commandSeq` boundary to the audit sink after every
flushed command. `Checkpoint` fsyncs the `.log` and `.idx`, then
writes the sidecar atomically, then advances the in-memory
`DurableThroughCommandSeq`. The WAL truncation gate refuses to drop
any record whose `commandSeq` exceeds this watermark, so a crash
between `OnTrade` and a successful `Checkpoint` is recovered by WAL
replay rather than silently dropped. On boot the writer seeds the
watermark from the sidecar; the dispatcher captures it once at the
start of replay and skips re-emitting any trade whose owning command
is at or below it — preventing duplicate audit records when the WAL
contains commands that were already fsync'd to the audit log
pre-crash. A missing or corrupt sidecar collapses to "watermark
unknown = 0", which is conservative: every replayed trade is
re-emitted (idempotency is enforced by the file-pair pattern; the
worst-case overshoot is bounded by the `[snapshot, crash]` window).

**Retention (PR-6).** `FileAuditLogWriter.PruneOldDays(todayUtc,
retentionDays)` deletes `fills-YYYY-MM-DD.{log,idx}` pairs whose
date is *strictly before* `todayUtc - retentionDays`. The currently
open day is never pruned, the per-channel watermark sidecar is never
pruned, and unrelated / malformed filenames are ignored. The method
is safe to call from any thread (it shares the `_checkpointLock`
that serializes `Checkpoint`/`Dispose`). When the host is
configured with `postTradeAudit.retentionDays > 0`, a per-channel
24h `Timer` (started ~1 minute after boot, then every 24h) invokes
`PruneOldDays` automatically and logs the deletion count at
`Information` when > 0. Set `retentionDays=0` to disable the timer
and manage retention from an external operator job.

**Recovery procedure (post-crash).**

1. Start the host. The dispatcher loads the latest snapshot, then
   replays the WAL through the matching engine — but emits *only*
   the audit log (UMDF and ER outbound are stubbed during replay).
2. For each replayed command, the audit gate compares the command's
   `commandSeq` against `_bootAuditDurableSeq`:
   - `commandSeq <= watermark` → trade was already on disk pre-crash,
     the duplicate emission is suppressed and the
     `exch_audit_replay_skipped_total` counter is bumped.
   - `commandSeq > watermark` → the audit log has a hole; the gate
     lets the trade through so the on-disk log is repaired.
3. After replay finishes, the dispatcher transitions to live mode and
   the WAL truncation gate proceeds normally (truncating up to the
   newly-advanced audit watermark on the next snapshot save).
4. Operator confirmation: `exch_audit_replay_skipped_total` should
   be > 0 on any boot that found a non-empty WAL; a 0 reading
   combined with replayed records means either no trades happened in
   the recovered window OR the sidecar was missing/torn (also
   surfaced by `exch_wal_replays_total > 0` with the sidecar
   absent on disk). The second case is safe — duplicates within the
   replay window are tolerated — but it is worth investigating the
   underlying fsync / I/O failure that destroyed the sidecar.

**Audit log vs WAL — quick comparison:**

| Property | WAL (`channel-N.wal`) | Audit log (`fills-YYYY-MM-DD.log`) |
| --- | --- | --- |
| Lifetime | Transient — truncated on every successful snapshot, gated by the audit watermark. | Long-lived — kept until `PruneOldDays` deletes the day. |
| Recovery role | Rebuilds engine state alongside the snapshot. | Reconstructs the trade history; not used to rebuild the engine. |
| Schema versioning | Bumped via `SnapshotMigrations` chain. | Forward-compatible file header version field. |
| Failure mode | A halted WAL stops the channel (`exch_wal_halt_rejects_total`). | A failed write makes the writer sticky-faulted; the watermark refuses to advance and the WAL stops truncating, surfacing the issue as backpressure rather than silent loss. |
| Retention knob | `snapshotThrottle.everyN` + WAL truncation. | `PruneOldDays(today, retentionDays)`. |

**Benchmarks.** Hot-path overhead is tracked by
`PostTradeAuditBenchmarks` in `bench/B3.Exchange.Bench/`:

```bash
dotnet run -c Release --project bench/B3.Exchange.Bench -- \
  --filter '*PostTradeAudit*'
```

Two scenarios: steady-state append (no fsync per record — the
default operating mode) and pessimistic per-call `Checkpoint` (an
upper bound on per-trade audit overhead). Compare numbers across PRs
that touch the dispatcher hot path or the writer to catch
regressions before they land.

### 7.9 EOD fills export — CSV drop (issue [#330](https://github.com/pedrosakuma/B3MatchingPlatform/issues/330))

Projects the per-trade audit log (§7.8) for a given business date
into a CSV "drop" file that downstream recon tools poll. This is
the **post-trade boundary** described by
[ADR 0001](./adr/0001-post-trade-boundary-and-eod-file-export.md) —
file-based, not REST. The `/admin/post-trade/eod-export` trigger is
operator-only and lives on the admin surface for ergonomics; the
artifact it produces is on disk.

**Opt-in per channel** via the `postTradeAudit.eodDropDir` field
(added by issue #330 PR-2). The channel must also have
`postTradeAudit.enabled=true` (§7.8) — without an audit log there
is nothing to project:

```jsonc
"channels": [{
  "channelNumber": 1,
  // ... transport, instruments, persistence ...
  "postTradeAudit": {
    "enabled": true,
    "dataDir": "/var/lib/b3matching/audit",
    "eodDropDir": "/var/lib/b3matching/drop",
    "retentionDays": 1825
  }
}]
```

If `eodDropDir` is empty, the channel has no export configured and
the trigger returns `404` for that channel (other channels still
run).

**On-disk layout** (one set per channel per business date):

```
{eodDropDir}/{channel}/{YYYY-MM-DD}/
  fills.csv        # data
  fills.csv.done   # sidecar with { rowCount, sha256, generatedAt }
```

The CSV columns are frozen at the ADR 0001 v1 shape:

```
tradeId, ts, symbol, aggressorSide, qty, price, buyClOrdId, sellClOrdId, buyFirm, sellFirm
```

`ts` is ISO-8601 UTC with microsecond precision. `aggressorSide`
is taker-relative (`B`/`S`, matching UMDF `Trade_53` semantics).
Internal crosses (same firm on both sides) emit one row with
`buyFirm == sellFirm`. `symbol` is resolved from each channel's
instrument map; trades on unknown securityIds fall back to the
numeric `securityId` as the symbol so the row is never lost.

**Atomic publish contract** (every consumer must rely on these
invariants):

1. The exporter stages `fills.csv` and `fills.csv.done` to
   `.tmp-<pid>-<nonce>` paths in the same directory.
2. The previous `.done` (if any) is deleted, then the directory
   is fsync'd — this is what guarantees a polling consumer never
   sees a stale `.done` next to a fresh CSV.
3. The CSV is renamed into place and fsync'd.
4. The `.done` sidecar is renamed last and fsync'd.

**Consumers MUST wait for `fills.csv.done` before reading
`fills.csv`.** A `.csv` without a matching `.done` is either
still being written or a leftover from a crashed export run.

**Idempotency.** Rerunning the export for the same `(channel,
date)` produces a byte-identical CSV (same input → deterministic
output). Only the `generatedAt` field inside `.done` differs.
This is the intended recovery / re-issue path for operators.

**Operator trigger — `POST /admin/post-trade/eod-export`.**
Synchronous: blocks until the staging + fsync + rename cycle
completes (typical CSV is small — a few MB at most for a busy
channel — so wall-clock is sub-second on local SSD, low single
digits on networked drops).

```bash
# Re-issue yesterday's drop for channel 1.
curl -sS -X POST \
  "http://127.0.0.1:8080/admin/post-trade/eod-export?channel=1&date=2026-05-18"
# {"csvPath":"/var/lib/b3matching/drop/1/2026-05-18/fills.csv","rowCount":12345,"sha256":"…"}
```

| Status | Meaning |
| --- | --- |
| `200 OK` | Export succeeded. Body is a JSON object with `csvPath`, `rowCount`, `sha256` (matches the value in `.done`). |
| `400 Bad Request` | Missing or malformed `channel` / `date` query parameter. `date` must be `YYYY-MM-DD` (UTC business date). |
| `404 Not Found` | The channel has no EOD export wired (either `postTradeAudit.enabled=false`, `postTradeAudit.dataDir` empty, or `postTradeAudit.eodDropDir` empty), or no `fills-YYYY-MM-DD.log` exists for that channel and date. The response body distinguishes the cases. |
| `409 Conflict` | Another export for the same `(channel, date)` is already in progress. The in-flight guard is per-tuple, so different `(channel, date)` pairs can run in parallel. |
| `500 Internal Server Error` | Unhandled exception during projection (disk full, audit-log CRC failure, permission flip). The body carries the exception type and message; no partial file is published. |
| `503 Service Unavailable` | Host has not finished `StartAsync` (the trigger is not yet wired). |

**Scheduled trigger — `POST /admin/daily-reset` and
`DailyResetScheduler`.** Both go through the same code path (see
§2.5): terminate sessions → drain dispatcher inbound queues →
chain the per-channel EOD export for **yesterday UTC**. Per-channel
failures (missing audit log for a quiet day → warning; race with an
operator manual rerun → warning; other exceptions → error) are
isolated so one channel never blocks siblings. Yesterday-UTC is
chosen because the currently-open day's audit file is still being
written by the dispatcher — operators always export the last
**sealed** day at rollover.

**Auth model.** Same as every other `/admin/*` endpoint: there is
no application-level auth. Operators must network-gate the HTTP
listener (bind to loopback, route through a reverse proxy, restrict
by k8s NetworkPolicy, etc.). See §1.1 / §2 for the listener bind
address.

**Failure modes a consumer can observe:**

| Symptom | Cause | Remediation |
| --- | --- | --- |
| `.csv` exists, no `.done` | Export run crashed mid-publish, or the host was killed between steps 3 and 4 above. | The consumer must ignore the file. Rerun the trigger; the rename is idempotent and the `.done` will appear once the rerun completes. |
| `.done` lists a `sha256` that doesn't match `sha256sum fills.csv` | The CSV was modified after publish (manual edit, restore from backup). | Re-run the export to overwrite both files from the authoritative audit log. |
| Trigger returns `404` "audit log not found" but the operator expected trades | Either the date is wrong (UTC vs local), or `postTradeAudit.enabled` was off on that day, or `dataDir` was different. | Check `{dataDir}/{channel}/fills-{date}.log` directly; verify the config in effect at the relevant historical moment. |
| Trigger returns `409` and never clears | A scheduler tick is mid-flight (typically sub-second). If it persists for minutes, the dispatcher is stuck. | Check host logs for `daily-reset (...): drain phase=grace-expired` and `eod-export ... failed` lines; investigate the audit writer's sticky-fault state (§7.8). |

**Failure-mode invariant:** a consumer that respects the
`.done` gate never observes a partial, torn, or stale CSV. The
exporter holds no state — every run reads the audit log fresh —
so safe-to-rerun on any failure.

### 7.10 Common persistence headaches

* **"Channel boots empty after restart."** Check
  `exch_snapshot_load_seconds` — `0` means `TryLoad` returned null.
  Most likely the `dataDir` is wrong (typo, missing volume mount,
  permissions). Tail the host log for `failed to load snapshot at
  ...` warnings.
* **"`exch_snapshot_restore_failures_total` keeps incrementing."**
  A persisted snapshot loaded but failed the structural validator.
  Run `/admin/channels/{ch}/snapshot/validate` for the reason.
  Usually a hand-edited file or a developer-only test artifact.
  Recover with `/admin/channels/{ch}/snapshot/reset?force=true`
  + restart.
* **"Disk usage on `dataDir` grows unboundedly."** Check
  `generations` (rolling slots cap snapshot growth). The WAL is
  truncated on every successful snapshot — if it grows, snapshots
  are failing (see `exch_snapshot_save_failures_total`) or
  throttled too aggressively (`everyN` too high relative to command
  rate).
* **"`asyncWriter` shows backpressure drops under load."** That is
  by design — the latest capture supersedes older queued ones so
  on a crash you lose at most the captures between the latest
  durable write and the crash. Lower `everyN` / `minIntervalMs` if
  the drop rate is unacceptable, or switch `asyncWriter=false` to
  enforce zero-RPO at the cost of a slower dispatch loop.

### 7.11 Late corrections — operator bust (issue [#369](https://github.com/pedrosakuma/B3MatchingPlatform/issues/369))

Per-trade reversal flow for post-trade life-cycle events
([ADR 0008](./adr/0008-late-corrections-and-bust-propagation.md)).
Builds on §7.8 (audit log) and §7.9 (EOD export); requires
`postTradeAudit.enabled=true` AND `postTradeAudit.eodDropDir` set on
the channel.

**One HTTP surface, two routing paths.** Operators always call the
same endpoint; the dispatcher decides between `pre-EOD` (bust folded
out of `fills.csv`) and `post-EOD` (bust appended to
`amendments.csv`) based on whether the day's `fills.csv.done`
sidecar already exists at the moment the bust is processed. The
decision is made under the per-channel routing lock that
`/admin/post-trade/eod-export` holds across its run, so no race can
land a bust in the wrong file.

```bash
curl -sS -X POST 'http://127.0.0.1:8080/admin/post-trade/bust'\
'?channel=84'\
'&tradeId=4242'\
'&tradeDate=2026-05-18'\
'&correlationId=900001'\
'&busterFirm=99'\
'&reason=5'\
'&securityId=900000000001'
```

| Query | Required | Meaning |
| --- | --- | --- |
| `channel` | yes | UMDF channel number (0..255). |
| `tradeId` | yes | The engine `TradeId` of the trade to cancel (1..uint32). |
| `tradeDate` | yes | `YYYY-MM-DD` of the original trade's business date. Range-checked against LocalMktDate (1970-01-01..2149-06-06). |
| `correlationId` | yes | Operator-supplied dedup key (`> 0`). Replaying with the same `(tradeId, tradeDate, correlationId)` is an idempotent 200. A different correlationId for the same `tradeId` is a 409. |
| `reason` | no | `ushort` reason code echoed on `TradeBust_57` and in the audit record (default `0`). |
| `securityId` | no | Echo check — server rejects the bust with 422 if the matched fill's security id differs. |
| `busterFirm` | no | `uint` operator firm id stamped in the audit record (default `0`). |

**Status code matrix** (mirrors ADR §2.3):

| Code | Meaning |
| --- | --- |
| `200 {"status":"busted"}` | Accepted; `TradeBust_57` emitted on the channel, audit record persisted, routing decided per §7.11.1 below. |
| `200 {"status":"idempotent-replay"}` + `X-Idempotent: true` | Same `(tradeId, correlationId, tradeDate)` already accepted. No new audit write, no UMDF emit; for post-EOD targets the publisher re-runs to repair any previously-failed `amendments.csv` publish. |
| `400` | Missing or invalid query param. |
| `404` | Unknown channel, or channel without `postTradeAudit.eodDropDir`. |
| `409` | `tradeId` already busted with a *different* `correlationId` (body carries the existing one). |
| `410` | `fills-{tradeDate}.log` missing for the channel (audit was off that day, or wrong date). |
| `422` `unknown-trade-id` | `tradeId` not found in the day's audit log. |
| `422` `security-id-mismatch` | `securityId` echo doesn't match the matched fill's. |
| `503` | Inbound queue full or bust-v2 not wired. |
| `504` | Dispatcher took longer than the phase-change timeout to complete validation. |

#### 7.11.1 Pre-EOD vs post-EOD routing

The dispatcher's routing decision happens under the same lock the
EOD exporter holds, so it can never observe a partial publish:

```
                       ┌──────────────────────────────────────┐
                       │ POST /admin/post-trade/bust          │
                       └──────────────┬───────────────────────┘
                                      │
                            BustValidator.Validate
                                      │
                  ┌───────────────────┼───────────────────┐
                  │ Accept            │ Idempotent        │ 4xx
                  ▼                   ▼                   ▼
        lock(PostTradeRoutingLock):    no-op +           write
          if fills.csv.done exists    optional           reject-attempt
            → post-EOD                amendments         to audit log
          else                         republish         (no UMDF emit)
            → pre-EOD
          append BustRecord
          dedup.Add(...)
          emit TradeBust_57
        ───────────────────────────
        if post-EOD: AmendmentsPublisher.Publish(...)
```

| Path | `fills.csv.done` at decision time | Bust audit file | Visible artifact for consumers |
| --- | --- | --- | --- |
| Pre-EOD | absent | `fills-{tradeDate}.log` (same day as the original trade) | None until EOD runs; then `fills.csv` is published **without** the bust target — the row is folded out. |
| Post-EOD | present | `fills-{UTC-today}.log` | `amendments.csv` next to the day's `fills.csv` — one row per accepted post-EOD bust whose target is in `fills.csv`. |

The exporter (§7.9) at EOD time walks `fills-{tradeDate}.log`,
builds the cancelled-trade set from every `BustRecord`, and writes
`fills.csv` with those trades omitted. Reject-attempt records
(`code=1/2/3`) are skipped entirely.

#### 7.11.2 `amendments.csv` layout

Sibling of `fills.csv` on the same drop directory; **only emitted
when at least one post-EOD bust references a trade present in
`fills.csv`**. A pre-EOD bust never produces an amendment row — the
consumer never saw the original fill, so there is nothing to amend.

```
{eodDropDir}/{channel}/{YYYY-MM-DD}/
  fills.csv         # §7.9 — base trade tape (folded)
  fills.csv.done    # §7.9
  amendments.csv      # ADR 0008 §4 — late corrections
  amendments.csv.done # { rowCount, sha256, generatedAt }
```

CSV schema:

```
cancelTradeId,bustTransactTime,reasonCode,correlationId,sha256OfOriginalFillRow
```

* `bustTransactTime` — ISO-8601 UTC microsecond.
* `sha256OfOriginalFillRow` — lower-case hex SHA-256 over the bytes
  of the corresponding row in `fills.csv`, from the first byte of
  the row through and including the row-terminating `\n`. Consumers
  recompute the digest locally to confirm they are amending the
  same bytes the producer signed.
* Rows are sorted by `bustTransactTime` ascending so consumers can
  apply them in a deterministic order.

**Atomic publish** mirrors §7.9 step-for-step
(stage → fsync → delete old `.done` → rename CSV → fsync dir
→ rename `.done` → fsync dir). Each call to the publisher
**regenerates the file in full from the audit log** — there is no
append path — so a torn write can never leave a partial row visible,
and rerunning the post-EOD bust (idempotent replay) re-runs the
publisher and converges on the same bytes (except for `generatedAt`).

#### 7.11.3 Recovery — failed amendments publish

If `AmendmentsPublisher.Publish` throws after the bust is already
written to the audit log (disk full, drop dir permission flip), the
HTTP response is still `200 busted` — the bust *is* durable. The
dispatcher logs the failure as `amendments.csv publish failed ...`
but the consumer-visible artifact is missing.

Operator remediation:

1. Resolve the underlying I/O fault (free disk, fix perms).
2. **Replay the same `/admin/post-trade/bust` with the same
   correlation id.** The validator returns `idempotent-replay` and
   the dispatcher re-invokes `AmendmentsPublisher.Publish`, which
   regenerates the file from the audit log and lands the missing
   row. No engine state mutates on the retry.

A host restart will *not* fix the missing amendments file on its
own — `AmendmentsPublisher.Publish` only runs as part of bust
processing, never at boot.

#### 7.11.4 Worked example

```bash
# Channel 84, audit + drop wired per §7.9.
# Three trades happened today; trade #2 was a fat-finger.

# Pre-EOD: operator busts trade #2 before EOD runs.
curl -sS -X POST 'http://127.0.0.1:8080/admin/post-trade/bust?channel=84&tradeId=2&tradeDate=2026-05-19&correlationId=11&busterFirm=99'
# 200 {"status":"busted","channel":84,"tradeId":2,"correlationId":11}

# EOD export.
curl -sS -X POST 'http://127.0.0.1:8080/admin/post-trade/eod-export?channel=84&date=2026-05-19'
# 200 {"rowCount":2,"csvPath":"/drop/84/2026-05-19/fills.csv", ...}
# fills.csv = header + trade #1 + trade #3 (trade #2 folded out)
# No amendments.csv yet.

# Post-EOD: a settlement issue prompts the operator to bust trade #3
# the next day. fills.csv.done already exists.
curl -sS -X POST 'http://127.0.0.1:8080/admin/post-trade/bust?channel=84&tradeId=3&tradeDate=2026-05-19&correlationId=22&busterFirm=99'
# 200 {"status":"busted","channel":84,"tradeId":3,"correlationId":22}
# amendments.csv now present, listing trade #3 with sha256 of its row
# in fills.csv. The published fills.csv is NOT rewritten.
```

**Failure modes a consumer can observe:**

| Symptom | Cause | Remediation |
| --- | --- | --- |
| `.csv` exists, no `.done` | Publish crashed between rename-csv and rename-done. | Consumer must ignore. Operator replay (same correlationId) re-runs the publisher and converges. |
| `amendments.csv` row's sha256 doesn't match `fills.csv` row | `fills.csv` was modified (manual edit, restore from backup) after the amendment was emitted. | Re-export `fills.csv` (§7.9) and re-run the most recent bust(s) to regenerate `amendments.csv` against the new bytes. |
| Pre-EOD bust returned `200 busted` but `fills.csv` still has the trade | Bust was accepted AFTER the exporter's lock window — i.e. someone triggered `/eod-export` between the bust enqueue and processing. | Re-run `/eod-export` for that channel + date; the exporter is idempotent and will fold the bust out on the second pass. |
| `404 unknown channel ...` or `404 ... eodDropDir` | Channel not wired for post-trade. | Check `postTradeAudit.enabled` + `eodDropDir` in the channel config and restart. |

---

## 8. Where to look in the code

| Subsystem | Path |
| --- | --- |
| Operator HTTP surface | `src/B3.Exchange.Host/HttpServer.cs` |
| Host wiring + config schema | `src/B3.Exchange.Host/ExchangeHost.cs`, `src/B3.Exchange.Host/HostConfig.cs` |
| FIXP session lifecycle | `src/B3.Exchange.Gateway/FixpSession.cs`, `FixpStateMachine.cs` |
| Mass-cancel / CoD plumbing | `src/B3.Exchange.Gateway/OrderOwnershipMap.cs`, `FixpSession.cs` (CoD) |
| Per-channel matching | `src/B3.Exchange.Core/ChannelDispatcher.cs`, `src/B3.Exchange.Matching/MatchingEngine.cs` |
| State persistence | `src/B3.Exchange.Persistence/FileChannelStatePersister.cs`, `BinaryChannelStateSnapshotCodec.cs`, `FileChannelWriteAheadLog.cs`; migration framework in `src/B3.Exchange.Core/SnapshotMigrations.cs` |
| Post-trade audit log | `src/B3.Exchange.PostTrade/FileAuditLogWriter.cs`, `AuditRecordCodec.cs`, `AuditIndexCodec.cs`, `AuditWatermarkCodec.cs` |
| Late-correction (bust) flow | `src/B3.Exchange.PostTrade/BustValidator.cs`, `BustDedupIndex.cs`, `AmendmentsPublisher.cs`; `src/B3.Exchange.Core/ChannelDispatcher.Operator.cs` (routing) |
| UMDF wire encoders | `src/B3.Umdf.WireEncoder/` |
| Synthetic trader strategies | `src/B3.Exchange.SyntheticTrader/MarketMakerStrategy.cs`, `NoiseTakerStrategy.cs` |

For protocol semantics see
[`B3-ENTRYPOINT-COMPLIANCE.md`](./B3-ENTRYPOINT-COMPLIANCE.md) (gap
status table) and
[`B3-ENTRYPOINT-ARCHITECTURE.md`](./B3-ENTRYPOINT-ARCHITECTURE.md)
(threading model and component boundaries).
