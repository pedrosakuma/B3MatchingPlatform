# ScenarioReplay

A small console driver that pumps a deterministic JSONL "script" of orders
against a running `B3.Exchange.Host` over the EntryPoint TCP gateway, and
records the resulting ExecutionReports + UMDF multicast packets to a JSONL
"tape" file for diff-based assertion.

Companion tool to issue [#17][issue-17].

## Quick start

```bash
dotnet build SbeB3Exchange.slnx -c Release

# Run an exchange host in another shell first:
dotnet run --project src/B3.Exchange.Host -- config/exchange-simulator.json

# Then replay a script against it:
dotnet run --project tools/ScenarioReplay -- \
    --host 127.0.0.1 --port 9876 \
    --script tools/ScenarioReplay/example.jsonl \
    --multicast 239.255.42.84:30184 \
    --out tape.jsonl
```

## CLI

```
ScenarioReplay --script <path.jsonl> [options]

connection:
  --host <ip|hostname>      EntryPoint host (default 127.0.0.1)
  --port <n>                EntryPoint port (default 9876)

capture:
  --out <path>              Tape file (JSONL); default: discard
  --multicast <group:port>  UMDF incremental multicast to record

timing:
  --speed <multiplier>      Replay speed multiplier (default 1.0).
                            speed=10 makes 1 virtual ms = 0.1 real ms.
  --timeout-ms <n>          ms to wait for trailing ER frames after the
                            last script event (default 1000)
```

Exit codes: `0` ok, `1` aborted/cancelled, `2` usage, `3` script-parse, `4` connect.

## Script format (JSONL)

One event per line. Lines starting with `#` or `//` are comments. Unknown
properties are ignored, so adding fields in a future revision will not
break older scripts.

```jsonl
# resting buy
{"atMs": 0,    "kind": "new",    "clOrdId": 1001, "securityId": 900000000001, "side": "buy",  "type": "limit", "tif": "day", "qty": 100, "px": 320000}
# IOC sell that crosses
{"atMs": 50,   "kind": "new",    "clOrdId": 1002, "securityId": 900000000001, "side": "sell", "type": "limit", "tif": "ioc", "qty": 100, "px": 320000}
# operator cancel of the resting order, by clOrdId
{"atMs": 200,  "kind": "cancel", "clOrdId": 1003, "origClOrdId": 1001, "securityId": 900000000001, "side": "buy"}
```

| Field         | Required for                     | Notes                                                                 |
|---------------|----------------------------------|-----------------------------------------------------------------------|
| `atMs`        | all                              | monotonic ms from script start; events are sorted stably by `atMs`    |
| `kind`        | all                              | `new` or `cancel`                                                     |
| `clOrdId`     | all                              | unique per order; cancel uses its own `clOrdId` (mirrors FIX wire)    |
| `securityId`  | all                              | numeric `SecurityID` matching an instrument loaded by the host        |
| `side`        | all                              | `buy` / `sell` (also accepted: `b`/`s`, `1`/`2`)                      |
| `qty`         | `new`                            | strictly positive integer                                             |
| `px`          | `new` (limit)                    | price mantissa (×10000 of the wire price); ignored for market orders  |
| `type`        | `new` (optional)                 | `limit` (default) or `market`                                         |
| `tif`         | `new` (optional)                 | `day` (default), `ioc`, or `fok`                                      |
| `origClOrdId` | `cancel`                         | the resting order's original `clOrdId`                                |

## Tape format (JSONL)

Each line is a self-describing JSON record. Filter with `jq`:

```bash
jq 'select(.src=="er") | {clOrdId, execType, lastQty, lastPxMantissa}' tape.jsonl
jq 'select(.src=="mcast") | {sequenceNumber, messageCount}' tape.jsonl
```

Record types:

- `{"src":"er", "execType":"new"|"trade"|"cancel"|"reject", ...}` — decoded
  ExecutionReport fields.
- `{"src":"mcast", "channel", "sequenceVersion", "sequenceNumber",
  "messageCount", "bytes":"<hex>"}` — full UMDF packet bytes plus PacketHeader
  metadata (multicast capture only; requires `--multicast`).
- `{"src":"evt", "event":"script_start"|"submit_new"|"submit_cancel"|"script_end"|"disconnect"|"aborted", ...}`
  — runner lifecycle annotations.

The `t` field is a unix-millisecond timestamp; mask it (or pipe through
`jq 'del(.t)'`) when diffing across runs.

## Limitations

This MVP is intentionally narrow:

- single TCP session (no multi-session scripts yet)
- no "diff harness" subcommand (use plain `diff` / `jq` for now)
- no FIXP authentication (host must be configured with
  `auth.requireFixpHandshake=false`)

Follow-ups will live as separate issues.

[issue-17]: https://github.com/pedrosakuma/B3MatchingPlatform/issues/17
