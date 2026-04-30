using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using B3.Exchange.Matching;

namespace B3.Exchange.EntryPoint;

/// <summary>
/// Stream-based per-connection session. Hosts:
///  - a receive loop that reads SBE-framed inbound messages and dispatches
///    decoded commands to <see cref="IEntryPointEngineSink"/>;
///  - a send loop that drains a bounded <see cref="Channel{T}"/> of
///    pre-encoded outbound frames and writes them to the stream.
///
/// Stream-based (rather than Socket-bound) so it can be exercised in tests
/// with <c>MemoryStream</c> or duplex pipes — see
/// <see cref="EntryPointListener"/> for the production accept loop.
///
/// Send-side backpressure: the channel is bounded; on overflow the channel
/// is closed (<c>FullMode = DropWrite</c> + explicit close). This drops the
/// connection rather than ballooning memory under a stuck peer.
/// </summary>
public sealed class EntryPointSession : IEntryPointResponseChannel, IAsyncDisposable
{
    private const int InboundHeaderSize = 8;
    private const int DefaultSendQueueCapacity = 1024;

    private readonly Stream _stream;
    private readonly IEntryPointEngineSink _sink;
    private readonly Channel<byte[]> _sendQueue;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _recvCts = new();
    private readonly Func<ulong> _nowNanos;
    private long _msgSeqNum;
    private int _isOpen = 1;
    private int _closingAfterFlush;
    private Task? _recvTask;
    private Task? _sendTask;

    public long ConnectionId { get; }
    public uint EnteringFirm { get; }
    public uint SessionId { get; }
    public bool IsOpen => Volatile.Read(ref _isOpen) == 1;

    public EntryPointSession(long connectionId, uint enteringFirm, uint sessionId,
        Stream stream, IEntryPointEngineSink sink, Func<ulong>? nowNanos = null,
        int sendQueueCapacity = DefaultSendQueueCapacity)
    {
        ConnectionId = connectionId;
        EnteringFirm = enteringFirm;
        SessionId = sessionId;
        _stream = stream;
        _sink = sink;
        _nowNanos = nowNanos ?? DefaultNowNanos;
        _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(sendQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    }

    public void Start()
    {
        _recvTask = Task.Run(() => RunReceiveLoopAsync(_recvCts.Token));
        _sendTask = Task.Run(() => RunSendLoopAsync(_cts.Token));
    }

    private static ulong DefaultNowNanos()
        => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        var headerBuf = new byte[InboundHeaderSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadExactlyAsync(_stream, headerBuf, ct).ConfigureAwait(false);
                if (!EntryPointFrameReader.TryParseInboundHeader(headerBuf, out var info, out var hdrErr))
                {
                    // Session-level fault: we cannot reliably resync the
                    // stream (no skip semantics for unknown templates / bad
                    // BlockLength). Send Terminate and close after flush.
                    _sink.OnDecodeError(this, hdrErr ?? "invalid header");
                    byte code = hdrErr != null && hdrErr.StartsWith("unsupported template", StringComparison.Ordinal)
                        ? SessionRejectEncoder.TerminationCode.UnrecognizedMessage
                        : SessionRejectEncoder.TerminationCode.DecodingError;
                    WriteSessionReject(code);
                    return;
                }
                var bodyBuf = ArrayPool<byte>.Shared.Rent(info.BodyLength);
                try
                {
                    await ReadExactlyAsync(_stream, bodyBuf.AsMemory(0, info.BodyLength), ct).ConfigureAwait(false);
                    var body = bodyBuf.AsSpan(0, info.BodyLength);
                    DispatchInbound(info, body);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bodyBuf);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        finally
        {
            // Receive side done. If we initiated a graceful shutdown
            // (close-after-flush), do NOT cancel the send loop; it must
            // drain pending frames first. Otherwise tear everything down.
            if (Volatile.Read(ref _closingAfterFlush) == 0)
                Close();
        }
    }

    private void DispatchInbound(EntryPointFrameReader.FrameInfo info, ReadOnlySpan<byte> body)
    {
        ulong now = _nowNanos();
        // InboundBusinessHeader: SessionID@0 (uint32) | MsgSeqNum@4 (uint32).
        // All supported inbound messages start with this header, so the
        // refSeqNum used for any reject is the uint32 at body offset 4.
        uint refSeqNum = body.Length >= 8 ? MemoryMarshal.Read<uint>(body.Slice(4, 4)) : 0u;
        // ClOrdID is at body offset 20 (uint64) for all supported templates;
        // surfaced on BusinessMessageReject as BusinessRejectRefID.
        ulong clOrdRef = body.Length >= 28 ? MemoryMarshal.Read<ulong>(body.Slice(20, 8)) : 0UL;
        switch (info.TemplateId)
        {
            case EntryPointFrameReader.TidSimpleNewOrder:
                if (InboundMessageDecoder.TryDecodeNewOrder(body, EnteringFirm, now, out var no, out var noClOrd, out var noErr))
                    _sink.EnqueueNewOrder(no, this, noClOrd);
                else SendBusinessReject(info.TemplateId, refSeqNum, clOrdRef, noErr ?? "decode error: SimpleNewOrder");
                break;
            case EntryPointFrameReader.TidSimpleModifyOrder:
                if (InboundMessageDecoder.TryDecodeReplace(body, now, out var rp, out var rpClOrd, out var rpOrigClOrd, out var rpErr))
                    _sink.EnqueueReplace(rp, this, rpClOrd, rpOrigClOrd);
                else SendBusinessReject(info.TemplateId, refSeqNum, clOrdRef, rpErr ?? "decode error: SimpleModifyOrder");
                break;
            case EntryPointFrameReader.TidOrderCancelRequest:
                if (InboundMessageDecoder.TryDecodeCancel(body, now, out var cn, out var cnClOrd, out _, out var cnErr))
                    _sink.EnqueueCancel(cn, this, cnClOrd);
                else SendBusinessReject(info.TemplateId, refSeqNum, clOrdRef, cnErr ?? "decode error: OrderCancelRequest");
                break;
            default:
                // ExpectedInboundBlockLength gates this branch — unreachable
                // unless someone adds a new TID without a decode arm.
                _sink.OnDecodeError(this, $"unsupported templateId={info.TemplateId}");
                WriteSessionReject(SessionRejectEncoder.TerminationCode.UnrecognizedMessage);
                break;
        }
    }

    private void SendBusinessReject(ushort templateId, uint refSeqNum, ulong clOrdRef, string reason)
    {
        _sink.OnDecodeError(this, reason);
        byte refMsg = BusinessMessageRejectEncoder.MapRefMsgTypeFromTemplateId(templateId);
        WriteBusinessMessageReject(refMsg, refSeqNum, clOrdRef,
            BusinessMessageRejectEncoder.Reason.InvalidField, reason);
    }

    private async Task RunSendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    Close();
                    return;
                }
                // Frames enqueued here are plain `new byte[]` (see TryEnqueueExact),
                // never pool-owned, so we do not return them to ArrayPool.
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Make sure the peer observes EOF once we've stopped sending —
            // particularly important after a graceful close-after-flush
            // (SessionReject) where we want the client to see the Terminate
            // followed by a clean disconnect.
            try { _stream.Dispose(); } catch { }
            Close();
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

    private bool TryEnqueue(byte[] frame)
    {
        if (!IsOpen) return false;
        if (_sendQueue.Writer.TryWrite(frame)) return true;
        Close();
        return false;
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

    public bool WriteExecutionReportCancel(in OrderCanceledEvent e, ulong clOrdIdValue)
    {
        if (!IsOpen) return false;
        var frame = ArrayPool<byte>.Shared.Rent(ExecutionReportEncoder.ExecReportCancelTotal);
        int n = ExecutionReportEncoder.EncodeExecReportCancel(frame.AsSpan(0, ExecutionReportEncoder.ExecReportCancelTotal),
            SessionId, NextMsgSeqNum(), e.TransactTimeNanos,
            e.Side, clOrdIdValue, origClOrdIdValue: 0, e.OrderId,
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

    public bool WriteSessionReject(byte terminationCode)
    {
        // Atomically transition open → closing-after-flush so concurrent
        // writers stop enqueuing once we've decided to terminate.
        if (Interlocked.Exchange(ref _isOpen, 0) == 0) return false;
        Volatile.Write(ref _closingAfterFlush, 1);

        var frame = new byte[SessionRejectEncoder.TerminateTotal];
        int n = SessionRejectEncoder.EncodeTerminate(frame, SessionId, sessionVerId: 0UL, terminationCode);
        // Bypass IsOpen guard: enqueue directly + complete writer so the
        // send loop drains this last frame and exits cleanly.
        var exact = new byte[n];
        Buffer.BlockCopy(frame, 0, exact, 0, n);
        bool queued = _sendQueue.Writer.TryWrite(exact);
        _sendQueue.Writer.TryComplete();
        // Stop the receive loop; we've already decided the session is dead.
        try { _recvCts.Cancel(); } catch { }
        return queued;
    }

    public bool WriteBusinessMessageReject(byte refMsgType, uint refSeqNum, ulong businessRejectRefId,
        uint businessRejectReason, string? text = null)
    {
        if (!IsOpen) return false;
        int needed = BusinessMessageRejectEncoder.TotalSize(
            text == null ? 0 : Math.Min(text.Length, BusinessMessageRejectEncoder.MaxTextLength));
        var frame = ArrayPool<byte>.Shared.Rent(needed);
        int n = BusinessMessageRejectEncoder.EncodeBusinessMessageRejectWithText(frame.AsSpan(0, needed),
            SessionId, NextMsgSeqNum(), _nowNanos(),
            refMsgType, refSeqNum, businessRejectRefId, businessRejectReason, text);
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
        return TryEnqueue(exact);
    }

    private static byte MapRejectReason(RejectReason r) => r switch
    {
        // OrdRejReason wire codes: 0=BrokerExchangeOption (generic), 1=UnknownSymbol,
        // 3=OrderExceedsLimit, 5=UnknownOrder, 6=DuplicateOrder, 11=UnsupportedOrderCharacteristic.
        // Engine RejectReason values mapped to nearest wire code; unmapped => 0.
        _ => 0,
    };

    public void Close()
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0)
        {
            // Already marked closed (possibly by graceful shutdown). Force
            // the cancellation path so any blocking receive/send returns.
            _sendQueue.Writer.TryComplete();
            try { _recvCts.Cancel(); } catch { }
            try { _cts.Cancel(); } catch { }
            return;
        }
        _sendQueue.Writer.TryComplete();
        try { _recvCts.Cancel(); } catch { }
        try { _cts.Cancel(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try { if (_recvTask != null) await _recvTask.ConfigureAwait(false); } catch { }
        try { if (_sendTask != null) await _sendTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
        _recvCts.Dispose();
    }
}
