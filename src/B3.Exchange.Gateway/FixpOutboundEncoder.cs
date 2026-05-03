using B3.EntryPoint.Wire;
using System.Buffers.Binary;
using B3.Exchange.Matching;

namespace B3.Exchange.Gateway;

/// <summary>
/// Owns the "encode → append-to-retx → enqueue-on-transport" pipeline for
/// every business frame a <see cref="FixpSession"/> emits (ExecutionReports,
/// OrderMassActionReport, BusinessMessageReject, SessionReject/Terminate).
///
/// <para>Extracted from <see cref="FixpSession"/> as part of issue #121 to
/// keep the session class focused on FIXP lifecycle / state-machine concerns.
/// The encoder shares mutable state with its owning session (<c>_msgSeqNum</c>,
/// <c>_outboundLock</c>, <c>_retxBuffer</c>, the live transport reference)
/// via constructor-injected accessors, so behavior is identical to the
/// pre-refactor inline implementation.</para>
///
/// <para>All <c>Write*</c> methods are safe to call from any thread; they
/// serialize on the shared <c>_outboundLock</c> object exactly as the
/// previous inline code did.</para>
/// </summary>
internal sealed class FixpOutboundEncoder
{
    private readonly Func<uint> _sessionId;
    private readonly Func<uint> _nextMsgSeqNum;
    private readonly Func<TcpTransport> _transport;
    private readonly RetransmitBuffer _retxBuffer;
    private readonly object _outboundLock;
    private readonly Func<ulong> _nowNanos;
    private readonly Func<bool> _isOpen;
    private readonly Action _close;

    /// <summary>Per-session monotonic counter sourcing
    /// <c>OrderMassActionReport.MassActionReportID</c> (template 702).
    /// Spec requires an engine-assigned unique id; deriving it from a
    /// per-session counter is sufficient because the (sessionId, id)
    /// pair is globally unique.</summary>
    private long _massActionReportSeq;

    public FixpOutboundEncoder(
        Func<uint> sessionId,
        Func<uint> nextMsgSeqNum,
        Func<TcpTransport> transport,
        RetransmitBuffer retxBuffer,
        object outboundLock,
        Func<ulong> nowNanos,
        Func<bool> isOpen,
        Action close)
    {
        _sessionId = sessionId;
        _nextMsgSeqNum = nextMsgSeqNum;
        _transport = transport;
        _retxBuffer = retxBuffer;
        _outboundLock = outboundLock;
        _nowNanos = nowNanos;
        _isOpen = isOpen;
        _close = close;
    }

    public bool WriteExecutionReportNew(in OrderAcceptedEvent e, ulong receivedTimeNanos = ulong.MaxValue)
    {
        if (!_isOpen()) return false;
        ulong clOrd = ulong.TryParse(e.ClOrdId, out var v) ? v : 0;
        var exact = new byte[ExecutionReportEncoder.ExecReportNewTotal];
        lock (_outboundLock)
        {
            ExecutionReportEncoder.EncodeExecReportNew(exact,
                _sessionId(), _nextMsgSeqNum(), e.InsertTimestampNanos,
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
        if (!_isOpen()) return false;
        var side = isAggressor ? e.AggressorSide : (e.AggressorSide == Side.Buy ? Side.Sell : Side.Buy);
        var exact = new byte[ExecutionReportEncoder.ExecReportTradeTotal];
        lock (_outboundLock)
        {
            ExecutionReportEncoder.EncodeExecReportTrade(exact,
                _sessionId(), _nextMsgSeqNum(), e.TransactTimeNanos,
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
        if (!_isOpen()) return false;
        var exact = new byte[ExecutionReportEncoder.ExecReportCancelTotal];
        lock (_outboundLock)
        {
            ExecutionReportEncoder.EncodeExecReportCancel(exact,
                _sessionId(), _nextMsgSeqNum(), e.TransactTimeNanos,
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
        if (!_isOpen()) return false;
        var exact = new byte[ExecutionReportEncoder.ExecReportModifyTotal];
        lock (_outboundLock)
        {
            ExecutionReportEncoder.EncodeExecReportModify(exact,
                _sessionId(), _nextMsgSeqNum(), transactTimeNanos,
                side, clOrdIdValue, origClOrdIdValue, orderId,
                securityId, orderId, (ulong)rptSeq, transactTimeNanos,
                leavesQty: newRemainingQty, cumQty: 0, orderQty: newRemainingQty, priceMantissa: newPriceMantissa,
                receivedTimeNanos: receivedTimeNanos);
            return AppendAndEnqueueLocked(exact);
        }
    }

    /// <summary>
    /// Encodes and enqueues an <c>OrderMassActionReport</c> (template 702,
    /// spec §4.8 / #GAP-19) acknowledging — or rejecting — an inbound
    /// <c>OrderMassActionRequest</c>.
    /// </summary>
    public bool WriteOrderMassActionReport(ulong clOrdIdValue, byte massActionResponse,
        byte? massActionRejectReason, byte? side, long securityId, ulong transactTimeNanos,
        string? text = null)
    {
        if (!_isOpen()) return false;
        ulong reportId = (ulong)Interlocked.Increment(ref _massActionReportSeq);
        int textLen = string.IsNullOrEmpty(text) ? 0 : Math.Min(text!.Length, OrderMassActionReportEncoder.MaxTextLength);
        var exact = new byte[OrderMassActionReportEncoder.TotalSize(textLen)];
        lock (_outboundLock)
        {
            OrderMassActionReportEncoder.EncodeOrderMassActionReportWithText(exact,
                _sessionId(), _nextMsgSeqNum(), transactTimeNanos,
                clOrdIdValue, reportId, transactTimeNanos,
                massActionResponse, massActionRejectReason, side, securityId, text);
            return AppendAndEnqueueLocked(exact);
        }
    }

    public bool WriteExecutionReportReject(in RejectEvent e, ulong clOrdIdValue)
    {
        if (!_isOpen()) return false;
        uint rej = MapRejectReason(e.Reason);
        var exact = new byte[ExecutionReportEncoder.ExecReportRejectTotal];
        lock (_outboundLock)
        {
            ExecutionReportEncoder.EncodeExecReportReject(exact,
                _sessionId(), _nextMsgSeqNum(), e.TransactTimeNanos,
                clOrdIdValue, origClOrdIdValue: 0, e.SecurityId, e.OrderIdOrZero,
                rej, e.TransactTimeNanos);
            return AppendAndEnqueueLocked(exact);
        }
    }

    public bool WriteSessionReject(byte terminationCode)
    {
        if (!_isOpen()) return false;
        var exact = new byte[SessionRejectEncoder.TerminateTotal];
        SessionRejectEncoder.EncodeTerminate(exact, _sessionId(), 0, terminationCode);
        bool result = _transport().TryEnqueueFrame(exact);
        _close();
        return result;
    }

    public bool WriteBusinessMessageReject(byte refMsgType, uint refSeqNum, ulong businessRejectRefId,
        uint businessRejectReason, string? text = null)
    {
        if (!_isOpen()) return false;
        int textLen = string.IsNullOrEmpty(text) ? 0 : Math.Min(text.Length, BusinessMessageRejectEncoder.MaxTextLength);
        int total = BusinessMessageRejectEncoder.TotalSize(textLen);
        var exact = new byte[total];
        lock (_outboundLock)
        {
            BusinessMessageRejectEncoder.EncodeBusinessMessageRejectWithText(
                exact, _sessionId(), _nextMsgSeqNum(), _nowNanos(),
                refMsgType, refSeqNum, businessRejectRefId, businessRejectReason, text);
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
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(
            exact.AsSpan(EntryPointFrameReader.WireHeaderSize + 4, 4));
        // Append BEFORE enqueue: per spec recovery semantics, any seq
        // that has been allocated MUST be replayable, even if the local
        // send queue rejects the frame (gpt-5.5 critique #9).
        _retxBuffer.Append(seq, exact);
        return _transport().TryEnqueueFrame(exact);
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
}
