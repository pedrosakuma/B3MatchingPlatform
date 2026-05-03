using System.Buffers;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;

namespace B3.Exchange.Gateway;

/// <summary>
/// Inbound dispatch surface of <see cref="FixpSession"/>: receive loop,
/// frame decoder, FIXP control-plane handlers (Negotiate / Establish /
/// RetransmitRequest), business-frame validation, throttle, and the
/// liveness watchdog. Split out as a partial class file (issue #121,
/// step 4/4) so the FixpSession surface is grouped by concern instead
/// of one ~2000-line file. No public surface change; this is a
/// physical-organization refactor only.
/// </summary>
public sealed partial class FixpSession
{
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
            ApplyCodEstablishParams(in req);
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
        ApplyCodEstablishParams(in req);
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

    private void EnqueueSequence() => _retransmitController.EnqueueSequence();

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
}
