using B3.EntryPoint.Wire;
using System.Buffers;
using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;

namespace B3.Exchange.Gateway;


/// <summary>
/// Inbound dispatcher: routes a decoded FIXP frame to the handler
/// for its template id (FIXP control plane vs. business templates).
/// Split out from FixpSession.Inbound.cs (issue #139) so the recv-
/// loop file does not exceed the per-file LOC budget. No public
/// surface change.
/// </summary>
public sealed partial class FixpSession
{
    private async Task<bool> DispatchInboundAsync(EntryPointFrameReader.FrameInfo info, byte[] bodyBuf, int bodyLength)
    {
        // Issue #175: open the root span for this inbound frame. All
        // downstream spans (dispatch.enqueue, engine.process,
        // outbound.emit) attach as descendants via Activity.Current
        // propagation + the explicit ParentContext stamped on each
        // WorkItem. SpanKind.Server because the gateway is the entry
        // point of the request from the client's POV.
        using var decodeSpan = ExchangeTelemetry.Source.StartActivity(
            ExchangeTelemetry.SpanGatewayDecode,
            System.Diagnostics.ActivityKind.Server);
        if (decodeSpan is not null)
        {
            decodeSpan.SetTag(ExchangeTelemetry.TagSession, ConnectionId);
            decodeSpan.SetTag(ExchangeTelemetry.TagTemplate, (int)info.TemplateId);
        }

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
                    if (!_sink.EnqueueNewOrder(no, Identity, EnteringFirm, noClOrd))
                        WriteSystemBusyReject(info.TemplateId, fixedBlock, noClOrd, "New");
                    return true;
                }
                _sink.OnDecodeError(Identity, noErr ?? "decode error: SimpleNewOrder");
                await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:SimpleNewOrder").ConfigureAwait(false);
                return false;
            case EntryPointFrameReader.TidSimpleModifyOrder:
                if (InboundMessageDecoder.TryDecodeReplace(fixedBlock, now, out var rp, out var rpClOrd, out var rpOrigClOrd, out var rpErr))
                {
                    if (!_sink.EnqueueReplace(rp, Identity, EnteringFirm, rpClOrd, rpOrigClOrd))
                        WriteSystemBusyReject(info.TemplateId, fixedBlock, rpClOrd, "Replace");
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
                        if (!_sink.EnqueueNewOrder(nos, Identity, EnteringFirm, nosClOrd))
                            WriteSystemBusyReject(info.TemplateId, fixedBlock, nosClOrd, "New");
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
                        if (!_sink.EnqueueReplace(ocr, Identity, EnteringFirm, ocrClOrd, ocrOrigClOrd))
                            WriteSystemBusyReject(info.TemplateId, fixedBlock, ocrClOrd, "Replace");
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
                        if (!_sink.EnqueueCross(cross, Identity, EnteringFirm))
                            WriteSystemBusyReject(info.TemplateId, fixedBlock, crossId, "Cross");
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
                    if (!_sink.EnqueueCancel(cn, Identity, EnteringFirm, cnClOrd, cnOrigClOrd))
                        WriteSystemBusyReject(info.TemplateId, fixedBlock, cnClOrd, "Cancel");
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
                        // Spec §4.8 — under normal load we ack synchronously
                        // with OrderMassActionReport(ACCEPTED) ahead of the
                        // per-order ER_Cancel frames the engine emits later.
                        // This ordering matters: the ACCEPTED ack travels
                        // straight to the TCP stream from the gateway thread
                        // while the ER_Cancel frames go through the
                        // dispatcher's UMDF-packet path, so reversing it
                        // would let cancels race ahead. We therefore keep
                        // ACCEPTED-first and, if the enqueue then fails due
                        // to backpressure (#153), follow up with a
                        // BusinessMessageReject(SystemBusy) so the peer
                        // knows the action will not be performed.
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
                        if (!_sink.EnqueueMassCancel(mcCmd, Identity, EnteringFirm))
                        {
                            WriteSystemBusyReject(
                                templateId: EntryPointFrameReader.TidOrderMassActionRequest,
                                fixedBlock: fixedBlock,
                                clOrdId: mcClOrd,
                                workKindLabel: "MassCancel");
                        }
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

    private static bool IsApplicationTemplate(ushort templateId)
        => templateId == EntryPointFrameReader.TidSimpleNewOrder
        || templateId == EntryPointFrameReader.TidSimpleModifyOrder
        || templateId == EntryPointFrameReader.TidNewOrderSingle
        || templateId == EntryPointFrameReader.TidOrderCancelReplaceRequest
        || templateId == EntryPointFrameReader.TidOrderCancelRequest
        || templateId == EntryPointFrameReader.TidNewOrderCross
        || templateId == EntryPointFrameReader.TidOrderMassActionRequest;
}
