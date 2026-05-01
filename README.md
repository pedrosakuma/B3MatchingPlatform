# SbeB3Exchange

[![CI](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/ci.yml/badge.svg)](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/ci.yml)
[![Docker](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/docker.yml/badge.svg)](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/docker.yml)
[![CodeQL](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/codeql.yml/badge.svg)](https://github.com/pedrosakuma/B3MatchingPlatform/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](global.json)
[![ghcr.io](https://img.shields.io/badge/ghcr.io-b3--matching-2496ED?logo=docker)](https://github.com/pedrosakuma/B3MatchingPlatform/pkgs/container/b3-matching)

Stateful B3-spec exchange simulator. Speaks the **B3 EntryPoint** SBE protocol
inbound (TCP) and the **B3 UMDF** market-data wire format outbound (UDP
multicast or unicast). Companion to [`SbeB3UmdfConsumer`][consumer] — designed
to run as a 24/7 simulated venue against any UMDF consumer.

> **Status:** active development. The matching engine, EntryPoint TCP gateway,
> and UMDF publisher are functional; FIXP session lifecycle and operator
> endpoints are landing incrementally — see open issues for the roadmap.

[consumer]: https://github.com/pedrosakuma/SbeB3UmdfConsumer

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

## License

MIT — see `LICENSE`.
