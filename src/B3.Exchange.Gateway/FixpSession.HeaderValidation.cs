using B3.EntryPoint.Wire;
using System.Buffers;
using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;

namespace B3.Exchange.Gateway;


/// <summary>
/// Per-frame business-header validation: inbound throttle window,
/// session-id check, and outbound MsgSeqNum check + reject. Split
/// out from FixpSession.Inbound.cs (issue #139).
/// </summary>
public sealed partial class FixpSession
{
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
}
