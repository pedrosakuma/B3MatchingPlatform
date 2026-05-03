# B3 EntryPoint — Target Architecture (Ephemeral)

Sister document to [`B3-ENTRYPOINT-COMPLIANCE.md`](B3-ENTRYPOINT-COMPLIANCE.md).
The compliance doc says **what's broken**; this doc says **how we will fix it
coherently**, so the 19+ open spec-compliance issues converge on a single
target shape instead of each landing as an isolated patch.

> **Scope note.** This document targets the **ephemeral** (in-memory) version
> of the simulator. Persistence (WAL + snapshot, recovery on restart) is
> deliberately out of scope and tracked separately in
> [Appendix A](#appendix-a--persistence-deferred-for-v2). The
> architectural boundaries proposed here are designed so persistence can be
> added later **without rework** to the gateway/core split.

> **Reading order.** §1 (Why) → §2 (Current vs. target) → §3 (Component
> split — the spine) → §4 (Domain model). §10 (Migration plan) maps the
> existing `GAP-NN` issues to phases.

---

## 1. Why this doc exists

The compliance audit surfaced 19 actionable gaps (#39–#57). Roughly 12 of
them touch shared session state (sequence counters, retransmission buffer,
keep-alive scheduler, CoD timer, session-versioned credentials, order
ownership, …). Closing each in isolation would create overlapping notions
of "session" all over `EntryPointSession`, `ChannelDispatcher`, and the
host.

Two architectural decisions made during scoping drive everything below:

1. **The simulator stops being single-tenant.** Multiple firms × multiple
   sessions per firm, each with its own credentials, throttle, CoD config.
   Session identity is independent of any single TCP connection.
2. **Order-entry edge concerns are split from the matching core.** Wire
   framing, FIXP state machine, retransmission, and authentication live in
   a dedicated **Gateway** component with a strict contract to the
   **Core**. They share no mutable state. (Mirrors B3's real deployment
   shape.)

Together these push the codebase from *"per-TCP `EntryPointSession`"* to a
proper component model with explicit session lifecycle and a typed
contract between layers.

---

## 2. Current vs. target — 30-second tour

### Current

```
TCP client ──► EntryPointSession ──► HostRouter ──► ChannelDispatcher ──► MatchingEngine
                  (TCP socket           (by SecurityID)         (in-memory book)
                   = "session"
                   = MsgSeqNum
                   = OrderOwnership target)
```

- One `EntryPointSession` per TCP connection. Conflates: TCP socket + FIXP
  identity + outbound sequence counter + per-session config.
- No FIXP handshake. Application messages flow on TCP `accept`.
- Outbound sequence is a local `_msgSeqNum` per-instance — dies with the
  socket.
- Single `EnteringFirm` from config.
- `ChannelDispatcher` holds direct references to `IEntryPointResponseChannel`
  (TCP-bound), so matching code transitively depends on the wire schema.

### Target

```
                ┌────────────────── B3.Exchange.Gateway ──────────────────┐
                │  TcpListener → TcpTransport ─┐                          │
                │                               │                          │
                │              ┌──── FixpSession (1 per logical session) ─│──┐
TCP socket ─────┤              │      • SOFH framing                       │  │
                │              │      • FIXP state machine                 │  │
                │              │      • MsgSeqNum + retx buffer (RAM)      │  │
                │              │      • Auth, throttle, CoD timer          │  │
                │              └────────────┬──────────────────────────────│  │
                │                           │ (ICoreInbound)               │  │
                └───────────────────────────│──────────────────────────────┘  │
                                            ▼                                  │
                ┌─────────────────── B3.Exchange.Core ──────────────────────┐  │
                │   HostRouter → ChannelDispatcher → MatchingEngine          │  │
                │   (1 dispatcher per UMDF channel; engine owns books)       │  │
                │                                                            │  │
                │   ─► IUmdfPacketSink ─► MulticastUdpPacketSink ─► UDP MC   │  │
                │                                                            │  │
                │   ─► IGatewayInbound.Deliver(SessionId, ExecutionEvent) ───│──┘
                └────────────────────────────────────────────────────────────┘

                    Contract (B3.Exchange.Contracts):
                      • InboundOrderCommand / CancelCommand / ModifyCommand   (Gateway → Core)
                      • ExecutionEvent / RejectEvent / BusinessRejectEvent    (Core → Gateway)
                      • SessionId, FirmId, ClOrdID  (neutral primitives, no SBE)
```

Key differences:

- **Two assemblies, two responsibility cones.** Gateway owns the wire and
  the session. Core owns matching and market-data publication.
- **No SBE in the Core.** Core consumes neutral DTOs; the SBE EntryPoint
  schema is a Gateway-only dependency.
- **`FixpSession` outlives `TcpTransport`.** A reconnect (`Establish`
  within the same `sessionVerId`) reattaches a fresh transport to the same
  session, preserving outbound seq + retx buffer.
- **Order ownership lives in the Gateway** (`OrderId → SessionId`). Core
  stamps `SessionId` on each `ExecutionEvent`; Gateway resolves the live
  `FixpSession` at delivery time.

---

## 3. Component split — Gateway / Core / Contracts

This is the architectural backbone. Three assemblies, strict contract
between them, no shared mutable state. The split is designed as if it were
an IPC boundary even though both run in-process — so a future move to a
multi-process deployment is mechanical, not a rewrite.

### 3.1 Project layout

```
src/
  B3.Exchange.Contracts/             ← NEW
    SessionId.cs
    FirmId.cs
    Commands/
      InboundOrderCommand.cs
      InboundCancelCommand.cs
      InboundModifyCommand.cs
    Events/
      ExecutionEvent.cs
      RejectEvent.cs
      BusinessRejectEvent.cs
    ICoreInbound.cs                  ← Gateway → Core
    IGatewayInbound.cs               ← Core → Gateway

  B3.Exchange.Gateway/               ← NEW (replaces B3.Exchange.Gateway)
    TcpListener.cs
    TcpTransport.cs                  ← owns Socket + send queue
    FixpSession.cs                   ← single-thread actor
    FixpStateMachine.cs
    RetransmitBuffer.cs              ← in-RAM ring per session
    SessionRegistry.cs
    FirmRegistry.cs
    OrderOwnershipMap.cs             ← OrderId → (SessionId, ClOrdId, Firm, Side, SecurityId) (per process)
    Framing/
      SofhFrameReader.cs             ← #39 (GAP-01)
      SofhFrameWriter.cs
      InboundDecoder.cs
      OutboundEncoder.cs             ← ER + reject + business-reject
    Auth/
      CredentialValidator.cs         ← #43 (GAP-05)
    GatewayHost.cs                   ← wires listener → sessions → ICoreInbound

  B3.Exchange.Core/                  ← RENAMED from B3.Exchange.Core
    HostRouter.cs
    ChannelDispatcher.cs             ← no longer holds IEntryPointResponseChannel
    InstrumentDefinitionPublisher.cs
    SnapshotRotator.cs
    Multicast/
      MulticastUdpPacketSink.cs
    CoreHost.cs                      ← exposes ICoreInbound, accepts IGatewayInbound

  B3.Exchange.Matching/              ← unchanged
  B3.Exchange.Instruments/           ← unchanged
  B3.Umdf.WireEncoder/               ← unchanged
  B3.EntryPoint.Sbe/                 ← Gateway-only consumer now
  B3.Umdf.Sbe/                       ← Core-only consumer

  B3.Exchange.Host/                  ← wires Gateway + Core together
```

### 3.2 The Gateway↔Core contract

The contract is **two interfaces and a handful of neutral DTOs**.

```csharp
// B3.Exchange.Contracts

public readonly record struct SessionId(string Value);
public readonly record struct FirmId(string Value);

public sealed record InboundOrderCommand(
    SessionId   Session,
    FirmId      Firm,
    string      ClOrdID,
    int         SecurityId,
    Side        Side,
    OrderType   Type,
    long        PriceMantissa,    // /10000
    long        Quantity,
    TimeInForce Tif,
    long        ReceivedAtNs);

public sealed record InboundCancelCommand(
    SessionId Session, FirmId Firm,
    string    ClOrdID,
    string?   OrigClOrdID,        // either this OR ExplicitOrderId
    long?     ExplicitOrderId,
    int       SecurityId,
    long      ReceivedAtNs);

public sealed record InboundModifyCommand(/* analogous */);

public sealed record ExecutionEvent(
    SessionId Session,            // routing back to originator
    long      OrderId,
    long      TradeId,
    string    ClOrdID,
    ExecType  ExecType,           // New, PartialFill, Fill, Cancelled, Replaced
    long      LastQty,
    long      LastPx,
    long      LeavesQty,
    long      CumQty,
    long      EmittedAtNs);

public sealed record RejectEvent(
    SessionId      Session,
    string         ClOrdID,
    OrdRejReason   Reason,
    string?        Text);

public sealed record BusinessRejectEvent(/* per spec §3, #50 */);

public interface ICoreInbound
{
    void Submit(InboundOrderCommand   cmd);
    void Submit(InboundCancelCommand  cmd);
    void Submit(InboundModifyCommand  cmd);
}

public interface IGatewayInbound
{
    void Deliver(ExecutionEvent       evt);
    void Deliver(RejectEvent          evt);
    void Deliver(BusinessRejectEvent  evt);
}
```

Properties of the contract:

- **No SBE types.** Neutral primitives only. Contracts assembly references
  nothing wire-specific.
- **Async by message-passing semantics.** Implementations enqueue work to
  the receiver's dispatch loop and return immediately. No callbacks, no
  shared locks across the boundary.
- **`SessionId` is the only routing key Core knows.** Core never holds a
  reference to a transport, socket, or `FixpSession` instance. It only
  produces events stamped with `SessionId`.
- **Order ownership is Gateway-side.** When Core emits an
  `ExecutionEvent`, the Gateway looks up the live `FixpSession` for that
  `SessionId`, encodes the ER frame with that session's outbound seq, and
  enqueues to its `TcpTransport`. If the session is `Suspended` (TCP gone
  but session alive), the ER is still recorded in the retx buffer and
  delivered on next `Establish`.

### 3.3 What the boundary buys us

| Benefit | How |
| --- | --- |
| Independent evolution | EntryPoint spec changes touch Gateway only; matching changes touch Core only. |
| Clean tests | Test Gateway with a fake `ICoreInbound`; test Core with a fake `IGatewayInbound`. No SBE in matching tests. |
| Future multi-process | Replace in-process `ICoreInbound` impl with an IPC proxy (Unix socket / shmem ring). DTOs are already serializable. |
| Failure isolation hooks | Gateway can disconnect a misbehaving session without touching Core; Core can pause a channel without touching sessions. |
| Mirrors B3 reality | EntryPoint gateway and matching engine are separate processes at B3. |

---

## 4. Domain model

Lives in `B3.Exchange.Gateway` (except where noted). Concrete types, not
just nouns.

### 4.1 `Firm`
Static config-loaded record. Identity for a participant (corretora).

```csharp
public sealed record Firm(
    FirmId  Id,                        // e.g. "FIRM01"
    string  Name,
    int     EnteringFirmCode);         // numeric code embedded in SBE headers
```

### 4.2 `SessionCredential`
Static config-loaded record. One per `(firm, sessionId)` pair.

```csharp
public sealed record SessionCredential(
    SessionId           SessionId,
    FirmId              Firm,
    string              AccessKey,            // dev: plain compare; prod: hashed (deferred)
    IReadOnlyList<IPNetwork>?  AllowedSourceCidrs,  // optional whitelist
    SessionPolicy       Policy);               // throttle, CoD default, etc.

public sealed record SessionPolicy(
    int  ThrottleMessagesPerSecond,
    int  KeepAliveIntervalMs,
    CancelOnDisconnectType DefaultCoD,
    int  RetransmitBufferSize);                // bounded ring
```

### 4.3 `FixpSession`
The heart of the Gateway. Single-threaded actor. **One per
`(firm, sessionId)` pair**, not per TCP connection. Survives transport
disconnect.

```csharp
public sealed class FixpSession
{
    public SessionId Id { get; }
    public FirmId    Firm { get; }
    public FixpState State { get; private set; }   // see §5
    public long      SessionVerId { get; private set; }
    public uint      OutboundSeq { get; private set; }   // next-to-send
    public uint      InboundExpectedSeq { get; private set; }

    private readonly RetransmitBuffer _retx;
    private readonly Channel<SessionWorkItem> _work;     // single dispatch loop
    private TcpTransport? _transport;                    // null when Suspended

    // From transport (after framing/decoding):
    public void OnFrame(InboundFrame frame) => _work.Writer.TryWrite(...);

    // From Core (via IGatewayInbound):
    public void Deliver(ExecutionEvent evt) => _work.Writer.TryWrite(...);

    // Lifecycle:
    public void AttachTransport(TcpTransport t);
    public void DetachTransport(DisconnectReason r);
}
```

Invariants enforced on the dispatch loop:

1. `OutboundSeq` allocation, retx-buffer append, and TcpTransport enqueue
   happen **atomically** in the same critical section (the dispatch
   thread).
2. `InboundExpectedSeq` is updated only after the frame is fully decoded
   and forwarded to `ICoreInbound`.
3. State transitions are funneled through `FixpStateMachine.Apply` — no
   ad-hoc state mutations.

### 4.4 `TcpTransport`
Owns one `Socket`. Knows nothing about FIXP semantics — just frames
bytes in (delivers `RawFrame` to attached `FixpSession`) and bytes out
(consumes a bounded send queue). Replaceable.

```csharp
public sealed class TcpTransport
{
    public TransportId Id { get; }
    public IPEndPoint  RemoteEndPoint { get; }

    public event Action<RawFrame>?    FrameReceived;
    public event Action<DisconnectReason>? Disconnected;

    public bool TryEnqueue(ReadOnlyMemory<byte> wireBytes);   // bounded; false ⇒ overflow
    public void Close(DisconnectReason r);
}
```

The lifecycle is:
1. `TcpListener` accepts → creates `TcpTransport`.
2. `TcpTransport` reads first frame (`Negotiate`) → hands to
   `SessionRegistry.Resolve(credentials)` → returns or creates a
   `FixpSession`.
3. `FixpSession.AttachTransport(transport)`. From here the transport just
   pumps bytes; the session owns semantics.

### 4.5 `SessionRegistry`
In-RAM map `SessionId → FixpSession`. Populated lazily on first
`Negotiate` for a credential present in `FirmRegistry`. Authority for
"does this session exist and is it active?".

### 4.6 `FirmRegistry`
In-RAM map `FirmId → Firm` plus `(SessionId → SessionCredential)`. Loaded
once from config at startup; immutable thereafter (for ephemeral). Future:
hot-reload via admin endpoint.

### 4.7 `OrderOwnershipMap` (per process)
`OrderId → (SessionId, ClOrdId, Firm, Side, SecurityId)`. Lives in the
Gateway as a **single per-process** instance keyed by engine-assigned
`OrderId`. Populated by `GatewayRouter` on every `ICoreOutbound.WriteExecutionReportNew`
callback (i.e. as the engine accepts a new order); evicted on terminal
events (`OnOrderCanceled`, full fill via `NotifyOrderTerminal`, and on
session close via `EvictSession`).

Used by:

- **`GatewayRouter`** to resolve the resting-side owner of passive trade
  reports (`WriteExecutionReportPassiveTrade`) and resting cancels
  (`WriteExecutionReportPassiveCancel`) — Core never sees `SessionId` for
  those events.
- **`HostRouter`** to pre-resolve `OrigClOrdID → OrderId` on inbound
  `Cancel`/`Replace` and to compute the explicit `OrderId` list for
  `MassCancel` (filtering by `(Session, Firm, Side?, SecurityId?)`) before
  enqueueing into the Core dispatcher.

Threading: backed by `ConcurrentDictionary` for both forward
(`OrderId → owner`) and reverse (`(Firm, ClOrdId) → OrderId`) indices.
Writes happen on Core dispatch threads (one writer per `OrderId` because
ID allocation is per-channel); reads happen on any FixpSession recv thread.

Suspended-state ownership (open question 5): because the map is keyed by
`SessionId` (the durable identity from the FIXP `Establish` claim) and not
by the live `FixpSession` reference, entries survive a transport drop;
when `SessionRegistry` re-binds a fresh `FixpSession` to that `SessionId`
the existing resting orders' passive ER continue routing to the new
transport.

---

## 5. State machine

`FixpSession.State` is an explicit enum; transitions are the ONLY way to
mutate state.

```
       ┌────────────┐  Negotiate(valid)         ┌──────────────┐
       │   Idle     │ ────────────────────────► │  Negotiated  │
       └────────────┘                            └──────┬───────┘
              ▲                                          │
              │ NegotiateReject                          │ Establish(valid)
              │                                          ▼
              │                                  ┌──────────────┐
              │                                  │ Established  │◄──┐
              │                                  └──────┬───────┘   │
              │                                         │           │ Establish (re-attach
              │                          TCP loss /     │           │  same sessionVerId)
              │                          Terminate      ▼           │
              │                                  ┌──────────────┐   │
              │                                  │  Suspended   │ ──┘
              │                                  │ (transport   │
              │                                  │  detached;   │
              │                                  │  retx held)  │
              │                                  └──────┬───────┘
              │                                         │ Terminate /
              │                                         │ session-end /
              │                                         │ daily reset
              │                                         ▼
              │                                  ┌──────────────┐
              └────────────────────────────────  │  Terminated  │
                                                 └──────────────┘
```

**Acceptance matrix** (subset; full table goes in code XML doc):

| State | Negotiate | Establish | Application msg | Sequence | Terminate | RetransmitRequest |
| --- | --- | --- | --- | --- | --- | --- |
| Idle | ✓ → Negotiated / NegotiateReject | NotApplied | Reject + Terminate (UNNEGOTIATED) | – | ✓ | – |
| Negotiated | NegotiateReject(ALREADY_NEGOTIATED) | ✓ → Established / EstablishReject | Reject + Terminate (UNESTABLISHED) | NotApplied | ✓ | – |
| Established | NegotiateReject | EstablishReject | ✓ | ✓ | ✓ → Terminated | ✓ (replay from retx) |
| Suspended | NegotiateReject(ALREADY_NEGOTIATED) | ✓ → Established (re-attach) | – (no transport) | – | ✓ → Terminated | – (deferred to re-establish) |
| Terminated | terminal | terminal | terminal | terminal | terminal | terminal |

Closes #42 (GAP-04), #43 (GAP-05), #44 (GAP-06 keep-alive flows from
this).

---

## 6. Threading model

| Thread / loop | Owner | Responsibilities |
| --- | --- | --- |
| `TcpListener` accept loop | Gateway | Accept → wrap in `TcpTransport` → handoff. |
| `TcpTransport` recv loop | Gateway, 1/transport | Read socket, SOFH-frame, raise `FrameReceived`. **Does not decode SBE.** |
| `TcpTransport` send loop | Gateway, 1/transport | Drain send queue → write socket. Bounded queue with `DropWrite` → on overflow, request session `Terminate`. |
| `FixpSession` dispatch loop | Gateway, 1/session | Owns ALL session state mutation: state-machine transitions, decode-then-forward, allocate `OutboundSeq` + retx-append + transport-enqueue (atomic triple), retx replay, throttle accounting. |
| `ChannelDispatcher` work loop | Core, 1/channel | Unchanged. Single-thread per `SecurityID`-channel. |
| `SnapshotRotator` timer | Core, 1/channel | Unchanged. |
| Keep-alive scheduler | Gateway, 1/session (timer or piggy-backed on dispatch loop) | Emits `Sequence` heartbeats, monitors inbound silence → `Terminate`. |
| Cancel-on-Disconnect timer | Gateway, 1/session | On `Detach`, schedules cancel-all after `cod.gracePeriodMs`; cancelled if `Establish` re-attaches in time. |

**Critical guarantee:** the `(allocate seq → retx append → transport
enqueue)` triple is on the FixpSession dispatch loop and is the ONLY path
that writes outbound. There is no other producer for outbound seq numbers.

---

## 7. Authentication (ephemeral)

Simplified for the in-memory phase; structured to upgrade later.

- **Credential format on the wire** (per spec §4.5.2): `Negotiate.credentials`
  is a JSON blob `{"auth_type":"basic","username":"<sessionId>","access_key":"<token>"}`.
- **Validation** (`CredentialValidator`):
  1. `username` exists in `SessionRegistry` and matches a
     `SessionCredential`.
  2. `access_key` matches stored value (plain string compare in dev).
  3. Source IP is in `AllowedSourceCidrs` if specified.
  4. `sessionVerId` is strictly greater than the last seen for this
     session (in-memory counter; fresh after restart).
  5. Session is not already `Established` with a different transport
     (else `NegotiateReject(ALREADY_NEGOTIATED, currentSessionVerId)`).
- **No per-message auth.** Once `Established`, the session is trusted
  until `Terminated`.
- **Dev mode flag** (`auth.devMode: true` in config) bypasses access_key
  check entirely; logs a warning on startup. Useful for tests and local
  exploration.
- **Future (deferred):** bcrypt-hashed access keys at rest, hot-reload of
  credentials via admin endpoint, optional TLS. Listed in
  [Appendix A](#appendix-a--persistence-deferred-for-v2).

---

## 8. Configuration schema

Extends today's `exchange-simulator.json`. Old single-tenant `tcp` block
is replaced.

```jsonc
{
  "tcp": {
    "listen": "0.0.0.0:9876",
    "maxConcurrentTransports": 64
  },
  "auth": {
    "devMode": false
  },
  "firms": [
    {
      "id": "FIRM01",
      "name": "Acme Corretora",
      "enteringFirmCode": 8
    }
  ],
  "sessions": [
    {
      "sessionId": "SESS-001",
      "firmId": "FIRM01",
      "accessKey": "dev-shared-secret",
      "allowedSourceCidrs": ["127.0.0.1/32", "10.0.0.0/8"],
      "policy": {
        "throttleMessagesPerSecond": 200,
        "keepAliveIntervalMs": 1000,
        "defaultCancelOnDisconnect": "SESSION_END",
        "retransmitBufferSize": 10000
      }
    }
  ],
  "channels": [
    /* unchanged from today: per-UMDF-channel multicast group + instruments */
  ]
}
```

Validation at startup:
- Every `sessions[].firmId` resolves against `firms[]`.
- `sessionId` values are unique.
- `auth.devMode=true` AND any `sessions[].accessKey` is non-empty → log a
  warning ("dev mode bypasses access_key").

---

## 9. Operator surface

For the ephemeral phase, the existing HTTP server (`HostConfig.http`)
gains read-only diagnostics:

- `GET /sessions` — list all known sessions: `id`, `firmId`, `state`,
  `sessionVerId`, `outboundSeq`, `inboundExpectedSeq`, `retxBufferDepth`,
  `attachedTransportId?`, `lastActivityAtMs`.
- `GET /sessions/{id}` — same, single session.
- `GET /firms` — list firms.
- Existing `/health/*` and `/metrics` continue to work; metrics gain
  per-session counters (`fixp_session_outbound_messages_total{session_id}`,
  `fixp_session_retx_replay_total`, `fixp_session_state{session_id}`,
  etc.).

Mutating endpoints (force-terminate, reset session, etc.) are **deferred**
to v2 — they imply persistence semantics we don't want to commit to yet.

---

## 10. Migration plan

Five phases. Each phase is mergeable on its own — simulator continues to
work end-to-end after every phase. Each phase closes a known set of
GAP-NN issues.

### Phase 0 — Wire framing hardening (no architecture changes)
- Add SOFH (12-byte total header) — #39 (GAP-01).
- Consume variable-length data after SBE fixed block — #40 (GAP-02).
- Enforce `messageLength ≤ 512` — #41 (GAP-03 part 1).
- Send `Terminate` on framing/decoding errors — #41 (GAP-03 part 2).

Touches `EntryPointFrameReader`, `EntryPointSession.Close()`. Single PR,
small surface. Lands first because it's prerequisite for any wire-level
testing of later phases.

### Phase 1 — Component split (the big one)
- Create `B3.Exchange.Contracts` with DTOs and interfaces (§3.2).
- Create `B3.Exchange.Gateway`; move all of `B3.Exchange.Gateway`
  into it; rename old `EntryPointSession` to `LegacyTcpSession` and
  carve out `TcpTransport` + `FixpSession` skeletons.
- Rename `B3.Exchange.Core` → `B3.Exchange.Core`. Strip all
  references to `IEntryPointResponseChannel` from `ChannelDispatcher`;
  replace with `IGatewayInbound` from contracts.
- Wire both via `B3.Exchange.Host` using in-process implementations of
  the interfaces.
- Move `OrderOwnershipMap` from Core to Gateway; Core now stamps
  `SessionId` on every event.

No new spec compliance closed in this phase, but **all subsequent phases
become cleanly localized** to the Gateway.

### Phase 2 — FIXP session lifecycle
- `SessionRegistry` + `FirmRegistry` from config — closes #8 (multi-firm)
  by construction.
- `FixpStateMachine` enum + transition matrix.
- `Negotiate` / `NegotiateResponse` / `NegotiateReject` — #42 (GAP-04).
- `Establish` / `EstablishAck` / `EstablishReject` — #43 (GAP-05).
- `CredentialValidator` (dev mode + plain compare) — #43.
- `Terminate` flow + `TerminationCode` enum — extends Phase 0.

### Phase 3 — Reliable delivery (in-memory)
- `RetransmitBuffer` (bounded ring per session) — #43 (GAP-08).
- `Sequence` keep-alive heartbeats both directions — #44 (GAP-06).
- `NotApplied` on inbound gap — #42 (GAP-07).
- `RetransmitRequest` handling: replay from retx buffer, set `PossResend`
  flag — #43 (GAP-08), GAP-13.
- Suspended ↔ Established re-attach via re-`Establish`.

### Phase 4 — Operational hardening
- Cancel-on-Disconnect timer — #50 (GAP-18).
- Daily reset (in-memory): drain → bump `SessionVerId` → reset seqs →
  resume — #44 (GAP-09).
- Throttle enforcement — #52 (GAP-20).
- `BusinessMessageReject` for malformed application messages — #50
  (GAP-14).
- Improve `OrdRejReason` mapping — #49 (GAP-17 / fixes the all-zeros
  stub).
- Operator HTTP endpoints (§9).

### Closure summary

| Phase | Issues closed |
| --- | --- |
| 0 | #39, #40, #41 |
| 1 | (none directly; enables) |
| 2 | #8, #42, #43, #16 (supersede) |
| 3 | #42, #43, #44 |
| 4 | #49, #50, #51, #52 + #11 (supersede), #9 (supersede) |

Issues not in any phase are tracked separately (e.g. STP / cross-prevention
in #14 → matching-side, scheduled independently).

---

## 11. Test strategy

Three test surfaces, mirroring the component split.

### 11.1 Gateway unit tests (`tests/B3.Exchange.Gateway.Tests`)
- `FixpStateMachine` transition matrix (one test per cell).
- `RetransmitBuffer`: append/replay correctness, ring eviction, replay
  with `PossResend` flag, request beyond retained range → reject.
- `CredentialValidator`: each rejection branch.
- `SofhFrameReader`: malformed frames produce specific `Terminate`
  codes.
- `FixpSession` dispatch loop: under concurrent inbound from transport
  + outbound from Core, outbound seq is gap-free and retx contents
  match send order.

### 11.2 Core unit tests (`tests/B3.Exchange.Core.Tests`)
- ChannelDispatcher tests now use a fake `IGatewayInbound` collector
  (no SBE in test code).
- Existing matching tests continue unchanged (no Gateway dependency).

### 11.3 End-to-end tests (`tests/B3.Exchange.E2E.Tests`)
- Spin up `Host` in-process bound to ephemeral port; drive a real TCP
  client speaking SBE + SOFH against it.
- Scenarios: full session lifecycle, gap recovery, CoD trigger, daily
  reset mid-session, throttle reject.
- Use `SbeB3UmdfConsumer` style decoder to verify multicast output.

---

## 12. Open questions

These are deliberately deferred — flagged so we don't paint ourselves
into a corner.

1. **Single `FixpSession` instance vs. one per `(sessionId, sessionVerId)`?**
   Spec lets a session be re-negotiated (new `sessionVerId`) on the same
   `sessionId`. Current proposal: one instance, mutable
   `SessionVerId`, retx buffer cleared on `sessionVerId` bump. Simpler.
2. **Gateway↔Core back-pressure.** If Core is slow, Gateway's calls into
   `ICoreInbound.Submit` should never block the FixpSession dispatch
   loop. Proposal: Core's implementation is "enqueue to ChannelDispatcher
   bounded queue; if full, return failure → Gateway emits
   `BusinessMessageReject(SYSTEM_BUSY)` to the originator". Confirm in
   Phase 1.
3. **Multiple TCP transports for one session.** Spec implies at most one
   active transport per `sessionId`. Concurrent `Negotiate` from a
   second source while one is `Established` → reject second. Confirm
   exact spec wording in Phase 2.
4. **Daily reset coordination.** When a daily reset happens, does the
   Core need to coordinate (drain channels) before sessions clear seqs?
   Proposal: Core reset and Gateway reset are independent events
   triggered on the same wall-clock; drain happens at each loop's own
   pace. Confirm in Phase 4.
5. **Should `OrderOwnershipMap` survive `Suspended` state?** **Resolved
   (#66).** Yes — the Gateway map is keyed by the durable `SessionId`
   (from the FIXP `Establish` claim), not by the live `FixpSession`
   reference, so entries persist while a session is `Suspended` and
   continue to route passive ER once a fresh transport re-binds the same
   `SessionId` via `SessionRegistry`. Eviction on terminal events bounds
   the map; explicit `EvictSession(SessionId)` runs only on transport
   `OnSessionClosed`, releasing back-references without cancelling the
   resting orders themselves.

---

## Appendix A — Persistence (deferred for v2)

Out of scope for the ephemeral architecture above. Captured here so the
boundaries set in §3 remain compatible with future durability.

**Sketch:** WAL + snapshot, event-sourced, per channel and per session.

- **Channel WAL**: tee of UMDF Inc_* frames + inbound EntryPoint frames,
  ordered by wall-clock + journal seq. Mirrors the snapshot+incremental
  pattern UMDF already uses for consumers.
- **Session WAL**: per-session lifecycle events + outbound ER frames +
  keep-alive beats. Enables retx buffer to survive process restart.
- **Snapshots**: periodic + on clean shutdown + at daily reset. Snapshot
  payload reuses UMDF snapshot frame serialization where applicable.
- **Recovery**: load latest snapshot, replay WAL forward (do NOT re-emit
  to wire), reopen listeners, clients reconnect with fresh `Establish`.
- **`fsync` policy**: configurable (`every-batch` default, `every-event`,
  `periodic`).

The `B3.Exchange.Core` and `B3.Exchange.Gateway` boundaries in §3 are
unchanged by this — persistence is added via a `IWalAppender` interface
injected into both, with separate WAL files per concern. The Contracts
assembly is unaffected.

Other items deferred to v2:
- Bcrypt-hashed `accessKey` at rest.
- Hot-reload of `firms` / `sessions` / `instruments` via admin
  endpoints.
- Mutating operator endpoints (force-terminate, reset session).
- Optional TLS between client and gateway.
- Multi-process Gateway/Core deployment (the §3 contract makes this
  mechanical; deferring the operational work).

---

## Upgrading vendored SBE schemas

The two SBE XML schemas live under `schemas/`:

- `schemas/b3-entrypoint-messages-8.4.2.xml` — inbound EntryPoint
  protocol; consumed by `src/B3.EntryPoint.Sbe/B3.EntryPoint.Sbe.csproj`.
- `schemas/b3-market-data-messages-2.2.0.xml` — outbound UMDF protocol;
  consumed by `src/B3.Umdf.Sbe/B3.Umdf.Sbe.csproj`.

Both `.csproj` files use the `SbeSourceGenerator` package to regenerate
strongly-typed C# bindings from the XML at build time (the generated
files land under `generated/` per project; `EmitCompilerGeneratedFiles`
is on so they are inspectable).

**EntryPoint and UMDF version independently.** Each schema is bumped on
its own cadence; you do not need to upgrade both at once. The companion
[`SbeB3UmdfConsumer`](https://github.com/pedrosakuma/SbeB3UmdfConsumer)
repo vendors the same UMDF schema and must be kept in lock-step when the
UMDF version moves.

### Upgrade checklist

1. **Drop the new XML** into `schemas/` (replacing or alongside the old
   file).
2. **Update the `.csproj`** that references the schema:
   ```xml
   <AdditionalFiles Include="..\..\schemas\b3-entrypoint-messages-X.Y.Z.xml" />
   ```
   Bump the path to point at the new file. Remove the old `<AdditionalFiles>`
   entry once you are confident the new bindings work.
3. **Rebuild** the affected `.Sbe` project. The source generator will
   regenerate `generated/*.cs`. Inspect the diff (`git status` will not
   show generated files because they are gitignored, but you can read
   them directly under `src/B3.EntryPoint.Sbe/generated/` or
   `src/B3.Umdf.Sbe/generated/`).
4. **Run the wire-level tests** to surface schema-shape regressions:
   ```bash
   dotnet test tests/B3.EntryPoint.Wire.Tests   -c Release
   dotnet test tests/B3.Umdf.WireEncoder.Tests  -c Release
   ```
5. **Reconcile codecs.** Hand-written codecs in
   `src/B3.EntryPoint.Wire/` and `src/B3.Umdf.WireEncoder/` reference
   generated types directly. Renamed fields, removed templates, or
   changed block lengths will surface as build failures here — fix
   them.
6. **Run the full suite** (`dotnet test SbeB3Exchange.slnx -c Release`).
   New or changed templates may need additional Gateway tests (decoder
   coverage) or Core tests (engine routing).
7. **Update compliance docs.**
   [`B3-ENTRYPOINT-COMPLIANCE.md`](./B3-ENTRYPOINT-COMPLIANCE.md) lists
   the version we target and per-template gap status; bump the version
   and audit any newly added or removed templates.
8. **Mirror UMDF changes** in
   [`SbeB3UmdfConsumer`](https://github.com/pedrosakuma/SbeB3UmdfConsumer)
   when bumping the market-data schema; the consumer is what proves the
   wire is end-to-end correct.

If the upgrade introduces breaking changes that downstream OMS / market
data consumers cannot absorb in lock-step, gate the new version behind a
configuration switch and keep the previous bindings in `generated/` for
one release cycle.
