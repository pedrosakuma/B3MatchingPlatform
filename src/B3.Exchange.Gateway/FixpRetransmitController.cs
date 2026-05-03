using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using B3.Entrypoint.Fixp.Sbe.V6;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Gateway;

/// <summary>
/// Owns the FIXP retransmission control plane for a <see cref="FixpSession"/>:
/// the periodic <c>Sequence</c> heartbeat (spec §4.5.5) and the
/// <c>RetransmitRequest → Retransmission/Sequence</c> replay block
/// (spec §4.5.6, issue #46).
///
/// <para>Extracted from <see cref="FixpSession"/> as part of issue #121,
/// step 2/4. Shares mutable state with the owning session
/// (<c>_transport</c>, <c>_outboundLock</c>, <c>_retxBuffer</c>, message-seq
/// peek) via constructor-injected accessors so behavior is identical to the
/// pre-refactor inline implementation.</para>
/// </summary>
internal sealed class FixpRetransmitController
{
    private readonly Func<uint> _sessionId;
    private readonly Func<TcpTransport> _transport;
    private readonly RetransmitBuffer _retxBuffer;
    private readonly object _outboundLock;
    private readonly int _sendQueueCapacity;
    private readonly Func<bool> _isOpen;
    private readonly Func<uint> _peekNextMsgSeqNum;
    private readonly Func<FixpEvent, FixpAction> _applyTransition;
    private readonly Func<FixpState> _getState;
    private readonly ILogger _logger;
    private readonly long _connectionId;

    /// <summary>One-outstanding-request gate. 0 = idle, 1 = a replay block
    /// is mid-enqueue. CAS-guarded so a concurrent request can be rejected
    /// with <c>RETRANSMIT_IN_PROGRESS</c> rather than allowed to interleave.</summary>
    private int _retxInProgress;

    public FixpRetransmitController(
        Func<uint> sessionId,
        Func<TcpTransport> transport,
        RetransmitBuffer retxBuffer,
        object outboundLock,
        int sendQueueCapacity,
        Func<bool> isOpen,
        Func<uint> peekNextMsgSeqNum,
        Func<FixpEvent, FixpAction> applyTransition,
        Func<FixpState> getState,
        ILogger logger,
        long connectionId)
    {
        _sessionId = sessionId;
        _transport = transport;
        _retxBuffer = retxBuffer;
        _outboundLock = outboundLock;
        _sendQueueCapacity = sendQueueCapacity;
        _isOpen = isOpen;
        _peekNextMsgSeqNum = peekNextMsgSeqNum;
        _applyTransition = applyTransition;
        _getState = getState;
        _logger = logger;
        _connectionId = connectionId;
    }

    /// <summary>
    /// Enqueues a FIXP <c>Sequence</c> heartbeat frame announcing
    /// <see cref="FixpSession.PeekNextMsgSeqNum"/>. Spec §4.5.5: this
    /// message announces the *next* MsgSeqNum we intend to send and
    /// does not consume a sequence number itself, so we peek instead
    /// of incrementing — otherwise an idle heartbeat before Establish
    /// would cause the EstablishAck.nextSeqNo to be off by one.
    ///
    /// <para>Outbound lock: serializes against the replay block in
    /// <see cref="ProcessAndEnqueueRetransmitRequest"/> so a watchdog-driven
    /// heartbeat cannot land inside the Retransmission/replay/Sequence
    /// burst on the wire (gpt-5.5 review #1).</para>
    /// </summary>
    public void EnqueueSequence()
    {
        if (!_isOpen()) return;
        var frame = new byte[SessionFrameEncoder.SequenceTotal];
        lock (_outboundLock)
        {
            SessionFrameEncoder.EncodeSequence(frame, _peekNextMsgSeqNum());
            _transport().TryEnqueueFrame(frame);
        }
    }

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
    /// (i.e. <see cref="FixpSession.PeekNextMsgSeqNum"/>). The entire
    /// block is enqueued under <see cref="_outboundLock"/> so live
    /// business writes cannot interleave it on the wire and so the
    /// trailing Sequence's seq matches what the peer will see next.</para>
    /// </summary>
    public void ProcessAndEnqueueRetransmitRequest(ReadOnlySpan<byte> fixedBlock)
    {
        // Layout (BLOCK_LENGTH=20): SessionID(4) | Timestamp(8 ulong) |
        // FromSeqNo(4 uint) | Count(4 uint).
        if (fixedBlock.Length < 20) return; // header validator already enforced this; defensive
        uint reqSessionId = BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(0, 4));
        ulong reqTimestamp = BinaryPrimitives.ReadUInt64LittleEndian(fixedBlock.Slice(4, 8));
        uint fromSeq = BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(12, 4));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(16, 4));

        var action = _applyTransition(FixpEvent.RetransmitRequest);
        if (action != FixpAction.Replay)
        {
            // DropSilently / DeferredToReestablish / Terminal: per spec,
            // emit nothing. Suspended sessions have no live transport
            // anyway; a request that arrives in any pre-Established state
            // is simply ignored and the peer is expected to drive the
            // handshake before requesting recovery.
            _logger.LogDebug("session {ConnectionId} retransmit-request action={Action} state={State} dropped",
                _connectionId, action, _getState());
            return;
        }

        // Spec validations BEFORE single-in-flight gate so a malformed
        // request doesn't leave the gate held.
        if (reqSessionId != _sessionId())
        {
            EnqueueRetransmitReject(reqTimestamp, RetransmitRejectCode.INVALID_SESSION);
            return;
        }
        if (reqTimestamp == 0UL)
        {
            EnqueueRetransmitReject(reqTimestamp, RetransmitRejectCode.INVALID_TIMESTAMP);
            return;
        }

        // One-outstanding-request gate. Cleared in the finally below
        // after the entire replay block is enqueued under _outboundLock,
        // so a subsequent request can never see partial state.
        if (Interlocked.CompareExchange(ref _retxInProgress, 1, 0) != 0)
        {
            EnqueueRetransmitReject(reqTimestamp, RetransmitRejectCode.RETRANSMIT_IN_PROGRESS);
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
                var transport = _transport();
                if (transport.SendQueueDepth + needed > _sendQueueCapacity)
                {
                    EnqueueRetransmitReject(reqTimestamp, RetransmitRejectCode.SYSTEM_BUSY);
                    return;
                }

                var headerFrame = new byte[RetransmissionEncoder.RetransmissionTotal];
                RetransmissionEncoder.EncodeRetransmission(headerFrame,
                    _sessionId(), reqTimestamp,
                    firstRetransmittedSeqNo: snap.FirstSeq, count: snap.ActualCount);
                transport.TryEnqueueFrame(headerFrame);

                for (int i = 0; i < snap.Frames.Length; i++)
                    transport.TryEnqueueFrame(snap.Frames[i]);

                var trailer = new byte[SessionFrameEncoder.SequenceTotal];
                SessionFrameEncoder.EncodeSequence(trailer, _peekNextMsgSeqNum());
                transport.TryEnqueueFrame(trailer);
            }
        }
        finally
        {
            Volatile.Write(ref _retxInProgress, 0);
        }
    }

    private void EnqueueRetransmitReject(ulong requestTimestampNanos, RetransmitRejectCode code)
    {
        var frame = new byte[RetransmissionEncoder.RetransmitRejectTotal];
        RetransmissionEncoder.EncodeRetransmitReject(frame, _sessionId(), requestTimestampNanos, code);
        _transport().TryEnqueueFrame(frame);
        _logger.LogInformation("session {ConnectionId} retransmit-reject code={Code}",
            _connectionId, code);
    }
}
