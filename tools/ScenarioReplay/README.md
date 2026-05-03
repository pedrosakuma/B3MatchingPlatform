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

single-session connection (legacy, no FIXP handshake):
  --host <ip|hostname>      EntryPoint host (default 127.0.0.1)
  --port <n>                EntryPoint port (default 9876)

multi-session connection (FIXP handshake per session):
  --session <spec>          Repeatable. Spec: name=sessionId:firm[@host:port]
                            e.g. firmA=100:1@127.0.0.1:9876
                            The first --session is the default target for
                            events that omit "session".
  --access-key <name>=<key> Repeatable. Access key for a named session;
                            default is empty (works in auth.devMode=true).

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
| `session`     | optional (multi-session only)    | session name (matches a `--session` `name=...` arg). When omitted, the event is routed to the first declared session (or the legacy single client). |

## Multi-session example

```bash
# Host config: two firms (codes 1 + 2) and two FIXP sessions (100, 200)
# under auth.requireFixpHandshake=true.
dotnet run --project tools/ScenarioReplay -- \
    --session firmA=100:1 --session firmB=200:2 \
    --script crossing.jsonl \
    --out tape.jsonl
```

```jsonl
# crossing.jsonl: firmA posts a resting bid; firmB sweeps it.
{"atMs": 0,  "session": "firmA", "kind": "new",  "clOrdId": 1, "securityId": 900000000001, "side": "buy",  "type": "limit", "tif": "day", "qty": 100, "px": 320000}
{"atMs": 50, "session": "firmB", "kind": "new",  "clOrdId": 1, "securityId": 900000000001, "side": "sell", "type": "limit", "tif": "ioc", "qty": 100, "px": 320000}
```

clOrdId reuse across sessions is fine — the gateway namespaces resting
orders by `(firm, clOrdId)` and the replay runner keeps a per-session
`clOrdId → orderId` map for cancel resolution.

## Tape format (JSONL)

Each line is a self-describing JSON record. Filter with `jq`:

```bash
jq 'select(.src=="er") | {clOrdId, execType, lastQty, lastPxMantissa}' tape.jsonl
jq 'select(.src=="mcast") | {sequenceNumber, messageCount}' tape.jsonl
```

Record types:

- `{"src":"er", "session":"<name>", "execType":"new"|"trade"|"cancel"|"reject", ...}` — decoded
  ExecutionReport fields. `session` is the originating session name.
- `{"src":"mcast", "channel", "sequenceVersion", "sequenceNumber",
  "messageCount", "bytes":"<hex>"}` — full UMDF packet bytes plus PacketHeader
  metadata (multicast capture only; requires `--multicast`).
- `{"src":"evt", "session":"<name>", "event":"script_start"|"submit_new"|"submit_cancel"|"script_end"|"disconnect"|"aborted", ...}`
  — runner lifecycle annotations. `session` is null on script-wide events.

The `t` field is a unix-millisecond timestamp; mask it (or pipe through
`jq 'del(.t)'`) when diffing across runs.

## Limitations

This MVP is intentionally narrow:

- single TCP connection per session (no automatic reconnect on drop)
- the `diff` subcommand compares JSON top-level fields only; UMDF
  multicast records are matched on full hex-equality of the packet
  bytes (semantic decoding of inner SBE messages is a follow-up).

Follow-ups will live as separate issues.

## Regression diffing

```bash
# 1. Capture a baseline tape from a known-good build:
dotnet run --project tools/ScenarioReplay -- \
    --script my_scenario.jsonl --out golden.jsonl

# 2. On each PR, replay the same script and diff:
dotnet run --project tools/ScenarioReplay -- \
    --script my_scenario.jsonl --out run.jsonl

dotnet run --project tools/ScenarioReplay -- diff \
    --baseline golden.jsonl --candidate run.jsonl
echo $?  # 0=equivalent, 1=diverged, 2=malformed/usage
```

Default ignored fields are `t` (wall-clock timestamp) and `sendingTime`
(currently inside packet bytes; reserved for future structured-mcast
diffs). Add more with `--ignore`:

```bash
# Tolerate engine-allocated orderId (not deterministic across runs):
dotnet run --project tools/ScenarioReplay -- diff \
    --baseline golden.jsonl --candidate run.jsonl \
    --ignore orderId,leavesQty
```

The diff stops scanning at the first record-level divergence (printed
side-by-side) but counts total `modified` / `onlyInBaseline` /
`onlyInCandidate` across the whole pair so a CI summary line carries
the impact at a glance.

[issue-17]: https://github.com/pedrosakuma/B3MatchingPlatform/issues/17
