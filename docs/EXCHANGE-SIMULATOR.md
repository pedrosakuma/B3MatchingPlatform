# Exchange Simulator

Stateful B3-style exchange simulator built on top of the matching engine
in `src/B3.Exchange.Matching` and the EntryPoint TCP server in
`src/B3.Exchange.EntryPoint`. Publishes synthetic UMDF traffic on multicast
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
  is not thread-safe by design — single-threaded per channel).
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
  "tcp": { "listen": "0.0.0.0:9876", "enteringFirm": 1 },
  "http": { "listen": "0.0.0.0:8080", "livenessStaleMs": 5000 },
  "channels": [
    {
      "channelNumber": 84,
      "incrementalGroup": "224.0.20.84",
      "incrementalPort": 30084,
      "ttl": 1,
      "instruments": "config/instruments-eqt.json"
    }
  ]
}
```

* `tcp.listen` — bind address for the EntryPoint TCP server.
* `tcp.enteringFirm` — uint stamped on every accepted order (single-firm
  default; per-session firm assignment is not yet implemented).
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
* `instruments` — path to the instrument list (re-uses the format already
  consumed by `B3.Exchange.Instruments.InstrumentLoader`).

## Operability endpoints

Only enabled when the `http` config block is present. All endpoints are
plain HTTP (no TLS, no auth — assume an in-cluster scrape target / sidecar
healthcheck).

| Path             | Status semantics                                                                |
|------------------|---------------------------------------------------------------------------------|
| `/health/live`   | 200 if every dispatcher loop has ticked within `http.livenessStaleMs`; else 503 |
| `/health/ready`  | 200 once every registered `IReadinessProbe` reports ready; else 503             |
| `/metrics`       | Prometheus 0.0.4 text exposition (always 200)                                   |

`/metrics` series:

| Metric                                        | Type    | Labels             | Notes                                                                 |
|-----------------------------------------------|---------|--------------------|-----------------------------------------------------------------------|
| `exch_orders_in_total`                        | counter | `channel`          | NewOrder / Cancel / Replace commands processed.                        |
| `exch_packets_out_total`                      | counter | `channel`          | UMDF packets handed to the multicast sink.                            |
| `exch_snapshots_emitted_total`                | counter | `channel`          | Stub — incremented by the snapshot rotator (issue #1) once merged.    |
| `exch_instrument_defs_emitted_total`          | counter | `channel`          | Stub — incremented by the instrument-definition publisher (issue #2). |
| `exch_dispatch_loop_last_tick_unixms`         | gauge   | `channel`          | Unix ms of the dispatcher loop's last heartbeat.                       |
| `exch_send_queue_depth`                       | gauge   | `channel,session`  | Per-`EntryPointSession` outbound queue. `channel="all"` because the queue is shared across channels. |

### Readiness today vs. once issues #1/#2 land

`IReadinessProbe` is an `OR-of-AND` composition: the host's overall
readiness is the AND of every registered probe. The snapshot rotator
(issue #1) and instrument-definition publisher (issue #2) will each
register their own probe and flip ready once they have emitted at least
one snapshot / definition per loaded instrument since startup.

Until those land, the host registers a single `StartupReadinessProbe`
that flips ready as soon as `ExchangeHost.StartAsync` returns, so
`/health/ready` is effectively equivalent to `/health/live` for now.

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

v1 limitation: `Cancel` and `Modify` require an explicit `OrderID`.
`OrigClOrdID`-only lookup (find the order by its original client id) is
deferred to a later milestone.

### Outbound (TCP execution reports)

| Template ID | Name                        | BlockLength |
|-------------|-----------------------------|-------------|
| 200         | ExecutionReport_NewV2       | 144         |
| 201         | ExecutionReport_ModifyV2    | 160         |
| 202         | ExecutionReport_CancelV2    | 156         |
| 203         | ExecutionReport_TradeV2     | 154         |
| 204         | ExecutionReport_RejectV2    | 138         |

### Outbound (UMDF multicast)

Standard B3 UMDF inc packets — `PacketHeader` (16 bytes) + framed
`Order_MBO_50`, `DeleteOrder_MBO_51`, `Trade_53` messages. Compatible with
the existing `B3.Umdf.ConsoleApp` consumer in this repo.

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
