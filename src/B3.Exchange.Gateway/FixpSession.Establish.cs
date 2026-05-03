using B3.EntryPoint.Wire;
using System.Buffers;
using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;

namespace B3.Exchange.Gateway;


/// <summary>
/// FIXP Establish handler: validates the inbound Establish frame,
/// stamps keep-alive + cancel-on-disconnect parameters, builds
/// EstablishAck or EstablishReject, and applies the state-machine
/// transition. Split out from FixpSession.Inbound.cs (issue #139).
/// </summary>
public sealed partial class FixpSession
{
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
}
