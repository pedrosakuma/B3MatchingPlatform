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
public sealed class FixpSession : IAsyncDisposable
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
    /// <summary>0 / 1 — set via <see cref="Interlocked.CompareExchange"/>
    /// for the duration of a single retransmission replay (per spec
    /// §4.5.6: "one outstanding request at a time"). Cleared after the
    /// last frame of the replay block is enqueued under
    /// <see cref="_outboundLock"/>.</summary>
    private int _retxInProgress;
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

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        // Capture the transport this loop is bound to so a stale callback
        // after a re-attach (#69b-2) can be detected and ignored: a new
        // recv loop bound to a fresh transport will be the only owner of
        // the close decision for the new transport.
        var ownTransport = _transport;
        var stream = ownTransport.Stream;
        var headerBuf = new byte[InboundHeaderSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadExactlyAsync(stream, headerBuf, ct).ConfigureAwait(false);
                if (!EntryPointFrameReader.TryParseInboundHeader(headerBuf, out var info, out var headerError, out var hdrMsg))
                {
                    _sink.OnDecodeError(Identity, hdrMsg ?? headerError.ToString());
                    await TerminateAndCloseAsync(MapHeaderErrorToTerminationCode(headerError), $"decode-error:{headerError}").ConfigureAwait(false);
                    return;
                }
                var bodyBuf = ArrayPool<byte>.Shared.Rent(info.BodyLength);
                try
                {
                    await ReadExactlyAsync(stream, bodyBuf.AsMemory(0, info.BodyLength), ct).ConfigureAwait(false);
                    // Any well-framed inbound frame counts as liveness, including
                    // session-layer Sequence (heartbeat) frames.
                    Volatile.Write(ref _lastInboundMs, NowMs());
                    Volatile.Write(ref _lastInboundUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    Volatile.Write(ref _probeOutstanding, 0);
                    if (!await DispatchInboundAsync(info, bodyBuf, info.BodyLength).ConfigureAwait(false))
                    {
                        // DispatchInboundAsync already wrote Terminate + closed.
                        return;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bodyBuf);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "fixp session {ConnectionId} receive IO error", ConnectionId);
        }
        finally
        {
            // Transport-side EOF: defer to the central handler so the
            // Established → Suspended demotion (issue #69) is observed
            // identically to a transport-callback-driven teardown. Pass
            // the captured transport so a stale callback from a previous
            // generation (post-rebind) is filtered out by
            // OnTransportClosed.
            OnTransportClosed("recv-eof", ownTransport);
        }
    }

    /// <summary>
    /// Maps a header-parse <see cref="EntryPointFrameReader.HeaderError"/>
    /// to the FIXP <c>TerminationCode</c> the gateway must send before
    /// dropping the connection (spec §4.5.7 / §4.10).
    /// </summary>
    internal static byte MapHeaderErrorToTerminationCode(EntryPointFrameReader.HeaderError error) => error switch
    {
        EntryPointFrameReader.HeaderError.InvalidSofhEncodingType => SessionRejectEncoder.TerminationCode.InvalidSofh,
        EntryPointFrameReader.HeaderError.InvalidSofhMessageLength => SessionRejectEncoder.TerminationCode.InvalidSofh,
        EntryPointFrameReader.HeaderError.UnsupportedTemplate => SessionRejectEncoder.TerminationCode.UnrecognizedMessage,
        EntryPointFrameReader.HeaderError.UnsupportedSchema => SessionRejectEncoder.TerminationCode.UnrecognizedMessage,
        EntryPointFrameReader.HeaderError.BlockLengthMismatch => SessionRejectEncoder.TerminationCode.DecodingError,
        EntryPointFrameReader.HeaderError.MessageLengthMismatch => SessionRejectEncoder.TerminationCode.DecodingError,
        EntryPointFrameReader.HeaderError.ShortHeader => SessionRejectEncoder.TerminationCode.DecodingError,
        _ => SessionRejectEncoder.TerminationCode.Unspecified,
    };

    /// <summary>
    /// Sends a Terminate frame with <paramref name="terminationCode"/>
    /// directly to the underlying stream (bypassing the send queue), and
    /// closes the session.
    /// </summary>
    private async Task TerminateAndCloseAsync(byte terminationCode, string reason)
    {
        if (!IsOpen) { Close(reason); return; }
        var frame = new byte[SessionRejectEncoder.TerminateTotal];
        SessionRejectEncoder.EncodeTerminate(frame, SessionId, 0, terminationCode);
        try
        {
            await _transport.SendDirectAsync(frame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fixp session {ConnectionId} failed to write Terminate({Code})", ConnectionId, terminationCode);
        }
        Close(reason);
    }

    private async Task<bool> DispatchInboundAsync(EntryPointFrameReader.FrameInfo info, byte[] bodyBuf, int bodyLength)
    {
        ulong now = _nowNanos();
        _logger.LogTrace("session {ConnectionId} inbound frame templateId={TemplateId} blockLength={BlockLength} varDataLength={VarDataLength}",
            ConnectionId, info.TemplateId, info.BlockLength, info.VarDataLength);

        // Per spec §3.5, varData segments follow the fixed root block. We
        // validate them here (length-prefixed, declared per-template caps
        // from §4.10) before handing the fixed block to the typed
        // decoders. Classification (#GAP-14): structural failures
        // (truncation/overrun/trailing) are DECODING_ERROR → Terminate;
        // §4.10 business-rule failures (length-exceeded, CR/LF in
        // single-line text fields) on application templates are
        // BMR(33003) and the session stays open.
        var fullBody = new ReadOnlyMemory<byte>(bodyBuf, 0, bodyLength);
        var fixedBlock = fullBody.Slice(0, info.BlockLength).Span;
        var varData = fullBody.Slice(info.BlockLength).Span;

        // Per-session inbound sliding-window throttle (issue #56 / GAP-20,
        // guidelines §4.9). Only application templates count toward the
        // budget — FIXP session-layer messages (Negotiate, Establish,
        // Sequence, RetransmitRequest) bypass it. On violation we emit
        // BusinessMessageReject with text "Throttle limit exceeded" and
        // KEEP the session open; the offending frame does not consume a
        // slot (so a sustained burst keeps getting rejected, not slowly
        // re-admitted). The decoded varData / strict-gating checks below
        // are skipped on rejection.
        if (!TryAcceptInboundThrottle(info.TemplateId, fixedBlock))
            return true;

        // Strict gating (issue #43, spec §4.5.3.1): when the validator is
        // wired (i.e. we're running in "real" multi-tenant mode), an
        // application-template message that arrives before Establish has
        // completed MUST be rejected with Terminate(Unnegotiated) or
        // Terminate(NotEstablished) BEFORE we even attempt to decode the
        // payload. We deliberately skip varData validation in that case
        // so the peer learns the gating reason rather than a misleading
        // DECODING_ERROR. Legacy mode (no validator) keeps the previous
        // permissive behavior — covered by Phase 0/1 tests.
        if (_validator is not null && IsApplicationTemplate(info.TemplateId))
        {
            var gate = ApplyTransition(FixpEvent.ApplicationMessage);
            switch (gate)
            {
                case FixpAction.RejectAndTerminateUnnegotiated:
                    await TerminateAndCloseAsync(
                        SessionRejectEncoder.TerminationCode.Unnegotiated,
                        "app-message-before-negotiate").ConfigureAwait(false);
                    return false;
                case FixpAction.RejectAndTerminateUnestablished:
                    await TerminateAndCloseAsync(
                        SessionRejectEncoder.TerminationCode.NotEstablished,
                        "app-message-before-establish").ConfigureAwait(false);
                    return false;
                case FixpAction.Accept:
                    break;
                default:
                    // DropSilently / Terminal / NotApplied — defensive: drop frame.
                    return true;
            }

            // §4.6.3.1 / §4.10 (#GAP-10): businessHeader.sessionID must
            // match the negotiated SessionId. Mismatch → BMR(33003); the
            // session stays open (per spec the message is rejected, not
            // the session).
            if (!TryAcceptBusinessHeaderSessionId(info.TemplateId, fixedBlock))
                return true;

            // §4.5.5 / §4.6.2 (#GAP-07): inbound MsgSeqNum gap detection.
            // On gap (received > expected) emit NotApplied(fromSeqNo=expected,
            // count=received-expected) AND advance expected past the gap
            // (the new message is still applied — flow is idempotent).
            // Duplicates (received <= expected) are dropped silently
            // because the spec defines the inbound stream as idempotent.
            if (!TryAcceptBusinessHeaderMsgSeqNum(fixedBlock))
                return true;
        }

        // NewOrderCross (template 106) carries a NoSides repeating group
        // BEFORE the varData segments, so the generic varData walker
        // would mis-interpret the SBE group header as a length prefix.
        // Bypass the generic validator here; the bespoke decoder below
        // walks the group + varData with template-specific rules.
        if (info.TemplateId != EntryPointFrameReader.TidNewOrderCross)
        {
            var spec = EntryPointVarData.ExpectedFor(info.TemplateId, info.Version);
            var varResult = EntryPointVarData.ValidateDetailed(varData, spec);
            if (!varResult.IsOk)
            {
                // §4.10 (#GAP-14): length-exceeded and CR/LF rejections on
                // application messages are business-rule failures →
                // BMR(33003) and drop the offending frame; the session
                // stays open. Structural protocol errors (truncation,
                // overrun, trailing bytes) are still DECODING_ERROR →
                // terminate. Only applies in strict mode (Negotiated): in
                // legacy passthrough there is no business-header context to
                // reference, so retain the legacy terminate behaviour.
                if (_validator is not null && varResult.IsBusinessReject && IsApplicationTemplate(info.TemplateId))
                {
                    uint refSeqNum = fixedBlock.Length >= 8
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(4, 4))
                        : 0u;
                    ulong refClOrdId = fixedBlock.Length >= 28
                        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(fixedBlock.Slice(20, 8))
                        : 0UL;
                    WriteBusinessMessageReject(
                        refMsgType: BusinessMessageRejectEncoder.MapRefMsgTypeFromTemplateId(info.TemplateId),
                        refSeqNum: refSeqNum,
                        businessRejectRefId: refClOrdId,
                        businessRejectReason: 33003,
                        text: varResult.BmrText());
                    _logger.LogWarning(
                        "fixp session {ConnectionId} business reject (varData) template={Template} field={Field} kind={Kind}: {Debug}",
                        ConnectionId, info.TemplateId, varResult.FieldName, varResult.Kind, varResult.DebugMessage);
                    return true;
                }
                _sink.OnDecodeError(Identity, varResult.DebugMessage ?? "decode error: varData");
                await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:varData").ConfigureAwait(false);
                return false;
            }
        }

        // §4.10 (#GAP-21): CR/LF in fixed-block identifier slots
        // (senderLocation / enteringTrader / executingTrader) on the four
        // application templates that carry them must answer with
        // BMR(33003 "Line breaks not supported in <field>"). Runs after
        // the varData walker so a per-template empty slot table (Simple*
        // templates have no identifier fields) is a cheap no-op.
        if (_validator is not null && IsApplicationTemplate(info.TemplateId))
        {
            var idResult = EntryPointFixedIdentifiers.Validate(info.TemplateId, info.Version, fixedBlock);
            if (!idResult.IsOk)
            {
                uint refSeqNum = fixedBlock.Length >= 8
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(4, 4))
                    : 0u;
                ulong refRefId = fixedBlock.Length >= 28
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(fixedBlock.Slice(20, 8))
                    : 0UL;
                WriteBusinessMessageReject(
                    refMsgType: BusinessMessageRejectEncoder.MapRefMsgTypeFromTemplateId(info.TemplateId),
                    refSeqNum: refSeqNum,
                    businessRejectRefId: refRefId,
                    businessRejectReason: 33003,
                    text: idResult.BmrText());
                _logger.LogWarning(
                    "fixp session {ConnectionId} business reject (identifier line-break) template={Template} field={Field}",
                    ConnectionId, info.TemplateId, idResult.FieldName);
                return true;
            }
        }

        switch (info.TemplateId)
        {
            case EntryPointFrameReader.TidSequence:
                // Session-layer heartbeat / sequence sync. Liveness already
                // recorded by the receive loop; nothing else to do.
                return true;
            case EntryPointFrameReader.TidNegotiate:
                {
                    var step = ProcessNegotiate(fixedBlock, varData);
                    return await ExecuteNegotiateStepAsync(step).ConfigureAwait(false);
                }
            case EntryPointFrameReader.TidEstablish:
                {
                    var step = ProcessEstablish(fixedBlock);
                    return await ExecuteEstablishStepAsync(step).ConfigureAwait(false);
                }
            case EntryPointFrameReader.TidRetransmitRequest:
                ProcessAndEnqueueRetransmitRequest(fixedBlock);
                return true;
            case EntryPointFrameReader.TidSimpleNewOrder:
                if (InboundMessageDecoder.TryDecodeNewOrder(fixedBlock, EnteringFirm, now, out var no, out var noClOrd, out var noErr))
                {
                    _sink.EnqueueNewOrder(no, Identity, EnteringFirm, noClOrd);
                    return true;
                }
                _sink.OnDecodeError(Identity, noErr ?? "decode error: SimpleNewOrder");
                await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:SimpleNewOrder").ConfigureAwait(false);
                return false;
            case EntryPointFrameReader.TidSimpleModifyOrder:
                if (InboundMessageDecoder.TryDecodeReplace(fixedBlock, now, out var rp, out var rpClOrd, out var rpOrigClOrd, out var rpErr))
                {
                    _sink.EnqueueReplace(rp, Identity, EnteringFirm, rpClOrd, rpOrigClOrd);
                    return true;
                }
                _sink.OnDecodeError(Identity, rpErr ?? "decode error: SimpleModifyOrder");
                await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:SimpleModifyOrder").ConfigureAwait(false);
                return false;
            case EntryPointFrameReader.TidNewOrderSingle:
                {
                    var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
                        fixedBlock, EnteringFirm, now, out var nos, out var nosClOrd, out var nosMsg);
                    if (outcome == InboundMessageDecoder.InboundDecodeOutcome.Success)
                    {
                        _sink.EnqueueNewOrder(nos, Identity, EnteringFirm, nosClOrd);
                        return true;
                    }
                    if (outcome == InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature)
                    {
                        WriteApplicationBusinessReject(info.TemplateId, fixedBlock, nosClOrd, nosMsg ?? "unsupported");
                        return true;
                    }
                    _sink.OnDecodeError(Identity, nosMsg ?? "decode error: NewOrderSingle");
                    await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:NewOrderSingle").ConfigureAwait(false);
                    return false;
                }
            case EntryPointFrameReader.TidOrderCancelReplaceRequest:
                {
                    var outcome = InboundMessageDecoder.TryDecodeOrderCancelReplace(
                        fixedBlock, now, out var ocr, out var ocrClOrd, out var ocrOrigClOrd, out var ocrMsg);
                    if (outcome == InboundMessageDecoder.InboundDecodeOutcome.Success)
                    {
                        _sink.EnqueueReplace(ocr, Identity, EnteringFirm, ocrClOrd, ocrOrigClOrd);
                        return true;
                    }
                    if (outcome == InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature)
                    {
                        WriteApplicationBusinessReject(info.TemplateId, fixedBlock, ocrClOrd, ocrMsg ?? "unsupported");
                        return true;
                    }
                    _sink.OnDecodeError(Identity, ocrMsg ?? "decode error: OrderCancelReplaceRequest");
                    await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:OrderCancelReplaceRequest").ConfigureAwait(false);
                    return false;
                }
            case EntryPointFrameReader.TidNewOrderCross:
                {
                    // Body has root(84) + NoSides group + varData; we need
                    // the full span (not just fixedBlock) so the bespoke
                    // decoder can walk past the SBE group header.
                    var fullBodySpan = fullBody.Span;
                    var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
                        fullBodySpan, EnteringFirm, now, out var cross, out var crossId, out var crossMsg);
                    if (outcome == InboundMessageDecoder.InboundDecodeOutcome.Success)
                    {
                        _sink.EnqueueCross(cross, Identity, EnteringFirm);
                        return true;
                    }
                    if (outcome == InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature)
                    {
                        // BusinessRejectRefID for NewOrderCross is CrossID,
                        // not ClOrdID (offset 20 holds CrossID per schema).
                        WriteApplicationBusinessReject(info.TemplateId, fixedBlock, crossId, crossMsg ?? "unsupported");
                        return true;
                    }
                    _sink.OnDecodeError(Identity, crossMsg ?? "decode error: NewOrderCross");
                    await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:NewOrderCross").ConfigureAwait(false);
                    return false;
                }
            case EntryPointFrameReader.TidOrderCancelRequest:
                if (InboundMessageDecoder.TryDecodeCancel(fixedBlock, now, out var cn, out var cnClOrd, out var cnOrigClOrd, out var cnErr))
                {
                    _sink.EnqueueCancel(cn, Identity, EnteringFirm, cnClOrd, cnOrigClOrd);
                    return true;
                }
                _sink.OnDecodeError(Identity, cnErr ?? "decode error: OrderCancelRequest");
                await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:OrderCancelRequest").ConfigureAwait(false);
                return false;
            case EntryPointFrameReader.TidOrderMassActionRequest:
                {
                    var mcOutcome = InboundMessageDecoder.TryDecodeOrderMassActionRequest(
                        fixedBlock, now, out var mcCmd, out var mcClOrd, out var mcMsg);
                    if (mcOutcome == InboundMessageDecoder.InboundDecodeOutcome.Success)
                    {
                        // Spec §4.8 — acknowledge synchronously with
                        // OrderMassActionReport(ACCEPTED) ahead of the
                        // per-order ER_Cancel frames the engine will emit
                        // asynchronously when it processes EnqueueMassCancel.
                        byte? sideByte = mcCmd.SideFilter switch
                        {
                            Matching.Side.Buy => (byte)'1',
                            Matching.Side.Sell => (byte)'2',
                            _ => null,
                        };
                        WriteOrderMassActionReport(mcClOrd,
                            OrderMassActionReportEncoder.MassActionResponseAccepted,
                            massActionRejectReason: null,
                            side: sideByte, securityId: mcCmd.SecurityId,
                            transactTimeNanos: now);
                        _sink.EnqueueMassCancel(mcCmd, Identity, EnteringFirm);
                        return true;
                    }
                    if (mcOutcome == InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature)
                    {
                        // Template 702 IS the reject mechanism for 701 —
                        // emit OrderMassActionReport(REJECTED) rather than
                        // a generic BusinessMessageReject so the peer's
                        // FIX-side state machine sees the expected ack
                        // family (spec §4.8).
                        WriteOrderMassActionReport(mcClOrd,
                            OrderMassActionReportEncoder.MassActionResponseRejected,
                            massActionRejectReason: OrderMassActionReportEncoder.RejectReasonMassActionNotSupported,
                            side: null, securityId: 0, transactTimeNanos: now,
                            text: mcMsg);
                        return true;
                    }
                    _sink.OnDecodeError(Identity, mcMsg ?? "decode error: OrderMassActionRequest");
                    await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:OrderMassActionRequest").ConfigureAwait(false);
                    return false;
                }
            default:
                // Unreachable: TryParseInboundHeader has already screened out
                // unknown templates and returned UnsupportedTemplate.
                _sink.OnDecodeError(Identity, $"unsupported templateId={info.TemplateId}");
                await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.UnrecognizedMessage, "decode-error:unsupported").ConfigureAwait(false);
                return false;
        }
    }

    /// <summary>
    /// Outcome of synchronously processing an inbound Negotiate frame.
    /// Buffered byte arrays are produced inside <see cref="ProcessNegotiate"/>
    /// so the async writer in <see cref="ExecuteNegotiateStepAsync"/> can
    /// send them after spans have gone out of scope.
    /// </summary>
    private readonly struct NegotiateStep
    {
        public readonly bool IsAccepted;
        public readonly bool DecodeError;
        public readonly string? DecodeErrorMessage;
        public readonly byte[]? ResponseFrame;
        public readonly byte[]? RejectFrame;
        public readonly string LogReason;

        private NegotiateStep(bool accepted, bool decodeErr, string? decodeMsg,
            byte[]? response, byte[]? reject, string logReason)
        {
            IsAccepted = accepted;
            DecodeError = decodeErr;
            DecodeErrorMessage = decodeMsg;
            ResponseFrame = response;
            RejectFrame = reject;
            LogReason = logReason;
        }

        public static NegotiateStep Accepted(byte[] response, string reason)
            => new(true, false, null, response, null, reason);
        public static NegotiateStep Rejected(byte[] reject, string reason)
            => new(false, false, null, null, reject, reason);
        public static NegotiateStep Decode(string message)
            => new(false, true, message, null, null, message);
    }

    /// <summary>
    /// Synchronous Negotiate processing: SBE decode, JSON credentials
    /// parse, validator dispatch, claim acquisition, state transition,
    /// session-state mutation. Produces a <see cref="NegotiateStep"/>
    /// describing what the async wrapper should send (and whether to
    /// close the connection afterwards).
    /// </summary>
    private NegotiateStep ProcessNegotiate(ReadOnlySpan<byte> fixedBlock, ReadOnlySpan<byte> varData)
    {
        if (!NegotiateDecoder.TryDecode(fixedBlock, varData,
                out var req, out var credentialsBytes, out var decodeErr))
        {
            return NegotiateStep.Decode(decodeErr ?? "decode error: Negotiate");
        }

        // No validator configured → legacy single-tenant mode: accept the
        // peer's claim wholesale (Phase 1 behavior). Stamp identity
        // fields so subsequent app messages carry the peer's values.
        // We still consult the state machine: a second Negotiate on an
        // already-negotiated connection MUST be rejected with
        // ALREADY_NEGOTIATED per spec §4.5.3.1, even in legacy mode.
        if (_validator is null || _claims is null)
        {
            var action = ApplyTransition(FixpEvent.Negotiate);
            if (action != FixpAction.Accept)
            {
                var rejectFrame = new byte[NegotiateRejectEncoder.Total];
                NegotiateRejectEncoder.Encode(rejectFrame, req.SessionId, req.SessionVerId,
                    req.TimestampNanos, enteringFirm: null,
                    B3.Entrypoint.Fixp.Sbe.V6.NegotiationRejectCode.ALREADY_NEGOTIATED,
                    currentSessionVerId: SessionVerId == 0UL ? null : SessionVerId);
                return NegotiateStep.Rejected(rejectFrame,
                    $"negotiate-reject (legacy, ALREADY_NEGOTIATED, action={action})");
            }
            SessionId = req.SessionId;
            EnteringFirm = req.EnteringFirm;
            SessionVerId = req.SessionVerId;
            var frame = new byte[NegotiateResponseEncoder.Total];
            NegotiateResponseEncoder.Encode(frame, req.SessionId, req.SessionVerId,
                req.TimestampNanos, req.EnteringFirm,
                semVerMajor: 8, semVerMinor: 4, semVerPatch: 2);
            return NegotiateStep.Accepted(frame, $"negotiate-accept (legacy, sid={req.SessionId})");
        }

        if (!NegotiateCredentials.TryParse(credentialsBytes, out var creds, out var jsonErr))
        {
            // Credentials parse failure is a Credentials reject per spec
            // §4.5.2, NOT a decoding error (the SBE wire shape was fine).
            var rejectFrame = new byte[NegotiateRejectEncoder.Total];
            NegotiateRejectEncoder.Encode(rejectFrame, req.SessionId, req.SessionVerId,
                req.TimestampNanos, enteringFirm: null,
                B3.Entrypoint.Fixp.Sbe.V6.NegotiationRejectCode.CREDENTIALS,
                currentSessionVerId: null);
            return NegotiateStep.Rejected(rejectFrame,
                $"negotiate-reject (Credentials: {jsonErr})");
        }

        var outcome = _validator.Validate(in req, in creds, State);

        if (!outcome.IsAccepted)
        {
            var rejectFrame = new byte[NegotiateRejectEncoder.Total];
            NegotiateRejectEncoder.Encode(rejectFrame, req.SessionId, req.SessionVerId,
                req.TimestampNanos, enteringFirm: null,
                outcome.RejectCode,
                outcome.CurrentSessionVerId == 0UL ? null : outcome.CurrentSessionVerId);
            return NegotiateStep.Rejected(rejectFrame,
                $"negotiate-reject ({outcome.RejectCode}: {outcome.RejectReason})");
        }

        // Atomic claim. If another live transport already holds this
        // sessionID (or the version is stale by the time we commit), we
        // must reject — spec §4.5.2.
        var claim = _claims.TryClaim(req.SessionId, req.SessionVerId, this);
        if (claim != SessionClaimRegistry.ClaimResult.Accepted)
        {
            var code = claim switch
            {
                SessionClaimRegistry.ClaimResult.DuplicateConnection
                    => B3.Entrypoint.Fixp.Sbe.V6.NegotiationRejectCode.DUPLICATE_SESSION_CONNECTION,
                _ => B3.Entrypoint.Fixp.Sbe.V6.NegotiationRejectCode.INVALID_SESSIONVERID,
            };
            var rejectFrame = new byte[NegotiateRejectEncoder.Total];
            NegotiateRejectEncoder.Encode(rejectFrame, req.SessionId, req.SessionVerId,
                req.TimestampNanos, enteringFirm: null, code,
                claim == SessionClaimRegistry.ClaimResult.StaleVersion
                    ? _claims.CurrentSessionVerId(req.SessionId) : null);
            return NegotiateStep.Rejected(rejectFrame,
                $"negotiate-reject ({code}: claim {claim})");
        }

        _claimedSessionId = req.SessionId;
        _ = ApplyTransition(FixpEvent.Negotiate);
        SessionId = req.SessionId;
        EnteringFirm = outcome.Firm!.EnteringFirmCode;
        SessionVerId = req.SessionVerId;

        var responseFrame = new byte[NegotiateResponseEncoder.Total];
        NegotiateResponseEncoder.Encode(responseFrame, req.SessionId, req.SessionVerId,
            req.TimestampNanos, outcome.Firm.EnteringFirmCode,
            semVerMajor: 8, semVerMinor: 4, semVerPatch: 2);
        return NegotiateStep.Accepted(responseFrame,
            $"negotiate-accept (sid={req.SessionId} firm={outcome.Firm.Id})");
    }

    private async Task<bool> ExecuteNegotiateStepAsync(NegotiateStep step)
    {
        if (step.DecodeError)
        {
            _sink.OnDecodeError(Identity, step.DecodeErrorMessage ?? "decode error: Negotiate");
            await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError,
                "decode-error:Negotiate").ConfigureAwait(false);
            return false;
        }
        if (step.IsAccepted)
        {
            _logger.LogInformation("session {ConnectionId} {Reason}", ConnectionId, step.LogReason);
            if (!_transport.TryEnqueueFrame(step.ResponseFrame!))
            {
                _logger.LogWarning("session {ConnectionId} could not enqueue NegotiateResponse — closing",
                    ConnectionId);
                Close("send-queue-full:NegotiateResponse");
                return false;
            }
            return true;
        }
        // Reject path: send NegotiateReject, then Terminate, then close.
        _logger.LogInformation("session {ConnectionId} {Reason}", ConnectionId, step.LogReason);
        try
        {
            await _transport.SendDirectAsync(step.RejectFrame!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session {ConnectionId} failed to write NegotiateReject", ConnectionId);
        }
        await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.Unspecified,
            step.LogReason).ConfigureAwait(false);
        return false;
    }

    private static bool IsApplicationTemplate(ushort templateId)
        => templateId == EntryPointFrameReader.TidSimpleNewOrder
        || templateId == EntryPointFrameReader.TidSimpleModifyOrder
        || templateId == EntryPointFrameReader.TidNewOrderSingle
        || templateId == EntryPointFrameReader.TidOrderCancelReplaceRequest
        || templateId == EntryPointFrameReader.TidOrderCancelRequest
        || templateId == EntryPointFrameReader.TidNewOrderCross
        || templateId == EntryPointFrameReader.TidOrderMassActionRequest;

    /// <summary>
    /// Per-session inbound sliding-window throttle gate (issue #56 /
    /// GAP-20). Returns <c>true</c> if the inbound frame is admitted
    /// (the slot is consumed), or <c>false</c> after emitting
    /// <c>BusinessMessageReject(text="Throttle limit exceeded")</c> for
    /// the rejected frame. Session-layer FIXP messages (anything that
    /// is not an <see cref="IsApplicationTemplate"/> template) and
    /// sessions configured without a throttle (<c>_throttle == null</c>)
    /// always return <c>true</c>. Exposed as <c>internal</c> so tests can
    /// drive the throttle directly without constructing a fully-decodable
    /// SimpleNewOrder body.
    /// </summary>
    internal bool TryAcceptInboundThrottle(ushort templateId, ReadOnlySpan<byte> fixedBlock)
    {
        if (_throttle is null || !IsApplicationTemplate(templateId))
            return true;
        if (_throttle.TryAccept())
        {
            _options.ThrottleMetrics?.IncAccepted();
            return true;
        }
        uint refSeqNum = fixedBlock.Length >= 8
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(4, 4))
            : 0u;
        ulong refClOrdId = fixedBlock.Length >= 28
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(fixedBlock.Slice(20, 8))
            : 0UL;
        WriteBusinessMessageReject(
            refMsgType: BusinessMessageRejectEncoder.MapRefMsgTypeFromTemplateId(templateId),
            refSeqNum: refSeqNum,
            businessRejectRefId: refClOrdId,
            businessRejectReason: BusinessMessageRejectEncoder.Reason.Other,
            text: "Throttle limit exceeded");
        _options.ThrottleMetrics?.IncRejected();
        _logger.LogWarning(
            "fixp session {ConnectionId} throttle reject template={Template} (max={Max}/{WindowMs}ms)",
            ConnectionId, templateId, _throttle.MaxMessages, _throttle.TimeWindowMs);
        return false;
    }

    /// <summary>
    /// Emits a <c>BusinessMessageReject(33003)</c> for an application
    /// frame whose wire body decoded but requested an unsupported
    /// sub-feature (e.g. stop-order, iceberg, RLP, GTC). The session
    /// stays open; spec §4.10 / #GAP-15. <paramref name="fixedBlock"/>
    /// is the inbound body whose first 8 bytes are the
    /// <c>InboundBusinessHeader</c>: <c>sessionID</c> (offset 0) and
    /// <c>msgSeqNum</c> (offset 4). The supplied
    /// <paramref name="clOrdId"/> is forwarded as
    /// <c>businessRejectRefId</c> so the peer can correlate.
    /// </summary>
    private void WriteApplicationBusinessReject(ushort templateId, ReadOnlySpan<byte> fixedBlock, ulong clOrdId, string text)
    {
        uint refSeqNum = fixedBlock.Length >= 8
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(4, 4))
            : 0u;
        WriteBusinessMessageReject(
            refMsgType: BusinessMessageRejectEncoder.MapRefMsgTypeFromTemplateId(templateId),
            refSeqNum: refSeqNum,
            businessRejectRefId: clOrdId,
            businessRejectReason: 33003,
            text: text);
        _logger.LogWarning(
            "fixp session {ConnectionId} business reject (unsupported feature) template={Template} text={Text}",
            ConnectionId, templateId, text);
    }

    /// <summary>
    /// Spec §4.6.3.1 / §4.10 (#GAP-10): validates that an inbound business
    /// message's <c>InboundBusinessHeader.sessionID</c> matches this
    /// session's negotiated SessionId. Returns <c>true</c> if the header
    /// is OK and dispatch may proceed; returns <c>false</c> after
    /// emitting <c>BusinessMessageReject(33003)</c> for the mismatched
    /// frame. Called only in strict (validator-enabled) mode; legacy
    /// passthrough mode skips this check to preserve pre-Phase-2 test
    /// fixtures that mint sessionIds independently.
    /// </summary>
    /// internal — exposed for tests in B3.Exchange.Gateway.Tests
    internal bool TryAcceptBusinessHeaderSessionId(ushort templateId, ReadOnlySpan<byte> fixedBlock)
    {
        // InboundBusinessHeader composite: sessionID(uint32 @0),
        // msgSeqNum(uint32 @4), sendingTime(uint64 @8),
        // eventIndicator(byte @16), marketSegmentID(byte @17). Header is
        // the first 20 bytes of every business message body; the
        // EntryPoint exact-frame validator guarantees the body has at
        // least BlockLength bytes (>= 76 for the smallest template).
        uint hdrSessionId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(0, 4));
        if (hdrSessionId == SessionId) return true;

        uint hdrMsgSeqNum = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(4, 4));
        // ClOrdID lives at body offset 20 for all three currently-supported
        // application templates. Forwarded as businessRejectRefId so the
        // peer can correlate the reject back to the source order.
        ulong refClOrdId = fixedBlock.Length >= 28
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(fixedBlock.Slice(20, 8))
            : 0UL;

        WriteBusinessMessageReject(
            refMsgType: BusinessMessageRejectEncoder.MapRefMsgTypeFromTemplateId(templateId),
            refSeqNum: hdrMsgSeqNum,
            businessRejectRefId: refClOrdId,
            businessRejectReason: 33003,
            text: "Wrong sessionID in businessHeader");
        _logger.LogWarning(
            "fixp session {ConnectionId} rejected business message: sessionID {Got} != negotiated {Expected} (template={Template})",
            ConnectionId, hdrSessionId, SessionId, templateId);
        return false;
    }

    /// <summary>
    /// Spec §4.5.5 / §4.6.2 (#GAP-07): inbound MsgSeqNum gap detection.
    /// Reads <c>InboundBusinessHeader.msgSeqNum</c> at body[4..8] and
    /// compares to the next-expected counter
    /// (<c>LastIncomingSeqNo + 1</c>):
    ///
    /// <list type="bullet">
    ///   <item><description>received == expected → advance and accept (return true).</description></item>
    ///   <item><description>received &gt; expected → emit one
    ///     <c>NotApplied(fromSeqNo=expected, count=received-expected)</c>,
    ///     advance past the gap (received), and accept the new message
    ///     (return true). The flow is idempotent — the gap window is
    ///     recoverable by the client at its discretion.</description></item>
    ///   <item><description>received &lt;= LastIncomingSeqNo → duplicate
    ///     (e.g. PossResend on a client retransmit). Drop silently;
    ///     return false so the dispatch loop skips engine enqueue.
    ///     Idempotence per spec means the gateway never applies the
    ///     same MsgSeqNum twice.</description></item>
    /// </list>
    ///
    /// Called only in strict mode.
    /// </summary>
    internal bool TryAcceptBusinessHeaderMsgSeqNum(ReadOnlySpan<byte> fixedBlock)
    {
        uint hdrMsgSeqNum = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(4, 4));

        // Duplicate / replay: drop silently. Includes hdrMsgSeqNum == 0
        // (a peer that hasn't started numbering yet — defensive: don't
        // advance to 0).
        if (hdrMsgSeqNum == 0 || hdrMsgSeqNum <= LastIncomingSeqNo)
        {
            _logger.LogDebug(
                "fixp session {ConnectionId} dropping duplicate inbound msgSeqNum={SeqNum} (last accepted={Last})",
                ConnectionId, hdrMsgSeqNum, LastIncomingSeqNo);
            return false;
        }

        uint expected = LastIncomingSeqNo + 1;
        if (hdrMsgSeqNum > expected)
        {
            uint gap = hdrMsgSeqNum - expected;
            // NotApplied is a session-level frame: does NOT take an
            // outbound MsgSeqNum, so it goes straight to the transport
            // (no AppendAndEnqueueLocked / RetransmitBuffer involvement).
            // Holding _outboundLock keeps NotApplied from interleaving
            // inside an in-progress replay block (issue #46) or between
            // an ER and its trailing Sequence.
            var frame = new byte[SessionFrameEncoder.NotAppliedTotal];
            SessionFrameEncoder.EncodeNotApplied(frame, fromSeqNo: expected, count: gap);
            bool enqueued;
            lock (_outboundLock)
            {
                enqueued = _transport.TryEnqueueFrame(frame);
            }
            if (!enqueued)
            {
                // Send queue full / transport closing: do NOT advance and
                // do NOT accept. The transport will tear down the session;
                // dropping this message preserves the "NotApplied + apply"
                // coupling under failure (we never apply a gapped message
                // without successfully signaling the gap).
                _logger.LogWarning(
                    "fixp session {ConnectionId} could not enqueue NotApplied (transport closing or queue full); dropping inbound msgSeqNum={SeqNum}",
                    ConnectionId, hdrMsgSeqNum);
                return false;
            }
            _logger.LogWarning(
                "fixp session {ConnectionId} inbound gap detected: expected={Expected} received={Got} gap={Gap}; sent NotApplied",
                ConnectionId, expected, hdrMsgSeqNum, gap);
        }

        // Always advance to the new high-water mark on acceptance,
        // even when there was a gap (idempotent: the new message is
        // applied regardless of any missing predecessors).
        LastIncomingSeqNo = hdrMsgSeqNum;
        return true;
    }

    /// <summary>
    /// Outcome of synchronously processing an inbound Establish frame.
    /// Mirrors <see cref="NegotiateStep"/> so the async writer in
    /// <see cref="ExecuteEstablishStepAsync"/> can post the buffered
    /// EstablishAck / EstablishReject without holding spans across an
    /// <c>await</c>.
    /// </summary>
    private readonly struct EstablishStep
    {
        public readonly bool IsAccepted;
        public readonly bool DecodeError;
        public readonly string? DecodeErrorMessage;
        public readonly byte[]? AckFrame;
        public readonly byte[]? RejectFrame;
        public readonly string LogReason;

        private EstablishStep(bool accepted, bool decodeErr, string? decodeMsg,
            byte[]? ack, byte[]? reject, string logReason)
        {
            IsAccepted = accepted;
            DecodeError = decodeErr;
            DecodeErrorMessage = decodeMsg;
            AckFrame = ack;
            RejectFrame = reject;
            LogReason = logReason;
        }

        public static EstablishStep Accepted(byte[] ack, string reason)
            => new(true, false, null, ack, null, reason);
        public static EstablishStep Rejected(byte[] reject, string reason)
            => new(false, false, null, null, reject, reason);
        public static EstablishStep Decode(string message)
            => new(false, true, message, null, null, message);
    }

    /// <summary>
    /// Synchronous Establish processing: SBE decode, validator dispatch,
    /// state transition, session-state mutation. Produces an
    /// <see cref="EstablishStep"/> describing what the async wrapper should
    /// send (and whether to close the connection afterwards). On accept
    /// the negotiated <c>keepAliveInterval</c> is committed to
    /// <see cref="KeepAliveIntervalMs"/> so the watchdog can react.
    /// </summary>
    private EstablishStep ProcessEstablish(ReadOnlySpan<byte> fixedBlock)
    {
        if (!EstablishDecoder.TryDecode(fixedBlock, out var req, out var decodeErr))
            return EstablishStep.Decode(decodeErr ?? "decode error: Establish");

        // Legacy / no-validator mode mirrors Negotiate: accept whatever
        // the peer asked for, transition to Established, echo an Ack with
        // the requested keepAliveInterval. Existing Phase-1 tests rely on
        // this pass-through.
        if (_establishValidator is null)
        {
            var action = ApplyTransition(FixpEvent.Establish);
            if (action != FixpAction.Accept)
            {
                var rejectFrame = new byte[EstablishRejectEncoder.Total];
                EstablishRejectEncoder.Encode(rejectFrame, req.SessionId, req.SessionVerId,
                    req.TimestampNanos,
                    B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode.UNSPECIFIED,
                    lastIncomingSeqNo: LastIncomingSeqNo == 0 ? null : LastIncomingSeqNo);
                return EstablishStep.Rejected(rejectFrame,
                    $"establish-reject (legacy, action={action})");
            }
            KeepAliveIntervalMs = (long)req.KeepAliveIntervalMillis;
            var ackFrame = new byte[EstablishAckEncoder.Total];
            EstablishAckEncoder.Encode(ackFrame, req.SessionId, req.SessionVerId,
                req.TimestampNanos, req.KeepAliveIntervalMillis,
                nextSeqNo: PeekNextMsgSeqNum(), lastIncomingSeqNo: LastIncomingSeqNo,
                semVerMajor: 8, semVerMinor: 4, semVerPatch: 2);
            return EstablishStep.Accepted(ackFrame, $"establish-accept (legacy, sid={req.SessionId})");
        }

        // Strict mode: validate against current state + negotiated identity
        // before any transition. The validator is pure and never mutates.
        var outcome = _establishValidator.Validate(in req, State,
            negotiatedSessionId: SessionId,
            negotiatedSessionVerId: SessionVerId,
            lastIncomingSeqNo: LastIncomingSeqNo);
        if (!outcome.IsAccepted)
        {
            var rejectFrame = new byte[EstablishRejectEncoder.Total];
            EstablishRejectEncoder.Encode(rejectFrame, req.SessionId, req.SessionVerId,
                req.TimestampNanos, outcome.RejectCode,
                lastIncomingSeqNo: outcome.LastIncomingSeqNo);
            return EstablishStep.Rejected(rejectFrame,
                $"establish-reject ({outcome.RejectCode}: {outcome.RejectReason})");
        }

        // Defensive: validator already screened state, but fold the
        // result through the state machine so the canonical transition
        // log records the event.
        var act = ApplyTransition(FixpEvent.Establish);
        if (act != FixpAction.Accept)
        {
            var rejectFrame = new byte[EstablishRejectEncoder.Total];
            EstablishRejectEncoder.Encode(rejectFrame, req.SessionId, req.SessionVerId,
                req.TimestampNanos,
                B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode.UNSPECIFIED,
                lastIncomingSeqNo: LastIncomingSeqNo == 0 ? null : LastIncomingSeqNo);
            return EstablishStep.Rejected(rejectFrame,
                $"establish-reject (state-machine action={act})");
        }

        KeepAliveIntervalMs = (long)req.KeepAliveIntervalMillis;
        var ack = new byte[EstablishAckEncoder.Total];
        EstablishAckEncoder.Encode(ack, req.SessionId, req.SessionVerId,
            req.TimestampNanos, req.KeepAliveIntervalMillis,
            nextSeqNo: PeekNextMsgSeqNum(), lastIncomingSeqNo: LastIncomingSeqNo,
            semVerMajor: 8, semVerMinor: 4, semVerPatch: 2);
        return EstablishStep.Accepted(ack, $"establish-accept (sid={req.SessionId})");
    }

    private async Task<bool> ExecuteEstablishStepAsync(EstablishStep step)
    {
        if (step.DecodeError)
        {
            _sink.OnDecodeError(Identity, step.DecodeErrorMessage ?? "decode error: Establish");
            await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError,
                "decode-error:Establish").ConfigureAwait(false);
            return false;
        }
        if (step.IsAccepted)
        {
            _logger.LogInformation("session {ConnectionId} {Reason}", ConnectionId, step.LogReason);
            if (!_transport.TryEnqueueFrame(step.AckFrame!))
            {
                _logger.LogWarning("session {ConnectionId} could not enqueue EstablishAck — closing",
                    ConnectionId);
                Close("send-queue-full:EstablishAck");
                return false;
            }
            return true;
        }
        // Reject path: send EstablishReject, then Terminate, then close.
        _logger.LogInformation("session {ConnectionId} {Reason}", ConnectionId, step.LogReason);
        try
        {
            await _transport.SendDirectAsync(step.RejectFrame!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session {ConnectionId} failed to write EstablishReject", ConnectionId);
        }
        await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.Unspecified,
            step.LogReason).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Periodic watchdog that drives the FIXP-style heartbeat + idle-timeout
    /// policy. Runs on its own task; never touches the socket directly —
    /// instead it enqueues <c>Sequence</c> frames into the transport's
    /// bounded channel, so all writes remain serialised.
    /// </summary>
    private async Task RunWatchdogLoopAsync(CancellationToken ct)
    {
        // Capture the transport this loop is bound to so a stale tick
        // after a re-attach (#69b-2) can be detected and a teardown
        // refused. Without this guard, a watchdog from the OLD transport
        // could complete its Task.Delay just as the cancellation arrives,
        // observe stale liveness fields, and call Close("idle-timeout")
        // against the freshly attached session.
        var ownTransport = _transport;
        // Tick at a fraction of the smallest configured interval so we react
        // promptly without busy-polling. Capped to keep tests responsive.
        int tickMs = Math.Max(1, Math.Min(_options.HeartbeatIntervalMs,
            Math.Min(_options.IdleTimeoutMs, _options.TestRequestGraceMs)) / 4);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(tickMs, ct).ConfigureAwait(false);
                if (!IsOpen) return;
                // A re-attach replaced the transport this loop was bound
                // to; surrender to the new watchdog and exit.
                if (!ReferenceEquals(_transport, ownTransport)) return;

                long now = NowMs();
                long sinceIn = now - Volatile.Read(ref _lastInboundMs);
                long sinceOut = now - ownTransport.LastOutboundTickMs;

                // Idle teardown: if a probe is outstanding and grace elapsed
                // without any inbound, close. The grace clock starts from the
                // moment the probe was sent (i.e. last outbound tick was
                // bumped), so we measure the additional silence beyond
                // idleTimeoutMs.
                if (sinceIn >= (long)_options.IdleTimeoutMs + _options.TestRequestGraceMs &&
                    Volatile.Read(ref _probeOutstanding) == 1)
                {
                    // Generation-aware close: another reattach could have
                    // raced in between the snapshot above and the close
                    // call below; CloseIfTransportCurrent re-validates
                    // under _attachLock.
                    CloseIfTransportCurrent("idle-timeout", ownTransport);
                    return;
                }

                // Probe (FIXP TestRequest equivalent): if we've been silent on
                // the inbound side past the idle threshold and we haven't
                // already sent a probe in this idle stretch, send one.
                if (sinceIn >= _options.IdleTimeoutMs &&
                    Interlocked.CompareExchange(ref _probeOutstanding, 1, 0) == 0)
                {
                    EnqueueSequence();
                    continue;
                }

                // Regular heartbeat: only when no other outbound traffic has
                // been sent within the heartbeat interval. This naturally
                // suppresses heartbeats while ER traffic is flowing.
                if (sinceOut >= _options.HeartbeatIntervalMs)
                {
                    EnqueueSequence();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Watchdog must never crash the session silently; tear down on
            // unexpected error so the listener can drop the connection.
            Close("watchdog-error");
        }
    }

    private void EnqueueSequence()
    {
        if (!IsOpen) return;
        var frame = new byte[SessionFrameEncoder.SequenceTotal];
        // FIXP §4.5.5: the Sequence message announces the *next* MsgSeqNum
        // we intend to send. It does not consume a sequence number itself,
        // so we must peek instead of incrementing — otherwise the Establish
        // handshake would observe a phantom gap (e.g. an idle heartbeat
        // before Establish would push EstablishAck.nextSeqNo off by one).
        //
        // Outbound lock: serializes against the replay block in
        // ProcessAndEnqueueRetransmitRequest so a watchdog-driven
        // heartbeat cannot land inside the Retransmission/replay/Sequence
        // sequence on the wire (gpt-5.5 review #1).
        lock (_outboundLock)
        {
            SessionFrameEncoder.EncodeSequence(frame, PeekNextMsgSeqNum());
            _transport.TryEnqueueFrame(frame);
        }
    }

    private static async Task ReadExactlyAsync(Stream s, byte[] buf, CancellationToken ct)
        => await ReadExactlyAsync(s, buf.AsMemory(), ct).ConfigureAwait(false);

    private static async Task ReadExactlyAsync(Stream s, Memory<byte> dst, CancellationToken ct)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int n = await s.ReadAsync(dst.Slice(total), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            total += n;
        }
    }

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
    {
        // Layout (BLOCK_LENGTH=20): SessionID(4) | Timestamp(8 ulong) |
        // FromSeqNo(4 uint) | Count(4 uint).
        if (fixedBlock.Length < 20) return; // header validator already enforced this; defensive
        uint reqSessionId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(0, 4));
        ulong reqTimestamp = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(fixedBlock.Slice(4, 8));
        uint fromSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(12, 4));
        uint count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(16, 4));

        var action = ApplyTransition(FixpEvent.RetransmitRequest);
        if (action != FixpAction.Replay)
        {
            // DropSilently / DeferredToReestablish / Terminal: per spec,
            // emit nothing. Suspended sessions have no live transport
            // anyway; a request that arrives in any pre-Established state
            // is simply ignored and the peer is expected to drive the
            // handshake before requesting recovery.
            _logger.LogDebug("session {ConnectionId} retransmit-request action={Action} state={State} dropped",
                ConnectionId, action, State);
            return;
        }

        // Spec validations BEFORE single-in-flight gate so a malformed
        // request doesn't leave the gate held.
        if (reqSessionId != SessionId)
        {
            EnqueueRetransmitReject(reqTimestamp, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_SESSION);
            return;
        }
        if (reqTimestamp == 0UL)
        {
            EnqueueRetransmitReject(reqTimestamp, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_TIMESTAMP);
            return;
        }

        // One-outstanding-request gate. Cleared in the finally below
        // after the entire replay block is enqueued under _outboundLock,
        // so a subsequent request can never see partial state.
        if (Interlocked.CompareExchange(ref _retxInProgress, 1, 0) != 0)
        {
            EnqueueRetransmitReject(reqTimestamp, B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.RETRANSMIT_IN_PROGRESS);
            return;
        }
        try
        {
            // Buffer combines count-bounds + window check under one lock
            // (no TOCTOU between FirstAvailable read and clone copy).
            var snap = _retxBuffer.TryGet(fromSeq, count);
            if (snap.RejectCode is { } code)
            {
                EnqueueRetransmitReject(reqTimestamp, code);
                return;
            }

            // Hold the outbound lock for the entire block so (a) no live
            // business frame interleaves on the wire and (b) the
            // trailing Sequence's nextSeqNo matches the next live seq
            // the peer will see (gpt-5.5 critique #1, #2).
            lock (_outboundLock)
            {
                // Backpressure: TcpTransport's bounded send queue can
                // drop frames if it overflows mid-replay (gpt-5.5 review
                // #2). Reserve capacity for the FULL block (header +
                // clones + trailer) under the outbound lock; if the
                // queue cannot accept the burst, reject with SYSTEM_BUSY
                // rather than advertise an exact count we can't deliver.
                int needed = 2 + snap.Frames.Length;
                if (_transport.SendQueueDepth + needed > _sendQueueCapacity)
                {
                    EnqueueRetransmitReject(reqTimestamp,
                        B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.SYSTEM_BUSY);
                    return;
                }

                var headerFrame = new byte[RetransmissionEncoder.RetransmissionTotal];
                RetransmissionEncoder.EncodeRetransmission(headerFrame,
                    SessionId, reqTimestamp,
                    firstRetransmittedSeqNo: snap.FirstSeq, count: snap.ActualCount);
                _transport.TryEnqueueFrame(headerFrame);

                for (int i = 0; i < snap.Frames.Length; i++)
                    _transport.TryEnqueueFrame(snap.Frames[i]);

                var trailer = new byte[SessionFrameEncoder.SequenceTotal];
                SessionFrameEncoder.EncodeSequence(trailer, PeekNextMsgSeqNum());
                _transport.TryEnqueueFrame(trailer);
            }
        }
        finally
        {
            Volatile.Write(ref _retxInProgress, 0);
        }
    }

    private void EnqueueRetransmitReject(ulong requestTimestampNanos,
        B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode code)
    {
        var frame = new byte[RetransmissionEncoder.RetransmitRejectTotal];
        RetransmissionEncoder.EncodeRetransmitReject(frame, SessionId, requestTimestampNanos, code);
        _transport.TryEnqueueFrame(frame);
        _logger.LogInformation("session {ConnectionId} retransmit-reject code={Code}",
            ConnectionId, code);
    }

    public bool WriteExecutionReportNew(in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue)
    {
        if (!IsOpen) return false;
        ulong clOrd = ulong.TryParse(e.ClOrdId, out var v) ? v : 0;
        var exact = new byte[ExecutionReportEncoder.ExecReportNewTotal];
        lock (_outboundLock)
        {
            ExecutionReportEncoder.EncodeExecReportNew(exact,
                SessionId, NextMsgSeqNum(), e.InsertTimestampNanos,
                e.Side, clOrd, e.OrderId, e.SecurityId, e.OrderId,
                (ulong)e.RptSeq, e.InsertTimestampNanos,
                OrderType.Limit, TimeInForce.Day,
                e.RemainingQuantity, e.PriceMantissa,
                receivedTimeNanos);
            return AppendAndEnqueueLocked(exact);
        }
    }

    public bool WriteExecutionReportTrade(in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty)
    {
        if (!IsOpen) return false;
        var side = isAggressor ? e.AggressorSide : (e.AggressorSide == Side.Buy ? Side.Sell : Side.Buy);
        var exact = new byte[ExecutionReportEncoder.ExecReportTradeTotal];
        lock (_outboundLock)
        {
            ExecutionReportEncoder.EncodeExecReportTrade(exact,
                SessionId, NextMsgSeqNum(), e.TransactTimeNanos,
                side, clOrdIdValue, ownerOrderId, e.SecurityId, ownerOrderId,
                e.Quantity, e.PriceMantissa,
                (ulong)e.RptSeq, e.TransactTimeNanos, leavesQty, cumQty,
                isAggressor, e.TradeId,
                isAggressor ? e.RestingFirm : e.AggressorFirm,
                tradeDate: 0,
                orderQty: leavesQty + cumQty);
            return AppendAndEnqueueLocked(exact);
        }
    }

    public bool WriteExecutionReportCancel(in OrderCanceledEvent e, ulong clOrdIdValue, ulong origClOrdIdValue,
        ulong receivedTimeNanos = ulong.MaxValue)
    {
        if (!IsOpen) return false;
        var exact = new byte[ExecutionReportEncoder.ExecReportCancelTotal];
        lock (_outboundLock)
        {
            ExecutionReportEncoder.EncodeExecReportCancel(exact,
                SessionId, NextMsgSeqNum(), e.TransactTimeNanos,
                e.Side, clOrdIdValue, origClOrdIdValue, e.OrderId,
                e.SecurityId, e.OrderId,
                (ulong)e.RptSeq, e.TransactTimeNanos,
                cumQty: 0, e.RemainingQuantityAtCancel, e.PriceMantissa,
                receivedTimeNanos);
            return AppendAndEnqueueLocked(exact);
        }
    }

    public bool WriteExecutionReportModify(long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue,
        Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq,
        ulong receivedTimeNanos = ulong.MaxValue)
    {
        if (!IsOpen) return false;
        var exact = new byte[ExecutionReportEncoder.ExecReportModifyTotal];
        lock (_outboundLock)
        {
            ExecutionReportEncoder.EncodeExecReportModify(exact,
                SessionId, NextMsgSeqNum(), transactTimeNanos,
                side, clOrdIdValue, origClOrdIdValue, orderId,
                securityId, orderId, (ulong)rptSeq, transactTimeNanos,
                leavesQty: newRemainingQty, cumQty: 0, orderQty: newRemainingQty, priceMantissa: newPriceMantissa,
                receivedTimeNanos: receivedTimeNanos);
            return AppendAndEnqueueLocked(exact);
        }
    }

    /// <summary>
    /// Per-session monotonic counter sourcing
    /// <c>OrderMassActionReport.MassActionReportID</c> (template 702).
    /// Spec requires an engine-assigned unique id; deriving it from a
    /// per-session counter is sufficient because the (sessionId, id)
    /// pair is globally unique.
    /// </summary>
    private long _massActionReportSeq;

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
    {
        if (!IsOpen) return false;
        ulong reportId = (ulong)Interlocked.Increment(ref _massActionReportSeq);
        int textLen = string.IsNullOrEmpty(text) ? 0 : Math.Min(text!.Length, OrderMassActionReportEncoder.MaxTextLength);
        var exact = new byte[OrderMassActionReportEncoder.TotalSize(textLen)];
        lock (_outboundLock)
        {
            OrderMassActionReportEncoder.EncodeOrderMassActionReportWithText(exact,
                SessionId, NextMsgSeqNum(), transactTimeNanos,
                clOrdIdValue, reportId, transactTimeNanos,
                massActionResponse, massActionRejectReason, side, securityId, text);
            return AppendAndEnqueueLocked(exact);
        }
    }

    public bool WriteExecutionReportReject(in RejectEvent e, ulong clOrdIdValue)
    {
        if (!IsOpen) return false;
        uint rej = MapRejectReason(e.Reason);
        var exact = new byte[ExecutionReportEncoder.ExecReportRejectTotal];
        lock (_outboundLock)
        {
            ExecutionReportEncoder.EncodeExecReportReject(exact,
                SessionId, NextMsgSeqNum(), e.TransactTimeNanos,
                clOrdIdValue, origClOrdIdValue: 0, e.SecurityId, e.OrderIdOrZero,
                rej, e.TransactTimeNanos);
            return AppendAndEnqueueLocked(exact);
        }
    }

    /// <summary>
    /// Must be called with <see cref="_outboundLock"/> held. Reads the
    /// already-allocated <c>MsgSeqNum</c> back out of the encoded
    /// frame, appends to the retx buffer, then enqueues onto the
    /// transport. Centralizes the two-step "buffer + enqueue" so every
    /// business-frame write site has identical semantics.
    /// </summary>
    private bool AppendAndEnqueueLocked(byte[] exact)
    {
        // OutboundBusinessHeader.MsgSeqNum sits at body offset 4
        // (SessionID(4) | MsgSeqNum(4) | …) → absolute offset
        // WireHeaderSize + 4 = 16. Read it back so the buffer key
        // exactly matches what's on the wire.
        uint seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            exact.AsSpan(EntryPointFrameReader.WireHeaderSize + 4, 4));
        // Append BEFORE enqueue: per spec recovery semantics, any seq
        // that has been allocated MUST be replayable, even if the local
        // send queue rejects the frame (gpt-5.5 critique #9).
        _retxBuffer.Append(seq, exact);
        return _transport.TryEnqueueFrame(exact);
    }

    public bool WriteSessionReject(byte terminationCode)
    {
        if (!IsOpen) return false;
        var exact = new byte[SessionRejectEncoder.TerminateTotal];
        SessionRejectEncoder.EncodeTerminate(exact, SessionId, 0, terminationCode);
        bool result = _transport.TryEnqueueFrame(exact);
        Close();
        return result;
    }

    public bool WriteBusinessMessageReject(byte refMsgType, uint refSeqNum, ulong businessRejectRefId,
        uint businessRejectReason, string? text = null)
    {
        if (!IsOpen) return false;
        int textLen = string.IsNullOrEmpty(text) ? 0 : Math.Min(text.Length, BusinessMessageRejectEncoder.MaxTextLength);
        int total = BusinessMessageRejectEncoder.TotalSize(textLen);
        var exact = new byte[total];
        lock (_outboundLock)
        {
            BusinessMessageRejectEncoder.EncodeBusinessMessageRejectWithText(
                exact, SessionId, NextMsgSeqNum(), _nowNanos(),
                refMsgType, refSeqNum, businessRejectRefId, businessRejectReason, text);
            return AppendAndEnqueueLocked(exact);
        }
    }

    /// <summary>
    /// Maps engine <see cref="RejectReason"/> to the FIX OrdRejReason wire code
    /// emitted on ExecutionReport_Reject (#GAP-17 / issue #53). Standard FIX 4.4
    /// codes used:
    ///   0  = Broker / exchange option (generic / no closer match)
    ///   1  = Unknown symbol
    ///   3  = Order exceeds limit (used here for price-band / tick / non-positive price)
    ///   5  = Unknown order
    ///   11 = Unsupported order characteristic (used for invalid quantity and
    ///        Market/IOC-only constraints not satisfied by the inbound order)
    /// Engine reasons that have no direct FIX peer (e.g. <see cref="RejectReason.MarketNoLiquidity"/>,
    /// <see cref="RejectReason.FokUnfillable"/>, <see cref="RejectReason.SelfTradePrevention"/>)
    /// fall through to 0 (Broker/exchange option) — context is conveyed via the
    /// <c>Text</c> tag where applicable.
    /// </summary>
    internal static uint MapRejectReason(RejectReason r) => r switch
    {
        RejectReason.UnknownInstrument => 1u,
        RejectReason.UnknownOrderId => 5u,
        RejectReason.PriceOutOfBand => 3u,
        RejectReason.PriceNotOnTick => 3u,
        RejectReason.PriceNonPositive => 3u,
        RejectReason.QuantityNonPositive => 11u,
        RejectReason.QuantityNotMultipleOfLot => 11u,
        RejectReason.MarketNotImmediateOrCancel => 11u,
        RejectReason.InvalidTimeInForceForMarket => 11u,
        RejectReason.MarketNoLiquidity => 0u,
        RejectReason.FokUnfillable => 0u,
        RejectReason.SelfTradePrevention => 0u,
        _ => 0u,
    };

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
