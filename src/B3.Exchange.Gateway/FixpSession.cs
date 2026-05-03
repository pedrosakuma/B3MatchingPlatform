using System.Buffers;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;

namespace B3.Exchange.Gateway;

/// <summary>
/// Per-connection FIXP session: owns session identity (<see cref="SessionId"/>,
/// <see cref="EnteringFirm"/>), the inbound decode loop, the FIXP-style
/// liveness watchdog, and the encoders for outbound ExecutionReport
/// frames. Hands the actual byte-level send/receive of frames to a
/// composed <see cref="TcpTransport"/>.
///
/// <para>The session is the destination for the
/// <see cref="IInboundCommandSink"/> dispatch — Core never holds a
/// transport reference. Future Phase-2 work (FIXP state machine,
/// authentication, retransmission) will plug into this same surface.</para>
///
/// <para>Stream-based (rather than Socket-bound) so it can be exercised
/// in tests with <c>MemoryStream</c> or duplex pipes — see
/// <see cref="EntryPointListener"/> for the production accept loop.</para>
/// </summary>
public sealed partial class FixpSession : IAsyncDisposable
{
    private const int InboundHeaderSize = EntryPointFrameReader.WireHeaderSize;
    private const int DefaultSendQueueCapacity = 1024;

    private TcpTransport _transport;
    private readonly IInboundCommandSink _sink;
    private readonly ILogger<FixpSession> _logger;
    private readonly ILogger<TcpTransport> _transportLogger;
    private readonly int _sendQueueCapacity;
    private CancellationTokenSource _cts = new();
    private readonly Func<ulong> _nowNanos;
    private readonly FixpSessionOptions _options;
    private readonly Action<FixpSession, string>? _onClosed;
    private readonly NegotiationValidator? _validator;
    private readonly EstablishValidator? _establishValidator;
    private readonly SessionClaimRegistry? _claims;
    /// <summary>
    /// Serializes lifecycle transitions: <see cref="Suspend"/>,
    /// <see cref="Close"/>, and <see cref="TryReattach"/> all hold this
    /// lock so a concurrent reaper-driven Close can't race a peer-driven
    /// rebind. The dispatch / receive / watchdog hot paths do NOT take
    /// the lock; they operate on a captured snapshot of
    /// <see cref="_transport"/> / <see cref="_cts"/> and tolerate the
    /// snapshot becoming stale across a re-attach (the old transport's
    /// Close will exit their loops naturally; new loops are started by
    /// <see cref="TryReattach"/> bound to the new snapshot).
    /// </summary>
    private readonly object _attachLock = new();
    /// <summary>
    /// Serializes outbound business-frame production (sequence-number
    /// allocation + retransmit-buffer append + send-queue enqueue) and
    /// retransmission replay blocks. Held by every live business write
    /// (<see cref="WriteExecutionReportNew"/> &amp;c.) for the entire
    /// allocate-encode-buffer-enqueue triplet, and held by
    /// <see cref="ProcessAndEnqueueRetransmitRequest"/> for the entire
    /// header + N replay clones + trailing Sequence block. This
    /// guarantees (a) no live frame interleaves the replay block on the
    /// wire and (b) the trailing Sequence's <c>nextSeqNo</c> matches
    /// the next live seq the peer will see (issue #46, gpt-5.5
    /// critique).
    /// </summary>
    private readonly object _outboundLock = new();
    /// <summary>
    /// Per-session retransmission ring buffer (issue #46, spec §4.5.6).
    /// Holds copies of the wire frames for ExecutionReport_* and
    /// BusinessMessageReject — the buffered business templates that
    /// carry an <c>OutboundBusinessHeader.MsgSeqNum</c>. Not preserved
    /// across <see cref="Close"/>; preserved across
    /// <see cref="Suspend"/> / <see cref="TryReattach"/> so a new
    /// transport can recover via <c>RetransmitRequest</c>.
    /// </summary>
    private readonly RetransmitBuffer _retxBuffer;
    private readonly FixpOutboundEncoder _outboundEncoder;
    private readonly FixpRetransmitController _retransmitController;
    /// <summary>The wire SessionID currently claimed in <see cref="_claims"/>,
    /// or 0 if no claim is held. Tracked so <see cref="Close"/> can
    /// release the claim using the value from the moment the claim was
    /// taken (the public <see cref="SessionId"/> may be re-stamped on a
    /// subsequent rejected Negotiate that fails after we've already
    /// claimed).</summary>
    private uint _claimedSessionId;
    private long _msgSeqNum;
    private int _isOpen = 1;
    private int _isAttached = 1;
    // Watchdog state (milliseconds since process start, monotonic).
    private long _lastInboundMs;
    // Set true while we are waiting on the grace window after sending a probe;
    // cleared as soon as any inbound frame arrives. Prevents flooding probes.
    private int _probeOutstanding;
    private Task? _recvTask;
    private Task? _watchdogTask;
    private readonly Func<long> _nowMs;
    /// <summary>
    /// Per-session inbound sliding-window throttle (issue #56 / GAP-20).
    /// Null when both <see cref="FixpSessionOptions.ThrottleTimeWindowMs"/>
    /// and <see cref="FixpSessionOptions.ThrottleMaxMessages"/> are 0
    /// (throttling disabled). Mutated only on the receive thread.
    /// </summary>
    private readonly InboundThrottle? _throttle;

    public long ConnectionId { get; }
    /// <summary>
    /// Numeric firm code (B3 EnteringFirm). Initially set from
    /// <c>identityFactory</c> at accept time as a placeholder; rewritten
    /// to the resolved <see cref="Firm.EnteringFirmCode"/> after a
    /// successful FIXP <c>Negotiate</c> handshake (#42).
    /// </summary>
    public uint EnteringFirm { get; private set; }
    /// <summary>
    /// FIXP wire SessionID (uint32). Set from <c>identityFactory</c> at
    /// accept time as a placeholder; rewritten to the value claimed by
    /// the peer after a successful Negotiate.
    /// </summary>
    public uint SessionId { get; private set; }
    /// <summary>FIXP <c>sessionVerID</c> recorded on the most recent
    /// successful Negotiate. Zero before the handshake has completed.</summary>
    public ulong SessionVerId { get; private set; }

    /// <summary>Negotiated <c>keepAliveInterval</c> (ms) committed by the
    /// most recent successful Establish. Zero before Establish completes.
    /// Set inside the dispatch thread; read by the watchdog.</summary>
    public long KeepAliveIntervalMs { get; private set; }

    /// <summary>Highest inbound application <c>MsgSeqNum</c> accepted on
    /// this session. Zero until the first inbound application message is
    /// processed. Used to populate <c>EstablishAck.lastIncomingSeqNo</c>
    /// and to compute <c>EstablishReject.lastIncomingSeqNo</c>.</summary>
    public uint LastIncomingSeqNo { get; private set; }
    /// <summary>Stable, transport-neutral identity of this session as seen
    /// by Core / Contracts. Routing key the Gateway uses to resolve
    /// outbound ExecutionReports back to this <see cref="FixpSession"/>.
    /// Derived from <see cref="ConnectionId"/> until Phase 2 introduces a
    /// <c>SessionRegistry</c> backed by authentication.</summary>
    public ContractsSessionId Identity { get; }
    public bool IsOpen => Volatile.Read(ref _isOpen) == 1 && _transport.IsOpen;

    /// <summary>
    /// Wall-clock-ish (Environment.TickCount64) timestamp of the moment the
    /// session entered <see cref="FixpState.Suspended"/>, or <c>null</c> if
    /// the session is not currently Suspended. Read by the listener's
    /// suspended-session reaper to decide whether the session has lingered
    /// past <see cref="FixpSessionOptions.SuspendedTimeoutMs"/>.
    /// Set on the dispatch thread inside <see cref="Suspend"/>; cleared
    /// inside <see cref="Close"/>. Reads use <c>Volatile.Read</c>.
    /// </summary>
    public long? SuspendedSinceMs
    {
        get
        {
            var v = Volatile.Read(ref _suspendedSinceMs);
            return v == 0 ? null : v;
        }
    }
    private long _suspendedSinceMs;

    /// <summary>
    /// True while the session has a live <see cref="TcpTransport"/> attached.
    /// Set to false on transport-driven disconnect (issue #69). The session
    /// itself remains in the registry — see <see cref="State"/>; only re-attach
    /// (issue #69b) brings this back to true. Distinct from
    /// <see cref="IsOpen"/>, which also goes false on terminal Close.
    /// </summary>
    public bool IsAttached => Volatile.Read(ref _isAttached) == 1;

    /// <summary>
    /// Current FIXP lifecycle state. Mutated only via <see cref="ApplyTransition"/>,
    /// which routes through <see cref="FixpStateMachine.Apply"/>. Starts at
    /// <see cref="FixpState.Idle"/> immediately after the TCP transport is
    /// accepted (before any FIXP <c>Negotiate</c>).
    /// </summary>
    public FixpState State { get; private set; } = FixpState.Idle;

    /// <summary>
    /// Single entry point for state transitions. Looks up the next state and
    /// recommended action via <see cref="FixpStateMachine.Apply"/>, applies
    /// the new state, and returns the action so the dispatch loop can act
    /// on it (write a response frame, terminate, etc.). Idempotent against
    /// <see cref="FixpState.Terminated"/>.
    /// </summary>
    internal FixpAction ApplyTransition(FixpEvent ev)
    {
        var prev = State;
        var t = FixpStateMachine.Apply(prev, ev);
        State = t.NewState;
        var metrics = _options.LifecycleMetrics;
        if (metrics != null && t.Action == FixpAction.Accept && prev != t.NewState)
        {
            switch (t.NewState)
            {
                case FixpState.Established:
                    metrics.IncEstablished();
                    if (prev == FixpState.Suspended) metrics.IncRebound();
                    break;
                case FixpState.Suspended:
                    metrics.IncSuspended();
                    break;
            }
        }
        // Reset the inbound throttle window on every transition INTO
        // Established (initial Establish + rebind via #69b) so a freshly
        // (re-)established session starts with a clean budget. Counter
        // totals on _throttle (lifetime accept/reject) are preserved.
        if (t.Action == FixpAction.Accept && prev != t.NewState
            && t.NewState == FixpState.Established)
        {
            _throttle?.Reset();
        }
        return t.Action;
    }

    /// <summary>
    /// Approximate number of pre-encoded ExecutionReport frames sitting
    /// in the outbound queue, for /metrics scraping.
    /// </summary>
    public int SendQueueDepth => _transport.SendQueueDepth;

    /// <summary>Number of in-buffer business frames available to replay
    /// in response to a <c>RetransmitRequest</c>. Diagnostic only.</summary>
    public int RetxBufferDepth => _retxBuffer.Count;

    /// <summary>Last allocated outbound MsgSeqNum (the value the next
    /// emitted business frame's <c>NextMsgSeqNum()</c> call will return
    /// is <c>OutboundSeq + 1</c>). Diagnostic only.</summary>
    public uint OutboundSeq => (uint)Volatile.Read(ref _msgSeqNum);

    /// <summary>Unix epoch time (ms) of the most recent inbound frame
    /// (including session-layer Sequence/heartbeats). Zero if nothing has
    /// been received yet. Distinct from <c>_lastInboundMs</c>, which is
    /// monotonic ticks used by the watchdog. Diagnostic only.</summary>
    public long LastActivityAtMs => Volatile.Read(ref _lastInboundUnixMs);
    private long _lastInboundUnixMs;

    /// <summary>True while the session is registered with the gateway —
    /// covers Established AND Suspended states. Distinct from
    /// <see cref="IsOpen"/>, which also requires a live transport and
    /// therefore goes false during Suspended. Used by diagnostics
    /// providers (e.g. <c>/sessions</c>) so suspended sessions remain
    /// observable. Diagnostic only.</summary>
    public bool IsRegistered => Volatile.Read(ref _isOpen) == 1;

    /// <summary>Stable handle for the currently-attached TCP transport,
    /// or <c>null</c> when the session is Suspended (no attached
    /// transport). Format <c>"tx-&lt;hex(connectionId)&gt;"</c>.
    /// Diagnostic only.</summary>
    public string? AttachedTransportId => IsAttached
        ? "tx-" + ConnectionId.ToString("x", System.Globalization.CultureInfo.InvariantCulture)
        : null;

    /// <summary>
    /// Cancel-on-Disconnect criterion claimed by the most recent
    /// successful <c>Establish</c> (issue #54 / GAP-18, spec §4.7).
    /// Defaults to
    /// <c>DO_NOT_CANCEL_ON_DISCONNECT_OR_TERMINATE</c> until Establish
    /// completes. Set on the dispatch thread inside
    /// <see cref="ProcessEstablish"/>; read by
    /// <see cref="SuspendLocked"/> and <see cref="StopCodTimer"/>.
    /// </summary>
    public B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType CancelOnDisconnectType { get; private set; }
        = B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType.DO_NOT_CANCEL_ON_DISCONNECT_OR_TERMINATE;

    /// <summary>
    /// Cancel-on-Disconnect grace window (ms) committed at Establish.
    /// Spec range: <c>[0, 60000]</c>; out-of-range values are clamped on
    /// ingest. Zero means "fire CoD as soon as the trigger is detected".
    /// </summary>
    public long CodTimeoutWindowMs { get; private set; }

    /// <summary>FIXP spec §4.7 maximum CoD grace window (ms).</summary>
    internal const long MaxCodTimeoutWindowMs = 60_000L;

    /// <summary>
    /// Test seam: stamp the cancel-on-disconnect parameters that would
    /// normally be set inside <see cref="ProcessEstablish"/>. Mirrors the
    /// pattern used by <c>ApplyTransition</c> in the suspend/reattach
    /// tests so suites can drive CoD scheduling without building a full
    /// Establish frame. Intentionally <c>internal</c>.
    /// </summary>
    internal void SetCancelOnDisconnectForTest(
        B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType type,
        long codTimeoutWindowMs)
    {
        CancelOnDisconnectType = type;
        if (codTimeoutWindowMs < 0) codTimeoutWindowMs = 0;
        if (codTimeoutWindowMs > MaxCodTimeoutWindowMs) codTimeoutWindowMs = MaxCodTimeoutWindowMs;
        CodTimeoutWindowMs = codTimeoutWindowMs;
    }

    /// <summary>
    /// Active CoD timer scheduled in <see cref="SuspendLocked"/> when the
    /// session is configured for cancel-on-disconnect; non-null only
    /// while the session is Suspended and the grace window has not yet
    /// elapsed. Owned by <see cref="_attachLock"/>.
    /// </summary>
    private Timer? _codTimer;

    public FixpSession(long connectionId, uint enteringFirm, uint sessionId,
        Stream stream, IInboundCommandSink sink, ILogger<FixpSession> logger,
        Func<ulong>? nowNanos = null,
        int sendQueueCapacity = DefaultSendQueueCapacity,
        FixpSessionOptions? options = null,
        Action<FixpSession, string>? onClosed = null,
        ILogger<TcpTransport>? transportLogger = null,
        NegotiationValidator? negotiationValidator = null,
        SessionClaimRegistry? sessionClaims = null,
        EstablishValidator? establishValidator = null,
        Func<long>? nowMs = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ConnectionId = connectionId;
        EnteringFirm = enteringFirm;
        SessionId = sessionId;
        Identity = new ContractsSessionId("conn-" + connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _sink = sink;
        _logger = logger;
        _transportLogger = transportLogger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TcpTransport>.Instance;
        _sendQueueCapacity = sendQueueCapacity;
        _nowNanos = nowNanos ?? DefaultNowNanos;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _options = options ?? FixpSessionOptions.Default;
        _options.Validate();
        _retxBuffer = new RetransmitBuffer(_options.RetransmitBufferCapacity);
        _outboundEncoder = new FixpOutboundEncoder(
            sessionId: () => SessionId,
            nextMsgSeqNum: NextMsgSeqNum,
            transport: () => _transport!,
            retxBuffer: _retxBuffer,
            outboundLock: _outboundLock,
            nowNanos: () => _nowNanos(),
            isOpen: () => IsOpen,
            close: Close);
        _retransmitController = new FixpRetransmitController(
            sessionId: () => SessionId,
            transport: () => _transport!,
            retxBuffer: _retxBuffer,
            outboundLock: _outboundLock,
            sendQueueCapacity: _sendQueueCapacity,
            isOpen: () => IsOpen,
            peekNextMsgSeqNum: PeekNextMsgSeqNum,
            applyTransition: ApplyTransition,
            getState: () => State,
            logger: _logger,
            connectionId: ConnectionId);
        if (_options.ThrottleMaxMessages > 0 && _options.ThrottleTimeWindowMs > 0)
        {
            _throttle = new InboundThrottle(
                _options.ThrottleMaxMessages,
                _options.ThrottleTimeWindowMs,
                _nowMs);
        }
        _onClosed = onClosed;
        _validator = negotiationValidator;
        _establishValidator = establishValidator;
        _claims = sessionClaims;
        if (negotiationValidator is not null && sessionClaims is null)
            throw new ArgumentException(
                "sessionClaims is required when negotiationValidator is supplied",
                nameof(sessionClaims));
        // The transport's onClose callback funnels back through our
        // OnTransportClosed handler so transport-driven teardown can be
        // demoted to a Suspend (preserving the FIXP session) when we are
        // in Established state per spec §4.5 (issue #69).
        _transport = CreateBoundTransport(stream);
        Volatile.Write(ref _lastInboundMs, NowMs());
        Volatile.Write(ref _lastInboundUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Builds a <see cref="TcpTransport"/> whose <c>onClose</c> callback
    /// captures the transport instance and forwards it to
    /// <see cref="OnTransportClosed(string, TcpTransport?)"/> so a stale
    /// callback from a previous transport generation (post-rebind, see
    /// <see cref="TryReattach"/>) is ignored instead of tearing down the
    /// freshly attached session.
    /// </summary>
    private TcpTransport CreateBoundTransport(Stream stream)
    {
        TcpTransport? bound = null;
        bound = new TcpTransport(ConnectionId, stream, _transportLogger, _sendQueueCapacity,
            onClose: reason => OnTransportClosed(reason, bound));
        return bound;
    }

    public void Start()
    {
        _logger.LogInformation("fixp session opened: connectionId={ConnectionId} sessionId={SessionId} firm={EnteringFirm}",
            ConnectionId, SessionId, EnteringFirm);
        _transport.StartSendLoop(_cts.Token);
        _recvTask = Task.Run(() => RunReceiveLoopAsync(_cts.Token));
        _watchdogTask = Task.Run(() => RunWatchdogLoopAsync(_cts.Token));
    }

    private static ulong DefaultNowNanos()
        => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;

    private static long NowMs() => Environment.TickCount64;


    private uint NextMsgSeqNum() => (uint)Interlocked.Increment(ref _msgSeqNum);

    /// <summary>Peek the value <see cref="NextMsgSeqNum"/> would return on
    /// its next call without consuming a sequence number. Used by FIXP
    /// <c>Sequence</c> frames, which announce but do not consume.</summary>
    private uint PeekNextMsgSeqNum() => (uint)(Volatile.Read(ref _msgSeqNum) + 1);

    /// <summary>
    /// Decodes and processes a FIXP <c>RetransmitRequest</c> (template
    /// id 12), per spec §4.5.6 and issue #46. Runs on the dispatch
    /// (recv-loop) thread.
    ///
    /// <para>State machine routing (already encoded in
    /// <see cref="FixpStateMachine"/>): <c>Established → Replay</c>;
    /// <c>Suspended → DeferredToReestablish</c> (silently drop); other
    /// states → <c>DropSilently</c>. Validation order before replay:
    /// <c>SessionID</c> match, timestamp non-zero, count bounds, in-flight
    /// gate (CAS), then the buffer's window check (<c>OUT_OF_RANGE</c>
    /// vs <c>INVALID_FROMSEQNO</c>).</para>
    ///
    /// <para>On accept: encodes a <c>Retransmission</c> header with
    /// <c>nextSeqNo = request.fromSeqNo</c> and <c>count = actual</c>,
    /// enqueues all replay clones (each carrying the
    /// <c>PossResend</c> bit), and finally enqueues a <c>Sequence</c>
    /// frame whose <c>nextSeqNo</c> is the next live business seq
    /// (i.e. <see cref="PeekNextMsgSeqNum"/>). The entire block is
    /// enqueued under <see cref="_outboundLock"/> so live business
    /// writes cannot interleave it on the wire and so the trailing
    /// Sequence's seq matches what the peer will see next.</para>
    /// </summary>
    private void ProcessAndEnqueueRetransmitRequest(ReadOnlySpan<byte> fixedBlock)
        => _retransmitController.ProcessAndEnqueueRetransmitRequest(fixedBlock);

    public bool WriteExecutionReportNew(in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue)
        => _outboundEncoder.WriteExecutionReportNew(e, receivedTimeNanos);

    public bool WriteExecutionReportTrade(in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty)
        => _outboundEncoder.WriteExecutionReportTrade(e, isAggressor, ownerOrderId, clOrdIdValue, leavesQty, cumQty);

    public bool WriteExecutionReportCancel(in OrderCanceledEvent e, ulong clOrdIdValue, ulong origClOrdIdValue,
        ulong receivedTimeNanos = ulong.MaxValue)
        => _outboundEncoder.WriteExecutionReportCancel(e, clOrdIdValue, origClOrdIdValue, receivedTimeNanos);

    public bool WriteExecutionReportModify(long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue,
        Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq,
        ulong receivedTimeNanos = ulong.MaxValue)
        => _outboundEncoder.WriteExecutionReportModify(securityId, orderId, clOrdIdValue, origClOrdIdValue,
            side, newPriceMantissa, newRemainingQty, transactTimeNanos, rptSeq, receivedTimeNanos);

    /// <summary>
    /// Encodes and enqueues an <c>OrderMassActionReport</c> (template 702,
    /// spec §4.8 / #GAP-19) acknowledging — or rejecting — an inbound
    /// <c>OrderMassActionRequest</c>. The report is sent ahead of the
    /// per-order <c>ExecutionReport_Cancel</c> messages that the engine
    /// emits asynchronously for the matching resting orders.
    /// </summary>
    public bool WriteOrderMassActionReport(ulong clOrdIdValue, byte massActionResponse,
        byte? massActionRejectReason, byte? side, long securityId, ulong transactTimeNanos,
        string? text = null)
        => _outboundEncoder.WriteOrderMassActionReport(clOrdIdValue, massActionResponse,
            massActionRejectReason, side, securityId, transactTimeNanos, text);

    public bool WriteExecutionReportReject(in RejectEvent e, ulong clOrdIdValue)
        => _outboundEncoder.WriteExecutionReportReject(e, clOrdIdValue);

    public bool WriteSessionReject(byte terminationCode)
        => _outboundEncoder.WriteSessionReject(terminationCode);

    public bool WriteBusinessMessageReject(byte refMsgType, uint refSeqNum, ulong businessRejectRefId,
        uint businessRejectReason, string? text = null)
        => _outboundEncoder.WriteBusinessMessageReject(refMsgType, refSeqNum, businessRejectRefId,
            businessRejectReason, text);

    /// <summary>
    /// Maps engine <see cref="RejectReason"/> to the FIX OrdRejReason wire code
    /// emitted on ExecutionReport_Reject. See
    /// <see cref="FixpOutboundEncoder.MapRejectReason"/> for the full mapping
    /// table; this property is kept on <see cref="FixpSession"/> as an
    /// internal stable reference for tests that pre-date the #121 refactor.
    /// </summary>
    internal static uint MapRejectReason(RejectReason r) => FixpOutboundEncoder.MapRejectReason(r);

    public void Close() => Close("close");

    private void OnTransportClosed(string reason) => OnTransportClosed(reason, originatingTransport: null);

    /// <summary>
    /// Routes a transport-side teardown into either a session-preserving
    /// <see cref="Suspend"/> (when we are <see cref="FixpState.Established"/>
    /// per spec §4.5 / issue #69) or a full <see cref="Close"/>. Idempotent
    /// — multiple callers (transport callback, receive-loop finally, watchdog)
    /// converge here without repeating side effects.
    ///
    /// <para><paramref name="originatingTransport"/> identifies the transport
    /// instance whose teardown triggered this call. After a re-attach
    /// (#69b-2) the OLD transport's recv-loop / send-loop / onClose
    /// callbacks may still be in flight; if those fire after the new
    /// transport is installed, they would otherwise erroneously close or
    /// suspend the freshly attached session. We compare against the
    /// session's current <c>_transport</c> snapshot and drop the stale
    /// callback. <c>null</c> means "from an unknown source"; treated as
    /// authoritative for backward compatibility.</para>
    /// </summary>
    private void OnTransportClosed(string reason, TcpTransport? originatingTransport)
    {
        if (Volatile.Read(ref _isOpen) == 0) return;
        // Decision must run under _attachLock so the generation check and
        // the actual lifecycle transition can't be split by a concurrent
        // TryReattach. Without the lock a stale callback could pass the
        // generation check, get preempted while the new transport is
        // installed, then close the new generation.
        lock (_attachLock)
        {
            if (Volatile.Read(ref _isOpen) == 0) return;
            // Stale callback from a prior transport generation — ignore.
            if (originatingTransport is not null && !ReferenceEquals(originatingTransport, _transport))
                return;
            // Re-entrancy guard: Suspend() itself calls _transport.Close(...),
            // which will fire this callback a second time. Once we have detached
            // (or are about to detach), defer to whichever branch already took
            // ownership rather than escalating to a full Close.
            if (Volatile.Read(ref _isAttached) == 0) return;
            if (State == FixpState.Established)
            {
                SuspendLocked(reason);
                return;
            }
            CloseLocked(reason);
        }
    }

    /// <summary>
    /// Demote an <see cref="FixpState.Established"/> session to
    /// <see cref="FixpState.Suspended"/> after the underlying TCP transport
    /// has dropped (spec §4.5, issue #69). The session remains registered in
    /// the <see cref="SessionRegistry"/> and retains its claim so that a
    /// subsequent <c>Establish</c> on a brand-new transport (issue #69b) can
    /// re-attach without re-Negotiate. The dispatch / watchdog loops are
    /// stopped via <c>_cts.Cancel()</c>; a future re-attach will start fresh
    /// loops bound to the new transport.
    /// </summary>
    public void Suspend(string reason)
    {
        lock (_attachLock)
        {
            SuspendLocked(reason);
        }
    }

    /// <summary>
    /// Body of <see cref="Suspend(string)"/>; assumes the caller already
    /// holds <see cref="_attachLock"/>. Idempotent — second caller is a
    /// no-op via the <see cref="_isAttached"/> CAS.
    /// </summary>
    private void SuspendLocked(string reason)
    {
        // Atomically flip the attachment flag; second caller is a no-op.
        if (Interlocked.Exchange(ref _isAttached, 0) == 0) return;
        _logger.LogInformation("fixp session {ConnectionId} suspending (reason={Reason})",
            ConnectionId, reason);
        var action = ApplyTransition(FixpEvent.Detach);
        if (action != FixpAction.Accept || State != FixpState.Suspended)
        {
            _logger.LogInformation(
                "fixp session {ConnectionId} suspend declined (state={State} action={Action}); closing instead",
                ConnectionId, State, action);
            CloseLocked(reason);
            return;
        }
        try { _cts.Cancel(); } catch { }
        try { _transport.Close(reason); } catch { }
        try { _transport.Stream.Dispose(); } catch { }
        Volatile.Write(ref _suspendedSinceMs, NowMs());
        ScheduleCodTimerLocked();
    }

    /// <summary>
    /// Persist the cancel-on-disconnect knobs claimed by the peer's
    /// <c>Establish</c> frame (issue #54 / GAP-18, spec §4.7). Called on
    /// the recv thread inside <see cref="ProcessEstablish"/>; values are
    /// only consumed from <see cref="SuspendLocked"/> on the same path,
    /// so no synchronization is required.
    /// </summary>
    private void ApplyCodEstablishParams(in EstablishRequest req)
    {
        // The SBE field is a 1-byte enum followed by 1 byte of padding;
        // EstablishDecoder reads it as ushort for layout convenience.
        // Narrow to the underlying byte before casting to the enum.
        CancelOnDisconnectType =
            (B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType)(byte)req.CancelOnDisconnectType;
        var window = (long)Math.Min(req.CodTimeoutWindowMillis, (ulong)MaxCodTimeoutWindowMs);
        if (window < 0) window = 0;
        CodTimeoutWindowMs = window;
    }

    /// <summary>
    /// Returns true when the session's Establish-time
    /// <see cref="CancelOnDisconnectType"/> covers the
    /// transport-disconnect trigger (modes <c>1</c> and <c>3</c>). The
    /// explicit-Terminate trigger (modes <c>2</c> and <c>3</c>) is
    /// deferred until inbound peer-Terminate framing is wired.
    /// </summary>
    private bool CodCoversDisconnect()
    {
        var t = CancelOnDisconnectType;
        return t == B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType.CANCEL_ON_DISCONNECT_ONLY
            || t == B3.Entrypoint.Fixp.Sbe.V6.CancelOnDisconnectType.CANCEL_ON_DISCONNECT_OR_TERMINATE;
    }

    /// <summary>
    /// Schedules the one-shot cancel-on-disconnect timer for this
    /// session. Caller MUST hold <see cref="_attachLock"/>. No-op when
    /// CoD is disabled or already armed for this Suspend cycle.
    /// </summary>
    private void ScheduleCodTimerLocked()
    {
        if (_codTimer is not null) return;
        if (!CodCoversDisconnect()) return;
        var window = CodTimeoutWindowMs;
        if (window < 0) window = 0;
        _logger.LogInformation(
            "fixp session {ConnectionId} arming cancel-on-disconnect (mode={Mode} windowMs={WindowMs})",
            ConnectionId, CancelOnDisconnectType, window);
        // Capture the current generation indirectly: the callback will
        // re-acquire _attachLock and re-check that the timer it sees in
        // the field is still the same instance and that we are still in
        // Suspended. That handshake is sufficient to avoid firing on a
        // session that has since reattached or closed.
        Timer? created = null;
        created = new Timer(_ => OnCodTimerFired(created!), null,
            dueTime: window, period: Timeout.Infinite);
        _codTimer = created;
    }

    /// <summary>
    /// Cancels and disposes any armed cancel-on-disconnect timer. Caller
    /// MUST hold <see cref="_attachLock"/>. Idempotent.
    /// </summary>
    private void StopCodTimerLocked()
    {
        var t = _codTimer;
        if (t is null) return;
        _codTimer = null;
        try { t.Dispose(); } catch { }
    }

    /// <summary>
    /// One-shot CoD timer callback. Runs on a thread-pool thread, so it
    /// re-acquires <see cref="_attachLock"/> to verify the session is
    /// still Suspended and that the firing timer is still the one we
    /// armed (i.e. no concurrent reattach/close). On commit, enqueues a
    /// session-scoped mass-cancel through the gateway-side ownership map
    /// (see <see cref="IInboundCommandSink.EnqueueMassCancel"/>) and
    /// bumps the lifecycle counter.
    /// </summary>
    private void OnCodTimerFired(Timer originating)
    {
        bool fire;
        lock (_attachLock)
        {
            if (!ReferenceEquals(_codTimer, originating)) return;
            _codTimer = null;
            // Only fire if still Suspended and not Closed. Reattach
            // flips to Established before this callback can re-enter the
            // lock, so the State check covers the rebind race.
            fire = Volatile.Read(ref _isOpen) == 1 && State == FixpState.Suspended;
        }
        try { originating.Dispose(); } catch { }
        if (!fire) return;

        // SecurityId=0 + SideFilter=null ⇒ "every resting order owned by
        // this session" — HostRouter resolves the actual order list via
        // OrderOwnershipMap.FilterMassCancel(session, firm, null, 0).
        var cmd = new MassCancelCommand(
            SecurityId: 0,
            SideFilter: null,
            EnteredAtNanos: _nowNanos());
        _logger.LogInformation(
            "fixp session {ConnectionId} cancel-on-disconnect firing (mode={Mode} windowMs={WindowMs})",
            ConnectionId, CancelOnDisconnectType, CodTimeoutWindowMs);
        try { _sink.EnqueueMassCancel(cmd, Identity, EnteringFirm); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "fixp session {ConnectionId} cancel-on-disconnect: EnqueueMassCancel threw",
                ConnectionId);
            return;
        }
        try { _options.LifecycleMetrics?.IncCancelOnDisconnectFired(); } catch { }
    }

    /// <summary>
    /// Closes the session with a diagnostic reason. The reason is forwarded to
    /// the optional <c>onClosed</c> callback supplied at construction so the
    /// listener (or tests) can log it. <see cref="Close()"/> is the
    /// no-reason convenience overload.
    /// </summary>
    /// <summary>
    /// Generation-aware close: closes the session only if
    /// <paramref name="originatingTransport"/> is still the current
    /// transport. Used by background loops (watchdog) that may have
    /// raced past their cancellation token after a re-attach (#69b-2).
    /// </summary>
    private void CloseIfTransportCurrent(string reason, TcpTransport originatingTransport)
    {
        lock (_attachLock)
        {
            if (!ReferenceEquals(_transport, originatingTransport)) return;
            CloseLocked(reason);
        }
    }

    public void Close(string reason)
    {
        lock (_attachLock)
        {
            CloseLocked(reason);
        }
    }

    /// <summary>
    /// Body of <see cref="Close(string)"/>; assumes the caller already
    /// holds <see cref="_attachLock"/>. Called directly from
    /// <see cref="Suspend"/> when the state-machine refuses to demote
    /// (so we don't try to re-acquire the lock recursively, and so the
    /// transition from "trying to suspend" to "actually closing" is
    /// atomic from the perspective of a concurrent re-attach).
    /// </summary>
    private void CloseLocked(string reason)
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0) return;
        Volatile.Write(ref _isAttached, 0);
        // Clear the suspended-since timestamp so a concurrent reaper poll
        // doesn't try to re-Close a session that's already terminal.
        Volatile.Write(ref _suspendedSinceMs, 0);
        StopCodTimerLocked();
        _logger.LogInformation("fixp session {ConnectionId} closing", ConnectionId);
        try { _cts.Cancel(); } catch { }
        // The transport may have already closed (it's the source of the
        // callback in IO-error paths). Either way, ensure it's down so the
        // send loop wakes up and exits.
        _transport.Close(reason);
        // Release the FIXP session claim (if any) so the same sessionID
        // can be re-negotiated by a future transport.
        if (_claimedSessionId != 0 && _claims is not null)
        {
            try { _claims.Release(_claimedSessionId, this); } catch { }
            _claimedSessionId = 0;
        }
        // Notify the engine sink so it releases any cached references to this
        // session (see IInboundCommandSink.OnSessionClosed). Without this the
        // ChannelDispatcher's order-owners map keeps the session rooted for
        // the lifetime of every resting order it placed → unbounded memory.
        try { _sink.OnSessionClosed(Identity); } catch { }
        try { _onClosed?.Invoke(this, reason); } catch { }
    }

    /// <summary>
    /// Re-attach a freshly accepted transport to this Suspended FIXP
    /// session (issue #69b-2). Called by <see cref="EntryPointListener"/>
    /// after it has parsed the new connection's first frame and
    /// determined that it is an <c>Establish</c> targeting our
    /// <see cref="SessionId"/> with a matching <see cref="SessionVerId"/>.
    /// The buffered first frame must be replayable through
    /// <paramref name="rebindStream"/> (e.g. a <see cref="PrependedStream"/>);
    /// the new receive loop will read it, drive
    /// <see cref="ProcessEstablish"/>, send the <c>EstablishAck</c> and
    /// transition the state machine Suspended → Established.
    ///
    /// <para>Returns <c>false</c> (without disposing
    /// <paramref name="rebindStream"/>) when the session is no longer
    /// re-attachable (closed, never suspended, or another re-attach won
    /// the race). The caller is responsible for closing the new socket
    /// in that case.</para>
    /// </summary>
    public bool TryReattach(Stream rebindStream)
    {
        ArgumentNullException.ThrowIfNull(rebindStream);
        // Snapshot the previous loop tasks so we can JOIN with them
        // before installing the new transport. This guarantees that any
        // in-flight dispatch from the old receive loop (which still
        // reads `_transport`) has fully run to completion before a new
        // generation can be observed — eliminating the race in which an
        // old dispatch path's continuation would close or write to the
        // freshly attached transport.
        Task? oldRecv;
        Task? oldWatchdog;
        lock (_attachLock)
        {
            if (Volatile.Read(ref _isOpen) == 0) return false;
            if (Volatile.Read(ref _isAttached) == 1) return false;
            if (State != FixpState.Suspended) return false;
            oldRecv = _recvTask;
            oldWatchdog = _watchdogTask;
        }
        // Drain old loops outside the lock to avoid holding _attachLock
        // across an await of arbitrary duration. Loops were already
        // cancelled (and the old stream disposed) in Suspend, so this
        // join should complete promptly. Bound by a short timeout as a
        // belt-and-suspenders against runaway tasks; on timeout we skip
        // the attempt and let the caller close the new socket — the
        // suspended-session reaper will eventually clean up.
        if (oldRecv is not null)
        {
            try { if (!oldRecv.Wait(TimeSpan.FromSeconds(2))) return false; } catch { }
        }
        if (oldWatchdog is not null)
        {
            try { if (!oldWatchdog.Wait(TimeSpan.FromSeconds(2))) return false; } catch { }
        }
        lock (_attachLock)
        {
            // Re-validate after the unlock-await-relock window: a
            // concurrent Close (e.g. reaper) may have terminated the
            // session while we waited.
            if (Volatile.Read(ref _isOpen) == 0) return false;
            if (Volatile.Read(ref _isAttached) == 1) return false;
            if (State != FixpState.Suspended) return false;

            _logger.LogInformation(
                "fixp session {ConnectionId} re-attaching transport (sessionId={SessionId} sessionVerId={SessionVerId})",
                ConnectionId, SessionId, SessionVerId);

            try { _cts.Dispose(); } catch { }
            _cts = new CancellationTokenSource();
            _transport = CreateBoundTransport(rebindStream);
            // The new transport is back; cancel-on-disconnect grace is
            // satisfied (issue #54 spec §4.7: reconnect inside the
            // window cancels the pending CoD trigger).
            StopCodTimerLocked();
            Volatile.Write(ref _suspendedSinceMs, 0);
            Volatile.Write(ref _lastInboundMs, NowMs());
            Volatile.Write(ref _isAttached, 1);

            // Spin up new send / recv / watchdog loops bound to the fresh
            // transport + cancellation source. The buffered Establish frame
            // already inside `rebindStream` will flow through the recv loop
            // and drive ProcessEstablish → state machine Suspended → Established.
            _transport.StartSendLoop(_cts.Token);
            _recvTask = Task.Run(() => RunReceiveLoopAsync(_cts.Token));
            _watchdogTask = Task.Run(() => RunWatchdogLoopAsync(_cts.Token));
        }
        return true;
    }

    /// <summary>
    /// Atomic suspended-session reap (issue #69b-2): if this session is
    /// still in <see cref="FixpState.Suspended"/> and its
    /// <see cref="SuspendedSinceMs"/> is at or before
    /// <paramref name="thresholdMs"/>, fully closes the session and
    /// returns <c>true</c>. Otherwise leaves the session untouched and
    /// returns <c>false</c>. Holds <see cref="_attachLock"/> for the
    /// duration so a concurrent <see cref="TryReattach"/> cannot race
    /// in between the snapshot the reaper made and the close decision.
    /// </summary>
    internal bool TryReapIfSuspended(long thresholdMs)
    {
        lock (_attachLock)
        {
            if (Volatile.Read(ref _isOpen) == 0) return false;
            if (State != FixpState.Suspended) return false;
            var since = Volatile.Read(ref _suspendedSinceMs);
            if (since == 0) return false;
            if (since > thresholdMs) return false;
            CloseLocked("suspended-timeout");
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close("dispose");
        try { if (_recvTask != null) await _recvTask.ConfigureAwait(false); } catch { }
        try { if (_watchdogTask != null) await _watchdogTask.ConfigureAwait(false); } catch { }
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
