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

    private readonly TcpTransport _transport;
    private readonly IInboundCommandSink _sink;
    private readonly ILogger<FixpSession> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<ulong> _nowNanos;
    private readonly FixpSessionOptions _options;
    private readonly Action<FixpSession, string>? _onClosed;
    private readonly NegotiationValidator? _validator;
    private readonly SessionClaimRegistry? _claims;
    /// <summary>The wire SessionID currently claimed in <see cref="_claims"/>,
    /// or 0 if no claim is held. Tracked so <see cref="Close"/> can
    /// release the claim using the value from the moment the claim was
    /// taken (the public <see cref="SessionId"/> may be re-stamped on a
    /// subsequent rejected Negotiate that fails after we've already
    /// claimed).</summary>
    private uint _claimedSessionId;
    private long _msgSeqNum;
    private int _isOpen = 1;
    // Watchdog state (milliseconds since process start, monotonic).
    private long _lastInboundMs;
    // Set true while we are waiting on the grace window after sending a probe;
    // cleared as soon as any inbound frame arrives. Prevents flooding probes.
    private int _probeOutstanding;
    private Task? _recvTask;
    private Task? _watchdogTask;

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
    /// <summary>Stable, transport-neutral identity of this session as seen
    /// by Core / Contracts. Routing key the Gateway uses to resolve
    /// outbound ExecutionReports back to this <see cref="FixpSession"/>.
    /// Derived from <see cref="ConnectionId"/> until Phase 2 introduces a
    /// <c>SessionRegistry</c> backed by authentication.</summary>
    public ContractsSessionId Identity { get; }
    public bool IsOpen => Volatile.Read(ref _isOpen) == 1 && _transport.IsOpen;

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
        var t = FixpStateMachine.Apply(State, ev);
        State = t.NewState;
        return t.Action;
    }

    /// <summary>
    /// Approximate number of pre-encoded ExecutionReport frames sitting
    /// in the outbound queue, for /metrics scraping.
    /// </summary>
    public int SendQueueDepth => _transport.SendQueueDepth;

    public FixpSession(long connectionId, uint enteringFirm, uint sessionId,
        Stream stream, IInboundCommandSink sink, ILogger<FixpSession> logger,
        Func<ulong>? nowNanos = null,
        int sendQueueCapacity = DefaultSendQueueCapacity,
        FixpSessionOptions? options = null,
        Action<FixpSession, string>? onClosed = null,
        ILogger<TcpTransport>? transportLogger = null,
        NegotiationValidator? negotiationValidator = null,
        SessionClaimRegistry? sessionClaims = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ConnectionId = connectionId;
        EnteringFirm = enteringFirm;
        SessionId = sessionId;
        Identity = new ContractsSessionId("conn-" + connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _sink = sink;
        _logger = logger;
        _nowNanos = nowNanos ?? DefaultNowNanos;
        _options = options ?? FixpSessionOptions.Default;
        _options.Validate();
        _onClosed = onClosed;
        _validator = negotiationValidator;
        _claims = sessionClaims;
        if (negotiationValidator is not null && sessionClaims is null)
            throw new ArgumentException(
                "sessionClaims is required when negotiationValidator is supplied",
                nameof(sessionClaims));
        // The transport's onClose callback funnels back through our Close so
        // session-level book-keeping (sink notification, onClosed delegate)
        // runs even when teardown originates from a transport-side IO error.
        _transport = new TcpTransport(connectionId, stream,
            transportLogger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TcpTransport>.Instance,
            sendQueueCapacity,
            onClose: reason => Close(reason));
        Volatile.Write(ref _lastInboundMs, NowMs());
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
        var stream = _transport.Stream;
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
            Close("recv-eof");
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
        // decoders. A varData failure is always DECODING_ERROR per §4.10.
        var fullBody = new ReadOnlyMemory<byte>(bodyBuf, 0, bodyLength);
        var fixedBlock = fullBody.Slice(0, info.BlockLength).Span;
        var varData = fullBody.Slice(info.BlockLength).Span;
        var spec = EntryPointVarData.ExpectedFor(info.TemplateId, info.Version);
        if (!EntryPointVarData.TryValidate(varData, spec, out var varErr))
        {
            _sink.OnDecodeError(Identity, varErr ?? "decode error: varData");
            await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:varData").ConfigureAwait(false);
            return false;
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
            case EntryPointFrameReader.TidOrderCancelRequest:
                if (InboundMessageDecoder.TryDecodeCancel(fixedBlock, now, out var cn, out var cnClOrd, out var cnOrigClOrd, out var cnErr))
                {
                    _sink.EnqueueCancel(cn, Identity, EnteringFirm, cnClOrd, cnOrigClOrd);
                    return true;
                }
                _sink.OnDecodeError(Identity, cnErr ?? "decode error: OrderCancelRequest");
                await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:OrderCancelRequest").ConfigureAwait(false);
                return false;
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

    /// <summary>
    /// Periodic watchdog that drives the FIXP-style heartbeat + idle-timeout
    /// policy. Runs on its own task; never touches the socket directly —
    /// instead it enqueues <c>Sequence</c> frames into the transport's
    /// bounded channel, so all writes remain serialised.
    /// </summary>
    private async Task RunWatchdogLoopAsync(CancellationToken ct)
    {
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

                long now = NowMs();
                long sinceIn = now - Volatile.Read(ref _lastInboundMs);
                long sinceOut = now - _transport.LastOutboundTickMs;

                // Idle teardown: if a probe is outstanding and grace elapsed
                // without any inbound, close. The grace clock starts from the
                // moment the probe was sent (i.e. last outbound tick was
                // bumped), so we measure the additional silence beyond
                // idleTimeoutMs.
                if (sinceIn >= (long)_options.IdleTimeoutMs + _options.TestRequestGraceMs &&
                    Volatile.Read(ref _probeOutstanding) == 1)
                {
                    // Seam for issue #11: a future BusinessReject (templateId=206)
                    // should be enqueued here before close so the peer learns
                    // the reason. Today we close with a logged reason string.
                    Close("idle-timeout");
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
        SessionFrameEncoder.EncodeSequence(frame, NextMsgSeqNum());
        _transport.TryEnqueueFrame(frame);
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

    public bool WriteExecutionReportNew(in OrderAcceptedEvent e)
    {
        if (!IsOpen) return false;
        var frame = ArrayPool<byte>.Shared.Rent(ExecutionReportEncoder.ExecReportNewTotal);
        ulong clOrd = ulong.TryParse(e.ClOrdId, out var v) ? v : 0;
        int n = ExecutionReportEncoder.EncodeExecReportNew(frame.AsSpan(0, ExecutionReportEncoder.ExecReportNewTotal),
            SessionId, NextMsgSeqNum(), e.InsertTimestampNanos,
            e.Side, clOrd, e.OrderId, e.SecurityId, e.OrderId,
            (ulong)e.RptSeq, e.InsertTimestampNanos,
            OrderType.Limit, TimeInForce.Day,
            e.RemainingQuantity, e.PriceMantissa);
        return TryEnqueueExact(frame, n);
    }

    public bool WriteExecutionReportTrade(in TradeEvent e, bool isAggressor, long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty)
    {
        if (!IsOpen) return false;
        var frame = ArrayPool<byte>.Shared.Rent(ExecutionReportEncoder.ExecReportTradeTotal);
        var side = isAggressor ? e.AggressorSide : (e.AggressorSide == Side.Buy ? Side.Sell : Side.Buy);
        int n = ExecutionReportEncoder.EncodeExecReportTrade(frame.AsSpan(0, ExecutionReportEncoder.ExecReportTradeTotal),
            SessionId, NextMsgSeqNum(), e.TransactTimeNanos,
            side, clOrdIdValue, ownerOrderId, e.SecurityId, ownerOrderId,
            e.Quantity, e.PriceMantissa,
            (ulong)e.RptSeq, e.TransactTimeNanos, leavesQty, cumQty,
            isAggressor, e.TradeId,
            isAggressor ? e.RestingFirm : e.AggressorFirm,
            tradeDate: 0,
            orderQty: leavesQty + cumQty);
        return TryEnqueueExact(frame, n);
    }

    public bool WriteExecutionReportCancel(in OrderCanceledEvent e, ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        if (!IsOpen) return false;
        var frame = ArrayPool<byte>.Shared.Rent(ExecutionReportEncoder.ExecReportCancelTotal);
        int n = ExecutionReportEncoder.EncodeExecReportCancel(frame.AsSpan(0, ExecutionReportEncoder.ExecReportCancelTotal),
            SessionId, NextMsgSeqNum(), e.TransactTimeNanos,
            e.Side, clOrdIdValue, origClOrdIdValue, e.OrderId,
            e.SecurityId, e.OrderId,
            (ulong)e.RptSeq, e.TransactTimeNanos,
            cumQty: 0, e.RemainingQuantityAtCancel, e.PriceMantissa);
        return TryEnqueueExact(frame, n);
    }

    public bool WriteExecutionReportModify(long securityId, long orderId, ulong clOrdIdValue, ulong origClOrdIdValue,
        Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq)
    {
        if (!IsOpen) return false;
        var frame = ArrayPool<byte>.Shared.Rent(ExecutionReportEncoder.ExecReportModifyTotal);
        int n = ExecutionReportEncoder.EncodeExecReportModify(frame.AsSpan(0, ExecutionReportEncoder.ExecReportModifyTotal),
            SessionId, NextMsgSeqNum(), transactTimeNanos,
            side, clOrdIdValue, origClOrdIdValue, orderId,
            securityId, orderId, (ulong)rptSeq, transactTimeNanos,
            leavesQty: newRemainingQty, cumQty: 0, orderQty: newRemainingQty, priceMantissa: newPriceMantissa);
        return TryEnqueueExact(frame, n);
    }

    public bool WriteExecutionReportReject(in RejectEvent e, ulong clOrdIdValue)
    {
        if (!IsOpen) return false;
        var frame = ArrayPool<byte>.Shared.Rent(ExecutionReportEncoder.ExecReportRejectTotal);
        byte rej = MapRejectReason(e.Reason);
        int n = ExecutionReportEncoder.EncodeExecReportReject(frame.AsSpan(0, ExecutionReportEncoder.ExecReportRejectTotal),
            SessionId, NextMsgSeqNum(), e.TransactTimeNanos,
            clOrdIdValue, origClOrdIdValue: 0, e.SecurityId, e.OrderIdOrZero,
            rej, e.TransactTimeNanos);
        return TryEnqueueExact(frame, n);
    }

    private bool TryEnqueueExact(byte[] frame, int written)
    {
        // Send loop writes the entire array, so we must hand it a tight buffer.
        // The encoder buffer (`frame`) was rented from ArrayPool and may be
        // larger than `written`. Copy out exactly `written` bytes into a fresh
        // non-pool array and return the rented buffer to the pool.
        var exact = new byte[written];
        Buffer.BlockCopy(frame, 0, exact, 0, written);
        ArrayPool<byte>.Shared.Return(frame);
        return _transport.TryEnqueueFrame(exact);
    }

    public bool WriteSessionReject(byte terminationCode)
    {
        if (!IsOpen) return false;
        var frame = ArrayPool<byte>.Shared.Rent(SessionRejectEncoder.TerminateTotal);
        int n = SessionRejectEncoder.EncodeTerminate(frame.AsSpan(0, SessionRejectEncoder.TerminateTotal),
            SessionId, 0, terminationCode);
        bool result = TryEnqueueExact(frame, n);
        Close();
        return result;
    }

    public bool WriteBusinessMessageReject(byte refMsgType, uint refSeqNum, ulong businessRejectRefId,
        uint businessRejectReason, string? text = null)
    {
        if (!IsOpen) return false;
        int textLen = string.IsNullOrEmpty(text) ? 0 : Math.Min(text.Length, BusinessMessageRejectEncoder.MaxTextLength);
        var frame = ArrayPool<byte>.Shared.Rent(BusinessMessageRejectEncoder.TotalSize(textLen));
        int n = BusinessMessageRejectEncoder.EncodeBusinessMessageRejectWithText(
            frame.AsSpan(0, BusinessMessageRejectEncoder.TotalSize(textLen)),
            SessionId, NextMsgSeqNum(), _nowNanos(),
            refMsgType, refSeqNum, businessRejectRefId, businessRejectReason, text);
        return TryEnqueueExact(frame, n);
    }

    private static byte MapRejectReason(RejectReason r) => r switch
    {
        // OrdRejReason wire codes: 0=BrokerExchangeOption (generic), 1=UnknownSymbol,
        // 3=OrderExceedsLimit, 5=UnknownOrder, 6=DuplicateOrder, 11=UnsupportedOrderCharacteristic.
        // Engine RejectReason values mapped to nearest wire code; unmapped => 0.
        _ => 0,
    };

    public void Close() => Close("close");

    /// <summary>
    /// Closes the session with a diagnostic reason. The reason is forwarded to
    /// the optional <c>onClosed</c> callback supplied at construction so the
    /// listener (or tests) can log it. <see cref="Close()"/> is the
    /// no-reason convenience overload.
    /// </summary>
    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0) return;
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

    public async ValueTask DisposeAsync()
    {
        Close("dispose");
        try { if (_recvTask != null) await _recvTask.ConfigureAwait(false); } catch { }
        try { if (_watchdogTask != null) await _watchdogTask.ConfigureAwait(false); } catch { }
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
