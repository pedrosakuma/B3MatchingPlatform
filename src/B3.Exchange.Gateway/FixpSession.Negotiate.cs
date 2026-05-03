using B3.EntryPoint.Wire;
using System.Buffers;
using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;

namespace B3.Exchange.Gateway;


/// <summary>
/// FIXP Negotiate handler: validates the inbound Negotiate frame,
/// claims the wire SessionID, builds either NegotiateResponse or
/// NegotiateReject, and applies the state-machine transition. Split
/// out from FixpSession.Inbound.cs (issue #139).
/// </summary>
public sealed partial class FixpSession
{
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
}
