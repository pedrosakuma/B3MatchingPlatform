namespace B3.Exchange.Gateway;

/// <summary>
/// FIXP per-session lifecycle state. The session starts in <see cref="Idle"/>
/// when a TCP connection is accepted, transitions through Negotiate /
/// Establish, and ends in <see cref="Terminated"/> when the session ends.
/// <para>
/// See <c>docs/B3-ENTRYPOINT-ARCHITECTURE.md</c> §5 for the full diagram and
/// acceptance matrix. The single source of truth for transitions is
/// <see cref="FixpStateMachine.Apply"/>; <c>FixpSession.State</c> must only
/// ever be mutated through that method.
/// </para>
/// </summary>
public enum FixpState
{
    /// <summary>
    /// TCP transport is open; no <c>Negotiate</c> received yet. Application
    /// frames in this state must be rejected with <c>Terminate(Unnegotiated)</c>.
    /// </summary>
    Idle,
    /// <summary>
    /// <c>Negotiate</c> accepted. Awaiting <c>Establish</c>. Application
    /// frames in this state must be rejected with <c>Terminate(Unestablished)</c>.
    /// </summary>
    Negotiated,
    /// <summary>
    /// Fully established session. Application messages flow normally; keep-
    /// alive (<c>Sequence</c>) and retransmission requests are accepted.
    /// </summary>
    Established,
    /// <summary>
    /// Transport is detached but the session lifecycle is still alive (retx
    /// buffer held). A subsequent <c>Establish</c> with the same
    /// <c>sessionVerID</c> re-attaches and returns to <see cref="Established"/>.
    /// </summary>
    Suspended,
    /// <summary>
    /// Terminal state. No further events are processed; the session is reaped
    /// from the registry once its dispatch loop drains.
    /// </summary>
    Terminated,
}

/// <summary>
/// Inputs to <see cref="FixpStateMachine.Apply"/>. Combines inbound message
/// kinds (decoded by the transport layer) and out-of-band lifecycle events
/// (<see cref="Detach"/> for TCP loss).
/// </summary>
public enum FixpEvent
{
    Negotiate,
    Establish,
    /// <summary>
    /// Any FIX/SBE application message (NewOrder, Cancel, ExecutionReport,
    /// etc.). Only legal in <see cref="FixpState.Established"/>.
    /// </summary>
    ApplicationMessage,
    /// <summary>FIXP <c>Sequence</c> (heartbeat/seq carrier).</summary>
    Sequence,
    /// <summary>Inbound or outbound <c>Terminate</c>.</summary>
    Terminate,
    /// <summary>FIXP <c>RetransmitRequest</c>.</summary>
    RetransmitRequest,
    /// <summary>
    /// Out-of-band: TCP transport detached (socket closed, not from a
    /// <c>Terminate</c> exchange). Maps Established → Suspended; in pre-
    /// established states the session is reaped.
    /// </summary>
    Detach,
}

/// <summary>
/// Outcome describing both the new state and the action the dispatch loop
/// must take in response. The state machine itself is pure; the FixpSession
/// dispatch loop is responsible for performing the action (writing the
/// response frame, closing the transport, etc.).
/// </summary>
public enum FixpAction
{
    /// <summary>Frame accepted; nothing to send beyond normal application
    /// processing.</summary>
    Accept,
    /// <summary>Reject with <c>NegotiateReject(AlreadyNegotiated)</c>.</summary>
    NegotiateRejectAlreadyNegotiated,
    /// <summary>Reject with <c>NegotiateReject</c> (generic — duplicate
    /// negotiate after Establish).</summary>
    NegotiateReject,
    /// <summary>Reject with <c>EstablishReject</c> (duplicate establish).</summary>
    EstablishReject,
    /// <summary>FIXP <c>NotApplied</c> response (sequence-tracking message
    /// arrived in a state where seq tracking is not active).</summary>
    NotApplied,
    /// <summary>Application message in <see cref="FixpState.Idle"/> — must
    /// reject + send <c>Terminate(Unnegotiated)</c>.</summary>
    RejectAndTerminateUnnegotiated,
    /// <summary>Application message in <see cref="FixpState.Negotiated"/> —
    /// must reject + send <c>Terminate(Unestablished)</c>.</summary>
    RejectAndTerminateUnestablished,
    /// <summary>Discard frame silently (e.g., RetransmitRequest in
    /// <see cref="FixpState.Idle"/>).</summary>
    DropSilently,
    /// <summary>Replay missing messages from the retx buffer (RetransmitRequest
    /// in <see cref="FixpState.Established"/>).</summary>
    Replay,
    /// <summary>Defer handling until the session is re-established (RetransmitRequest
    /// in <see cref="FixpState.Suspended"/>).</summary>
    DeferredToReestablish,
    /// <summary>Terminal state was already reached; ignore the event.</summary>
    Terminal,
}

/// <summary>Pure transition result.</summary>
public readonly record struct FixpTransition(FixpState NewState, FixpAction Action);

/// <summary>
/// Pure (state, event) → (state', action) function table. No I/O, no
/// allocation, no logging — this is the single source of truth for the FIXP
/// session lifecycle. See <c>docs/B3-ENTRYPOINT-ARCHITECTURE.md</c> §5 for
/// the matrix this implements.
/// </summary>
public static class FixpStateMachine
{
    public static FixpTransition Apply(FixpState state, FixpEvent ev) => (state, ev) switch
    {
        // ── Idle ──────────────────────────────────────────────────────────
        (FixpState.Idle, FixpEvent.Negotiate)          => new(FixpState.Negotiated, FixpAction.Accept),
        (FixpState.Idle, FixpEvent.Establish)          => new(FixpState.Idle, FixpAction.NotApplied),
        (FixpState.Idle, FixpEvent.ApplicationMessage) => new(FixpState.Terminated, FixpAction.RejectAndTerminateUnnegotiated),
        (FixpState.Idle, FixpEvent.Sequence)           => new(FixpState.Idle, FixpAction.DropSilently),
        (FixpState.Idle, FixpEvent.Terminate)          => new(FixpState.Terminated, FixpAction.Accept),
        (FixpState.Idle, FixpEvent.RetransmitRequest)  => new(FixpState.Idle, FixpAction.DropSilently),
        (FixpState.Idle, FixpEvent.Detach)             => new(FixpState.Terminated, FixpAction.Accept),

        // ── Negotiated ────────────────────────────────────────────────────
        (FixpState.Negotiated, FixpEvent.Negotiate)          => new(FixpState.Negotiated, FixpAction.NegotiateRejectAlreadyNegotiated),
        (FixpState.Negotiated, FixpEvent.Establish)          => new(FixpState.Established, FixpAction.Accept),
        (FixpState.Negotiated, FixpEvent.ApplicationMessage) => new(FixpState.Terminated, FixpAction.RejectAndTerminateUnestablished),
        (FixpState.Negotiated, FixpEvent.Sequence)           => new(FixpState.Negotiated, FixpAction.NotApplied),
        (FixpState.Negotiated, FixpEvent.Terminate)          => new(FixpState.Terminated, FixpAction.Accept),
        (FixpState.Negotiated, FixpEvent.RetransmitRequest)  => new(FixpState.Negotiated, FixpAction.DropSilently),
        (FixpState.Negotiated, FixpEvent.Detach)             => new(FixpState.Terminated, FixpAction.Accept),

        // ── Established ───────────────────────────────────────────────────
        (FixpState.Established, FixpEvent.Negotiate)          => new(FixpState.Established, FixpAction.NegotiateReject),
        (FixpState.Established, FixpEvent.Establish)          => new(FixpState.Established, FixpAction.EstablishReject),
        (FixpState.Established, FixpEvent.ApplicationMessage) => new(FixpState.Established, FixpAction.Accept),
        (FixpState.Established, FixpEvent.Sequence)           => new(FixpState.Established, FixpAction.Accept),
        (FixpState.Established, FixpEvent.Terminate)          => new(FixpState.Terminated, FixpAction.Accept),
        (FixpState.Established, FixpEvent.RetransmitRequest)  => new(FixpState.Established, FixpAction.Replay),
        (FixpState.Established, FixpEvent.Detach)             => new(FixpState.Suspended, FixpAction.Accept),

        // ── Suspended (transport detached; session lifecycle alive) ───────
        (FixpState.Suspended, FixpEvent.Negotiate)          => new(FixpState.Suspended, FixpAction.NegotiateRejectAlreadyNegotiated),
        (FixpState.Suspended, FixpEvent.Establish)          => new(FixpState.Established, FixpAction.Accept), // re-attach
        (FixpState.Suspended, FixpEvent.ApplicationMessage) => new(FixpState.Suspended, FixpAction.DropSilently),
        (FixpState.Suspended, FixpEvent.Sequence)           => new(FixpState.Suspended, FixpAction.DropSilently),
        (FixpState.Suspended, FixpEvent.Terminate)          => new(FixpState.Terminated, FixpAction.Accept),
        (FixpState.Suspended, FixpEvent.RetransmitRequest)  => new(FixpState.Suspended, FixpAction.DeferredToReestablish),
        (FixpState.Suspended, FixpEvent.Detach)             => new(FixpState.Suspended, FixpAction.DropSilently),

        // ── Terminated (terminal) ─────────────────────────────────────────
        (FixpState.Terminated, _) => new(FixpState.Terminated, FixpAction.Terminal),

        _ => throw new InvalidOperationException($"Unhandled FIXP transition ({state}, {ev})"),
    };
}
