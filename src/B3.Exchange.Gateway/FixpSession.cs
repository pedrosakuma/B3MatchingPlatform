using B3.EntryPoint.Wire;
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
}
