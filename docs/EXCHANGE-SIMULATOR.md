# Exchange Simulator

Stateful B3-style exchange simulator built on top of the matching engine
in `src/B3.Exchange.Matching` and the EntryPoint TCP server in
`src/B3.Exchange.Gateway`. Publishes synthetic UMDF traffic on multicast
and accepts SBE EntryPoint orders over TCP.

This is **distinct from** the synthetic publisher in
`tools/SyntheticUmdfPublisher`, which is a stateless rotation-based UMDF
generator used purely for benchmarking/load. The exchange simulator is
stateful: client orders enter a real limit order book, get matched with
counterparty resting orders, and the resulting MBO/Trade events are published
on the multicast bus.

## Architecture

```
TCP client ──► EntryPointSession ──► HostRouter ──► ChannelDispatcher ──► MatchingEngine
                                       (by SecurityId)         │
                                                               ├─► UMDF MBO/Trade frames ──► UDP multicast
                                                               └─► ExecutionReports ──────► back to TCP client
```

* `HostRouter` dispatches each inbound command (NewOrder/Cancel/Replace) to
  the `ChannelDispatcher` whose channel owns the order's instrument.
* Each `ChannelDispatcher` runs a single dispatch thread (the matching engine
  is not thread-safe by design — single-threaded per channel). The engine is
  bound to that thread on dispatcher startup via
  `MatchingEngine.BindToDispatchThread`; every public engine entry point and
  every `IMatchingEventSink` callback asserts the owner thread in DEBUG
  builds (issue #169). Cross-thread access must go through the dispatcher's
  `Enqueue*` methods, which post a `WorkItem` onto the inbox channel.
* Per-command emitted events are batched into one UMDF packet (PacketHeader +
  N inc messages) before being multicast.
* The `orderId → reply` map ensures execution reports for passive (resting)
  fills route back to the originating TCP session even when the aggressing
  command came from a different session.

## Quick start (local, no Docker)

```bash
# 1. Build
dotnet build SbeB3UmdfConsumer.slnx

# 2. Run with the sample config
dotnet run --project src/B3.Exchange.Host -- config/exchange-simulator.json
```

Output:

```
[HH:MM:SS.fffZ] channel 84: 3 instruments → 224.0.20.84:30084
[HH:MM:SS.fffZ] listening on 0.0.0.0:9876
```

## Quick start (Docker)

```bash
docker compose -f docker-compose.exchange.yml up --build
```

Exposes:

* `localhost:8080` — WebSocket book API (consumer)
* `localhost:9876` — EntryPoint TCP listener (exchange)
* `localhost:3000` — web frontend

## Configuration

`config/exchange-simulator.json`:

```json
{
  "tcp": {
    "listen": "0.0.0.0:9876",
    "enteringFirm": 1,
    "heartbeatIntervalMs": 30000,
    "idleTimeoutMs": 30000,
    "testRequestGraceMs": 5000,
    "suspendedTimeoutMs": 300000,
    "sendingTimeSkewToleranceMs": 5000,
    "maxOrderQty": 1000000000,
    "maxPrice": 1000000000000000,
    "priceBandPercent": null
  },
  "auth": { "devMode": true },
  "firms": [
    { "id": "FIRM01", "name": "Alpha", "enteringFirmCode": 100 },
    { "id": "FIRM02", "name": "Beta",  "enteringFirmCode": 200 }
  ],
  "sessions": [
    { "sessionId": "FIRM01-SESS-01", "firmId": "FIRM01", "accessKey": "dev-1" },
    { "sessionId": "FIRM02-SESS-01", "firmId": "FIRM02", "accessKey": "dev-2" }
  ],
  "http": { "listen": "0.0.0.0:8080", "livenessStaleMs": 5000 },
  "channels": [
    {
      "channelNumber": 84,
      "incrementalGroup": "224.0.20.84",
      "incrementalPort": 30084,
      "ttl": 1,
      "selfTradePrevention": "none",
      "instruments": "config/instruments-eqt.json",
      "snapshot": {
        "group": "224.0.20.184",
        "port": 30184,
        "ttl": 1,
        "cadenceMs": 1000,
        "maxEntriesPerChunk": 30
      },
      "instrumentDefinition": {
        "channelNumber": 184,
        "group": "224.0.21.84",
        "port": 31084,
        "ttl": 1,
        "cadenceMs": 5000
      }
    }
  ]
}
```

* `tcp.listen` — bind address for the EntryPoint TCP server.
* `tcp.enteringFirm` — **DEPRECATED** (pre-#67 single-tenant fallback).
  Used only when `firms[]`/`sessions[]` are empty. New configs should
  declare firms and sessions explicitly (see below).
* `auth.devMode` — when `true`, the FIXP `Negotiate` handler (#42) skips
  `accessKey` validation. Useful for local dev / tests. Mixing
  `devMode=true` with non-empty per-session `accessKey` values produces a
  startup warning.
* `firms[]` — one entry per participant (corretora). Each firm exposes
  an `enteringFirmCode` (uint) that is stamped on outbound SBE headers.
  See `docs/B3-ENTRYPOINT-ARCHITECTURE.md` §4.1.
* `sessions[]` — per-`(firm, session)` credentials. The Negotiate
  handshake (#42) resolves a session by `sessionId` and validates the
  peer's `access_key` against `accessKey` (when `auth.devMode=false`).
  Pre-#42 the host picks the lexicographically-first `sessionId` as the
  default tenant for every accept. Optional `policy{}` block overrides
  per-session throttling and keep-alive timings.
* `tcp.heartbeatIntervalMs` — server emits a `Sequence` (templateId=9, used
  as heartbeat by B3) when no other outbound traffic has been sent within
  this window. Default `30000`.
* `tcp.idleTimeoutMs` — inbound silence after which the server sends a
  `Sequence` probe (the FIXP equivalent of FIX `TestRequest`). Default
  `30000`.
* `tcp.testRequestGraceMs` — additional silence the server tolerates after
  the probe before tearing down the connection. Default `5000`. On teardown
  the session is closed; a `BusinessReject` (templateId=206) framing the
  reason is the responsibility of issue #11 — `EntryPointSession.Close(reason)`
  exposes the seam.
* `tcp.suspendedTimeoutMs` — how long a Suspended FIXP session remains
  re-attachable after its transport drops before the listener reaper fully
  closes it. Default `300000`; set `0` to disable the suspended-session
  reaper.
* `tcp.sendingTimeSkewToleranceMs` — maximum absolute skew tolerated between
  the server clock and an inbound application message's
  `InboundBusinessHeader.sendingTime`. Default `5000`; set `0` to disable.
* `tcp.maxOrderQty` — decode-time upper bound for `OrderQty` /
  `NewQuantity`. Default `1000000000`; larger values are rejected with
  `ER_Reject(OrdRejReason=99 Other)`.
* `tcp.maxPrice` — decode-time upper bound for EntryPoint `Price` mantissa
  (`/10000`). Default `1000000000000000` (decimal
  `100,000,000,000.0000`), deliberately far above normal B3 cash-equity and
  listed-derivative prices while still catching corrupted 64-bit payloads.
* `tcp.priceBandPercent` — optional dynamic price-band check relative to the
  instrument's last trade price. `null` disables it; when enabled but no last
  trade reference exists yet, the decoder accepts and leaves downstream
  validation unchanged. Breaches produce
  `ER_Reject(OrdRejReason=16 Price exceeds current price band)`.
* `http` — **optional** Kestrel-hosted operability endpoint exposing
  `/health/live`, `/health/ready`, and `/metrics`. Omit the entire block to
  disable HTTP.
  * `http.listen` — bind address. Default `0.0.0.0:8080`.
  * `http.livenessStaleMs` — `/health/live` returns 503 if any dispatcher
    has not heartbeat within this window. Default `5000` (5 s). Each
    dispatcher records a heartbeat on every loop wakeup (1 s timer +
    every processed command), so a stuck or dead loop is detected within
    `livenessStaleMs + ~1s`.
* `channels[]` — one matching engine + one outbound multicast group per
  UMDF channel.
* `channels[].selfTradePrevention` — per-channel self-trade prevention policy
  evaluated each time an aggressor would cross against a resting order from
  the same `EnteringFirm`. One of:
  * `none` (default) — trade as today; firms can self-trade.
  * `cancel-aggressor` — cancel the aggressor's residual quantity and stop
    further matching. Trades already executed against other firms stand. The
    originating session receives an `ExecutionReport_Reject` with reason
    `SelfTradePrevention`; no MBO event is emitted (the aggressor never
    rested).
  * `cancel-resting` — cancel the conflicting resting order and continue
    matching the aggressor against the next maker. Each canceled resting
    order produces a `DeleteOrder_MBO_51` and an `ExecutionReport_Cancel`
    routed to the original resting-order owner (cancel reason
    `SelfTradePrevention`).
  * `cancel-both` — cancel both the conflicting resting order and the
    aggressor's residual; stop further matching.
* `instruments` — path to the instrument list (re-uses the format already
  consumed by `B3.Exchange.Instruments.InstrumentLoader`).
* `instrumentDefinition` *(optional)* — enables a dedicated
  `SecurityDefinition_12` publisher on its own multicast group so
  late-joining consumers can resolve every SecurityID seen on MBO/Trade
  frames without a pre-loaded instrument list.
  * `channelNumber` — UMDF channel number stamped on the InstrumentDef
    PacketHeader. Defaults to the parent channel's number when 0.
  * `group` / `port` / `ttl` / `localInterface` — multicast destination.
  * `cadenceMs` — how often (ms) to re-emit the full instrument list.
    Defaults to 5000 (5 s).
* `snapshot` (optional) — per-channel snapshot publisher. When present, the
  host opens a second multicast socket on `group:port` and a per-channel
  `SnapshotRotator` round-robins through the channel's instruments,
  publishing a `SnapshotFullRefresh_Header_30` + chunked
  `SnapshotFullRefresh_Orders_MBO_71` frames every `cadenceMs`
  milliseconds. The snapshot channel maintains its own `SequenceVersion` /
  `SequenceNumber` state, distinct from the incremental channel.
  `maxEntriesPerChunk` caps the per-`Orders_71` group size (defaults to
  30, ~1.3 KB per chunk → fits a standard 1500-byte MTU). Omit the
  `snapshot` block to publish only the incremental feed (no bootstrap for
  mid-session consumers).
* `persistence` *(optional, per channel — issue #260 + roadmap #262–#272)*
  — enables order book + counter persistence so a restart resumes with
  the live working book, RptSeq, order/trade-id allocators, untriggered
  stops, and resting-order ownership intact. The dispatcher captures a
  snapshot on the loop thread after every command flush (subject to
  `throttle`), serializes it (synchronously by default, or off-loop
  when `asyncWriter=true`) and writes it atomically into one of N
  rolling generation slots. Optional Write-Ahead Log records every
  state-mutating command between snapshots, replayed on cold start.
  * `dataDir` — directory holding the per-channel snapshot file(s) and
    optional WAL. The host creates it if missing. Mount a durable
    volume here in container deployments (e.g. `/var/lib/b3matching`
    — see Dockerfile).
  * `format` *(#266)* — `"json"` (default; pretty + `jq`-friendly) or
    `"binary"` (compact `B3SS`-prefixed framing, ~3× smaller). Both
    formats are auto-detected on load via magic-byte sniff, so a
    deployment can flip the value (or roll back) without a one-shot
    conversion.
  * `generations` *(#264)* — number of rolling snapshot slots kept on
    disk per channel. The persister round-robins across slots
    `0..generations-1` and falls back to the previous slot if the
    newest is corrupted. Default `3`.
  * `throttle` *(#267)* — `{ everyN, minIntervalMs }`. Both fields
    optional; whichever fires first triggers the persist. Operator
    commands always force-persist regardless of throttle. Absent /
    both zero ⇒ persist after every command flush (PR #261 default).
  * `asyncWriter` *(#268)* — when `true`, the dispatcher captures the
    snapshot POCO on the loop thread (cheap) and hands serialization
    + write to a dedicated writer thread. Backpressure is
    last-write-wins (newest capture supersedes older queued one) —
    monitor `exch_snapshot_dropped_by_backpressure_total`. Default
    `false` so existing deployments keep zero-RPO behaviour.
  * `wal` *(#269)* — `{ enabled, fsyncPerWrite }`. When
    `enabled=true` the dispatcher appends one record per
    state-mutating command to `{dataDir}/channel-{N}.wal` BEFORE the
    engine observes it, and replays the surviving records on cold
    start — closing the gap between the most-recent snapshot and an
    unclean shutdown. `fsyncPerWrite` (default `true`) trades
    latency for durability per-record. Off by default so existing
    deployments pay no per-command file IO.
  * `orphanSessionPolicy` *(#270)* — policy applied to
    `OrderOwnerSnapshot` entries whose `SessionValue` is no longer
    present in the host's session registry at restore time. Either
    `"drop"` (default — log + bump
    `exch_owner_orphans_dropped_total`, skip the owner; engine
    state still loads) or `"reject"` (treat as fatal; channel fails
    closed). Case-insensitive.
* Snapshot evolution and on-disk schema versioning are described in the
  *Snapshot file format* and *Snapshot schema evolution* sections below.
* Limitations: auction-top throttling state, the UMDF retransmit ring,
  snapshot-rotator cursor, and FIXP session counters are not persisted.
  Operators that depend on auction throttling should drain in-flight
  indicative state before restart. Omit the `persistence` block to keep
  the legacy stateless boot.

### FIXP session resync persistence (`tcp.retransmitPersistenceDir`)

The top-level `tcp.retransmitPersistenceDir` directory enables
per-session **FIXP envelope + outbound journal** persistence
(issue #405). When set, the host writes two artifacts:

* **Outbound journal** at `{dir}/journal/session-{sessionId:x8}/segment-*.log` —
  append-only, segmented (~16 MiB per segment), every outbound
  business frame is durably recorded with seq + timestamp + CRC32C.
  `RetransmitRequest` for any seq not in the in-memory ring is served
  from this journal, eliminating the bounded-ring eviction window of
  the prior `#289` prototype.
* **Envelope-state snapshot** at `{dir}/state/session-{sessionId:x8}.state` —
  rewritten on every handshake event (Negotiate / Establish /
  CoD-params / non-terminal Close) with `SessionVerId`,
  `LastIncomingSeqNo`, outbound `MsgSeqNum`, and CoD parameters.

On boot, every snapshot is loaded and `SessionClaimRegistry` is
seeded so a peer reconnecting after `docker compose restart
matching-platform` with its original `SessionVerId` is accepted
rather than `UNNEGOTIATED`-rejected — the
`EstablishmentAck.serverFlow=RECOVERABLE` contract (SBE 5.2 §1.5)
is honored across host restarts.

Terminal lifecycle events — peer-initiated `Terminate(Finished)`
and `SuspendedTimeout` expiration — remove both artifacts. Host
graceful shutdown and transport errors **preserve** them so the
peer can resync on reconnect.

Property name kept from `#289` for config back-compat; the prior
bounded `.ring` files are no longer read or written.

**Consolidated example with every sub-block:**

```jsonc
"persistence": {
  "dataDir": "/var/lib/b3matching/channel-84",
  "format": "binary",
  "generations": 5,
  "throttle": { "everyN": 100, "minIntervalMs": 250 },
  "asyncWriter": true,
  "wal": { "enabled": true, "fsyncPerWrite": true },
  "orphanSessionPolicy": "drop"
}
```


### Snapshot file format (issue #266)

The persister's on-disk encoding is selected per-channel via
`persistence.format` — accepted values are `"json"` (default; the
historical PR #261 behaviour) and `"binary"`. Both formats are always
recognised on **load** thanks to the leading magic-byte sniff
(`B3SS` ⇒ binary, anything else ⇒ JSON), so flipping the value and
restarting gracefully migrates a channel forward: the next snapshot
write uses the new format and old slots in the previous format remain
loadable until the rolling generations evict them. Roll-back works the
same way in reverse.

* **JSON** — `System.Text.Json` (`JsonStringEnumConverter`,
  `WriteIndented=false`). Debuggable with `jq`, but pays ~150 bytes of
  property names per resting order and renders every integer as
  decimal text.
* **Binary** — compact little-endian framing implemented by
  `BinaryChannelStateSnapshotCodec`: 4-byte `B3SS` magic, `uint16`
  schema version, fixed-width primitives for every numeric field,
  uvarint-prefixed UTF-8 for strings, uvarint-prefixed counts for
  every collection. Empty `Stops` and missing `Stops` collapse to the
  same wire form (uvarint `0`) — the engine treats them identically
  on restore. There is no trailing checksum; the existing
  tmp+fsync+rename atomic write contract still protects against
  partial writes, and length-prefixed framing makes truncation
  self-detecting (a malformed file throws and the persister falls
  back to the previous generation).

Measured size ratio on a synthetic 200-order book is ~3× (binary
~20 KB, JSON ~60 KB). Real production books with longer ClOrdIds,
larger phase tables, and richer ownership maps tend to push the
ratio higher; the bundled `BinaryChannelStateSnapshotCodecTests`
asserts ≥2.5× as the conservative lower bound.

The schema-evolution policy from issue #272 still applies — bump
`ChannelStateSnapshot.CurrentVersion` and register a migration when
the layout changes. Binary files carry the same `Version` field in
their header and are subject to the same forward-version rejection.
Operators rolling back to a host that does not understand the binary
format should switch `persistence.format` to `"json"` *before*
upgrading; once a snapshot has been written by a newer schema,
rolling the host back without a `POST /admin/channels/{ch}/snapshot/reset?force=true`
will fail closed.

### Snapshot schema evolution (issue #272)

`ChannelStateSnapshot` carries an integer `Version` field
(`ChannelStateSnapshot.CurrentVersion`, currently `1`). The persister
parses every snapshot to a `JsonNode` first and runs it through
`SnapshotMigrationSet` before final deserialization. Three rules apply:

* **Forward compatibility** — System.Text.Json ignores unknown JSON
  properties on read by default, so a payload written by a *newer* host
  loads cleanly under an *older* host as long as the fields the old
  host already knows about retain their meaning. Only ever add new
  optional fields with sensible defaults; never repurpose or rename a
  field without a version bump.
* **Backward compatibility** — payloads written by older hosts require
  a registered migration for every step from their `Version` up to
  `CurrentVersion`. Bumping the schema is a two-commit dance: first
  register the `N → N+1` migration in `SnapshotMigrationSet.BuildDefault`,
  then bump `CurrentVersion`. Missing any step on the chain throws and
  the dispatcher fails closed (per issue #270's restore contract).
* **Forward-version rejection** — if the on-disk `Version` is *higher*
  than `CurrentVersion` the load fails with a clear message. Operators
  must run a host version that supports the snapshot or invoke
  `POST /admin/channels/{ch}/snapshot/reset?force=true` to start fresh.

## Operability endpoints

Only enabled when the `http` config block is present. All endpoints are
plain HTTP (no TLS, no auth — assume an in-cluster scrape target / sidecar
healthcheck).

| Path             | Status semantics                                                                |
|------------------|---------------------------------------------------------------------------------|
| `/health/live`   | 200 if every dispatcher loop has ticked within `http.livenessStaleMs`; else 503 |
| `/health/ready`  | 200 once every registered `IReadinessProbe` reports ready; else 503             |
| `/metrics`       | Prometheus 0.0.4 text exposition (always 200)                                   |
| `POST /channel/{ch}/snapshot-now`  | Operator command — forces a snapshot publish on the next dispatcher cycle. 202 on enqueue, 404 unknown channel, 503 if the dispatcher's inbound queue is full. |
| `POST /channel/{ch}/bump-version`  | Operator command — atomically bumps the incremental + snapshot `SequenceVersion`, clears the order book, resets `RptSeq` to 0, and emits a `ChannelReset_11` frame on the incremental channel under the new version. 202 on enqueue, 404 unknown channel, 503 if the queue is full. |
| `GET /sessions`                    | JSON array of every currently-attached FIXP session (issue #70). |
| `GET /sessions/{id}`               | JSON object for a single session by `sessionId` (e.g. `conn-2a`). 404 if unknown. |
| `GET /firms`                       | JSON array of firms declared in `firms[]`. |

`/metrics` series:

| Metric                                        | Type    | Labels             | Notes                                                                 |
|-----------------------------------------------|---------|--------------------|-----------------------------------------------------------------------|
| `exch_orders_in_total`                        | counter | `channel`          | NewOrder / Cancel / Replace commands processed.                        |
| `exch_packets_out_total`                      | counter | `channel`          | UMDF packets handed to the multicast sink.                            |
| `exch_snapshots_emitted_total`                | counter | `channel`          | Stub — incremented by the snapshot rotator (issue #1) once merged.    |
| `exch_instrument_defs_emitted_total`          | counter | `channel`          | Stub — incremented by the instrument-definition publisher (issue #2). |
| `exch_dispatch_loop_last_tick_unixms`         | gauge   | `channel`          | Unix ms of the dispatcher loop's last heartbeat.                       |
| `exch_send_queue_depth`                       | gauge   | `channel,session`  | Per-`EntryPointSession` outbound queue. `channel="all"` because the queue is shared across channels. |
| `fixp_session_state`                          | gauge   | `session,firm`     | FIXP state machine value (Idle=0, Negotiated=1, Established=2, Suspended=3, Terminated=4). |
| `fixp_session_outbound_seq`                   | gauge   | `session`          | Last allocated outbound `MsgSeqNum`.                                  |
| `fixp_session_inbound_expected_seq`           | gauge   | `session`          | Highest inbound `MsgSeqNum` accepted (for gap detection).             |
| `fixp_session_retx_buffer_depth`              | gauge   | `session`          | Number of frames currently retained for retransmission.               |
| `fixp_session_attached_transports`            | gauge   | `session`          | 1 if a TCP transport is currently attached, 0 if Suspended/awaiting rebind. |
| `fixp_session_last_activity_unixms`           | gauge   | `session`          | Unix ms of the last byte received on the session (liveness).          |

### Readiness today vs. once issues #1/#2 land

`IReadinessProbe` implementations are combined using logical AND: the host's overall
readiness is the AND of every registered probe. The snapshot rotator
(issue #1) and instrument-definition publisher (issue #2) will each
register their own probe and flip ready once they have emitted at least
one snapshot / definition per loaded instrument since startup.

Until those land, the host registers a single `StartupReadinessProbe`
that flips ready as soon as `ExchangeHost.StartAsync` returns, so
`/health/ready` is effectively equivalent to `/health/live` for now.

### Operator endpoints (issue #6)

`POST /channel/{ch}/snapshot-now` and `POST /channel/{ch}/bump-version`
are operator-driven hooks intended for exercising consumer recovery /
epoch handling against a live simulator without restarting it. Both
dispatch their work via the channel's bounded inbound queue, so the
HTTP handler returns 202 as soon as the work item is enqueued (404 if
the channel number is unknown, 503 if the queue is full).

`bump-version` is the heart of the channel-reset path:

1. The dispatcher clears every per-instrument book and resets the
   engine's `RptSeq` counter to 0.
2. The incremental `SequenceVersion` is incremented and `SequenceNumber`
   is rebased to 0.
3. The attached snapshot rotator's `SequenceVersion` is incremented in
   lockstep (its `SequenceNumber` is rebased to 0 the same way).
4. A single `ChannelReset_11` frame is emitted on the incremental
   channel, packed into a one-message packet whose header carries the
   NEW `SequenceVersion` and `SequenceNumber=1`.

No per-order `DeleteOrder_MBO_51` frames are emitted ahead of the
`ChannelReset` — per the schema, `ChannelReset` is the canonical signal
for consumers to drop all per-channel state, and emitting redundant
deletes alongside it would risk consumers seeing cancels for orders
they have already discarded.

The next snapshot publish (whether triggered by the configured cadence
or by an operator `snapshot-now`) reflects the empty book stamped with
the new versions.

### Operator session inspection (issue #70)

`GET /sessions`, `GET /sessions/{id}` and `GET /firms` expose live FIXP
session state in JSON form so operators can correlate a peer report
("session is stuck", "I see no fills") with the simulator's view of the
session without scraping `/metrics`.

```bash
curl -s http://localhost:8080/firms
# => [{"id":"FIRM01","name":"Acme","enteringFirmCode":7}]

curl -s http://localhost:8080/sessions
# => [{"sessionId":"conn-2a","firmId":"FIRM01","state":2,"sessionVerId":1,
#      "outboundSeq":4291,"inboundExpectedSeq":1027,"retxBufferDepth":4291,
#      "sendQueueDepth":0,"attachedTransportId":"tx-2a","lastActivityAtMs":1700000123456}]

curl -s http://localhost:8080/sessions/conn-2a
# => 200 (single object) or 404 if no session with that id is currently registered
```

`state` mirrors the values exposed by the `fixp_session_state` gauge.

### Docker `HEALTHCHECK`

`Dockerfile` ships a `HEALTHCHECK` that hits `/health/live` via `wget`.
If you disable HTTP in your config, override `HEALTHCHECK NONE` in a
derived image.

## Wire protocol

### Inbound (TCP)

8-byte SBE `MessageHeader` + body. Schema id is 1. Supported templates:

| Template ID | Name                | BlockLength |
|-------------|---------------------|-------------|
| 100         | SimpleNewOrderV2    | 82          |
| 101         | SimpleModifyOrderV2 | 98          |
| 105         | OrderCancelRequest  | 76          |
| 9           | Sequence (heartbeat)| 4           |

The `Sequence` frame doubles as the FIXP-style heartbeat: any inbound
frame (including `Sequence`) resets the server's idle timer. Clients
should emit `Sequence` periodically (default cadence: ≤ `idleTimeoutMs`)
to keep the session alive.

`Cancel` and `Modify` accept either an explicit engine-assigned `OrderID` or
the original `OrigClOrdID` (the `ClOrdID` of the order being modified/cancelled).
The integration layer maintains a per-channel `(EnteringFirm, ClOrdID) → OrderID`
index that is populated when an order rests on the book and evicted when the
order leaves (cancel or full fill). Submitting both fields is allowed; the
explicit `OrderID` wins. If neither is present, or if the `OrigClOrdID` is
unknown to the channel, an `ExecutionReport_Reject` is returned.

### Outbound (TCP execution reports)

| Template ID | Name                        | BlockLength |
|-------------|-----------------------------|-------------|
| 200         | ExecutionReport_NewV2       | 144         |
| 201         | ExecutionReport_ModifyV2    | 160         |
| 202         | ExecutionReport_CancelV2    | 156         |
| 203         | ExecutionReport_TradeV2     | 154         |
| 204         | ExecutionReport_RejectV2    | 138         |
| 9           | Sequence (heartbeat/probe)  | 4           |

The server emits `Sequence` frames in two situations: (1) periodically
when the outbound link has been silent for `heartbeatIntervalMs`, and
(2) as a probe when the inbound link has been silent for `idleTimeoutMs`.
If the client does not respond within `testRequestGraceMs`, the session
is closed.

### Outbound (UMDF multicast)

Two distinct multicast streams per channel:

* **Incremental** — `PacketHeader` (16 bytes) + framed `Order_MBO_50`,
  `DeleteOrder_MBO_51`, `Trade_53` messages. One UDP packet per inbound
  command (events emitted while processing a single command are batched
  into one packet ≤1400 bytes, with a monotonic `SequenceNumber`).
* **Snapshot** (optional) — `PacketHeader` + `SnapshotFullRefresh_Header_30`
  + one or more `SnapshotFullRefresh_Orders_MBO_71` chunk frames. A
  per-channel rotator publishes a complete snapshot for one instrument per
  tick, round-robining through the channel's instruments. Empty / illiquid
  instruments emit a header-only packet with `LastRptSeq` absent (per B3
  §7.4). Snapshot packets carry their own `SequenceVersion` / `SequenceNumber`
  separate from the incremental channel.

Both streams are compatible with the existing `B3.Umdf.ConsoleApp` consumer
in this repo.

When the optional `instrumentDefinition` block is configured per channel,
the host also emits `SecurityDefinition_12` (`SecurityDefinition_d` in FIX
terms) frames to a dedicated multicast group every `cadenceMs`
milliseconds (default 5 s). One full cycle covers every configured
instrument; frames are packed into ≤1400-byte UDP datagrams with
monotonic `SequenceNumber`s on the InstrumentDef channel.

## Sending an order with `nc`

The simplest possible client: a Python one-liner that builds a
`SimpleNewOrderV2` frame and pipes it to `nc`.

```python
# tools/sample_send_order.py (sketch)
import socket, struct
schema_id, template_id, version, block_len = 1, 100, 2, 82
sec_id = 900_000_000_001  # PETR4
clord = 1
side = ord('1')           # Buy
ord_type = ord('2')       # Limit
tif = ord('0')            # Day
qty = 100
price = 12_3450           # implicit /10000

hdr = struct.pack("<HHHH", block_len, template_id, schema_id, version)
body = bytearray(block_len)
struct.pack_into("<Q", body, 20, clord)        # ClOrdID
struct.pack_into("<q", body, 48, sec_id)       # SecurityID
body[56] = side
body[57] = ord_type
body[58] = tif
struct.pack_into("<q", body, 60, qty)          # OrderQty
struct.pack_into("<q", body, 68, price)        # PriceMantissa

with socket.create_connection(("localhost", 9876)) as s:
    s.sendall(hdr + body)
    print("sent")
```

You should see the order appear in the consumer's book (subscribe via
WebSocket on 8080) and a multicast `Order_MBO_50` NEW frame on the wire.

## Notes

* **Synthetic publisher is now legacy/benchmark-only**. For meaningful
  end-to-end work (real client orders, real matching, real ExecutionReports),
  use the exchange simulator. The synthetic publisher remains useful for
  worst-case load testing of the consumer/conflation pipeline.
* **Single-tenant**: today every TCP connection shares the same
  `EnteringFirm`. Multi-firm support is a future milestone.
