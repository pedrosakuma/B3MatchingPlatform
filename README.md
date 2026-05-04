# SbeB3Exchange

[![CI](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/ci.yml/badge.svg)](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/ci.yml)
[![Docker](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/docker.yml/badge.svg)](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/docker.yml)
[![CodeQL](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/codeql.yml/badge.svg)](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](global.json)
[![ghcr.io](https://img.shields.io/badge/ghcr.io-b3--matching-2496ED?logo=docker)](https://github.com/pedrosakuma/B3MatchingPlatform/pkgs/container/b3-matching)

Stateful B3-spec exchange simulator. Speaks the **B3 EntryPoint** SBE protocol
inbound (TCP) and the **B3 UMDF** market-data wire format outbound (UDP
multicast or unicast). Designed to run as a 24/7 simulated venue against any
UMDF consumer or EntryPoint client.

> **Status:** active development. The matching engine, EntryPoint TCP gateway,
> and UMDF publisher are functional; FIXP session lifecycle and operator
> endpoints are landing incrementally — see open issues for the roadmap.

## Family of repositories

`B3MatchingPlatform` plays the role of **the exchange itself** — the matching
engine, UMDF publisher, and EntryPoint listener that the rest of the family
talks to. The companion repos play the roles of the consumers and
participants that surround a real exchange:

| Repo | Role | Wire IN | Wire OUT | Frontend? |
| --- | --- | --- | --- | --- |
| **[`B3MatchingPlatform`](https://github.com/pedrosakuma/B3MatchingPlatform)** *(this repo)* | The "exchange" (matching engine + UMDF publisher + EntryPoint listener) | EntryPoint orders | UMDF MD + EntryPoint ER | Operator-only |
| [`B3MarketDataPlatform`](https://github.com/pedrosakuma/B3MarketDataPlatform) | Market-data subscriber (UMDF consumer + WebSocket distribution + frontend) | UMDF | — | Yes |
| [`B3TradingPlatform`](https://github.com/pedrosakuma/B3TradingPlatform) | Participant / OMS-like backend (own orders, positions, end-clients) | EntryPoint ER | EntryPoint orders | Yes |
| [`B3EntryPointClient`](https://github.com/pedrosakuma/B3EntryPointClient) | Wire-pure EntryPoint client lib + conformance suite | EntryPoint ER | EntryPoint orders | — |

```
                   orders ─────────────────────────────────►
   ┌──────────────────────┐                ┌──────────────────────────────┐
   │  B3TradingPlatform   │ ── EntryPoint ►│      B3MatchingPlatform      │
   │  B3EntryPointClient  │ ◄── ER ─────── │   (this repo — "exchange")   │
   └──────────────────────┘                └──────────────┬───────────────┘
                                                          │ UMDF
                                                          ▼
                                            ┌──────────────────────────┐
                                            │   B3MarketDataPlatform   │
                                            └──────────────────────────┘
                   ◄──────────────────────────────── market data
```

## What's inside

- **`B3.Exchange.Matching`** — single-thread per-symbol limit order book.
  Limit/Market × Day/IOC/FOK, replace (priority preservation rules), cancel,
  FOK pre-check, market-no-liquidity, tick/lot/band validation.
- **`B3.Exchange.Instruments`** — JSON instrument loader.
- **`B3.Exchange.Gateway`** — TCP listener, framed SBE inbound decoder,
  ExecutionReport encoder.
- **`B3.Exchange.Core`** — per-channel `ChannelDispatcher`: bounded
  inbound queue, single dispatch thread, packs MBO/Trade frames into 1400-byte
  UMDF packets, publishes via `IUmdfPacketSink`.
- **`B3.Exchange.Host`** — JSON-configured single-binary host wiring
  EntryPoint listener + dispatchers + multicast UDP sinks.
- **`B3.Exchange.SyntheticTrader`** — separate console client that connects
  to the host over EntryPoint TCP and drives continuous order flow
  (market-maker + noise-taker strategies, seeded RNG for repro). See
  `config/synthetic-trader.json` and `docker-compose.synthtrader.yml`.
- **`B3.Umdf.WireEncoder`** — stateless byte-level encoders for UMDF MBO,
  Trade, and Snapshot frames (V16 schema).
- **`B3.Umdf.Sbe` / `B3.EntryPoint.Sbe`** — SBE bindings generated from the
  B3 schemas under `schemas/`.

## Build & run

```bash
dotnet build SbeB3Exchange.slnx
dotnet test SbeB3Exchange.slnx
dotnet run --project src/B3.Exchange.Host -- config/exchange-simulator.json
```

For day-2 operations (HTTP surface, multi-firm config, recovery
scenarios, tuning recipes) see [`docs/RUNBOOK.md`](docs/RUNBOOK.md).

## Docker

### Build locally

```bash
docker build -t sbeb3exchange:latest .
docker run --rm --network host -v $(pwd)/config:/app/config sbeb3exchange:latest
```

### Pre-built images on GHCR

Every push to `main` and every `v*` tag publishes:

```bash
docker pull ghcr.io/pedrosakuma/b3-matching:latest
docker pull ghcr.io/pedrosakuma/b3-matching-synthtrader:latest
```

Tags available: `:latest`, `:sha-<short>`, `:<branch>`, `:vX.Y.Z`, `:X.Y`.

### Bridge-network mode (issue #88, preview)

The default mode publishes UMDF over UDP **multicast** and so requires
host networking. For docker-compose family deployments where multicast is
not routable across the bridge, set `transport: "unicast"` per channel
and point the `*.group` fields at the consumer's service name. See
`config/exchange-simulator.bridge.json` and `docker-compose.bridge.yml`:

```bash
docker compose -f docker-compose.bridge.yml up
```

This mode is **PREVIEW** until the consumer side ships in
[B3MarketDataPlatform#2](https://github.com/pedrosakuma/B3MarketDataPlatform/issues/2).

The host needs network access for both the multicast publish socket and the
EntryPoint TCP listener (default port 9876).

## Schemas

`schemas/b3-market-data-messages-2.2.0.xml` and
`schemas/b3-entrypoint-messages-8.4.2.xml` are vendored copies of the
official B3 SBE schemas. The same files exist in `SbeB3UmdfConsumer`; keep
them in sync when upgrading.

### Inbound EntryPoint compatibility matrix

The exchange parses the inbound SBE templates listed below. Any version
not listed is rejected at the FIXP frame layer with `UnsupportedSchema` /
`UnsupportedTemplate` and the session is terminated (`DECODING_ERROR`).
See `B3.EntryPoint.Wire.EntryPointFrameReader.ExpectedInboundBlockLength`
for the source of truth.

| Template | ID | Versions accepted | Block size | Notes |
|---|---:|---|---:|---|
| `Negotiate` | 100 | V0 | 28 | FIXP session establishment. |
| `Establish` | 101 | V0 | 42 | FIXP session establishment. |
| `Terminate` | 102 | V0 | 13 | Peer-initiated graceful logout. |
| `RetransmitRequest` | 103 | V0 | 20 | FIXP retransmission. |
| `Sequence` | 104 | V0 | 4 | FIXP heartbeat / seq sync. |
| `SimpleNewOrder` | 100 | V2, **V6** | 82 | V6 has the same wire layout as V2; SDK 0.8.0 stamps version=6 (issue #236 / #239). |
| `SimpleModifyOrder` | 101 | V2, **V6** | 98 | Same as above. |
| `NewOrderSingle` | 102 | V2 (125 B), **V6 (133 B)** | 125 / 133 | V6 appends `strategyID@125` (int32) and `tradingSubAccount@129` (uint32). Both fields are decoded; non-null values produce `BusinessMessageReject(33003)` because the engine has no strategy / sub-account routing (issue #238). |
| `OrderCancelReplaceRequest` | 104 | V2 (142 B), **V6 (150 B)** | 142 / 150 | Same +8 trailer (`strategyID@142`, `tradingSubAccount@146`) and same reject-if-non-null policy. |
| `OrderCancelRequest` | 105 | V6 (root version=0) | 76 | Native V6 layout; no V2 ancestor. |
| `NewOrderCross` | 106 | V6 | 84 + group + varData | Native V6. |
| `OrderMassActionRequest` | 701 | V6 | 52 | Native V6. |

**Convention:** when an SDK bumps the schema version stamp **without**
changing the wire layout, both versions are added to
`ExpectedInboundBlockLength` with the same expected size. When the V6
revision **adds** trailing optional fields (additive-only), the new
larger size is added as a separate row and the decoder is updated to
read or reject the trailer based on `body.Length`. We deliberately do
**not** wildcard `blockLength &gt;= V2_size` — the explicit table
catches accidental schema regressions in upstream consumers.

## License

MIT — see `LICENSE`.
