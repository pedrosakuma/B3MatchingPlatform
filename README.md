# SbeB3Exchange

Stateful B3-spec exchange simulator. Speaks the **B3 EntryPoint** SBE protocol
inbound (TCP) and the **B3 UMDF** market-data wire format outbound (UDP
multicast). Companion to [`SbeB3UmdfConsumer`][consumer] — designed to run as
a 24/7 simulated venue against any UMDF consumer.

[consumer]: https://github.com/pedrosakuma/SbeB3UmdfConsumer

## What's inside

- **`B3.Exchange.Matching`** — single-thread per-symbol limit order book.
  Limit/Market × Day/IOC/FOK, replace (priority preservation rules), cancel,
  FOK pre-check, market-no-liquidity, tick/lot/band validation.
- **`B3.Exchange.Instruments`** — JSON instrument loader.
- **`B3.Exchange.EntryPoint`** — TCP listener, framed SBE inbound decoder,
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

```bash
docker build -t sbeb3exchange:latest .
docker run --rm --network host -v $(pwd)/config:/app/config sbeb3exchange:latest
```

The host needs network access for both the multicast publish socket and the
EntryPoint TCP listener (default port 9876).

## Schemas

`schemas/b3-market-data-messages-2.2.0.xml` and
`schemas/b3-entrypoint-messages-8.4.2.xml` are vendored copies of the
official B3 SBE schemas. The same files exist in `SbeB3UmdfConsumer`; keep
them in sync when upgrading.

## License

MIT — see `LICENSE`.
