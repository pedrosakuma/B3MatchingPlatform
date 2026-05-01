using System.Buffers;
using System.Threading.Channels;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Gateway;

/// <summary>
/// Stream-based per-connection session. Hosts:
///  - a receive loop that reads SBE-framed inbound messages and dispatches
///    decoded commands to <see cref="IInboundCommandSink"/>;
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
public sealed class EntryPointSession : IGatewayResponseChannel, IAsyncDisposable
{
    private const int InboundHeaderSize = EntryPointFrameReader.WireHeaderSize;
    private const int DefaultSendQueueCapacity = 1024;

    private readonly Stream _stream;
    private readonly IInboundCommandSink _sink;
    private readonly ILogger<EntryPointSession> _logger;
    private readonly Channel<byte[]> _sendQueue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<ulong> _nowNanos;
    private readonly EntryPointSessionOptions _options;
    private readonly Action<EntryPointSession, string>? _onClosed;
    private readonly SemaphoreSlim _streamWriteLock = new(1, 1);
    private long _msgSeqNum;
    private int _isOpen = 1;
    // Watchdog state (milliseconds since process start, monotonic).
    private long _lastInboundMs;
    private long _lastOutboundMs;
    // Set true while we are waiting on the grace window after sending a probe;
    // cleared as soon as any inbound frame arrives. Prevents flooding probes.
    private int _probeOutstanding;
    private Task? _recvTask;
    private Task? _sendTask;
    private Task? _watchdogTask;

    public long ConnectionId { get; }
    public uint EnteringFirm { get; }
    public uint SessionId { get; }
    public bool IsOpen => Volatile.Read(ref _isOpen) == 1;

    /// <summary>
    /// Approximate number of pre-encoded ExecutionReport frames sitting in
    /// the outbound queue, for /metrics scraping. Reads
    /// <see cref="System.Threading.Channels.ChannelReader{T}.Count"/>,
    /// which is O(1) on a bounded channel.
    /// </summary>
    public int SendQueueDepth => _sendQueue.Reader.Count;

    public EntryPointSession(long connectionId, uint enteringFirm, uint sessionId,
        Stream stream, IInboundCommandSink sink, ILogger<EntryPointSession> logger,
        Func<ulong>? nowNanos = null,
        int sendQueueCapacity = DefaultSendQueueCapacity,
        EntryPointSessionOptions? options = null,
        Action<EntryPointSession, string>? onClosed = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ConnectionId = connectionId;
        EnteringFirm = enteringFirm;
        SessionId = sessionId;
        _stream = stream;
        _sink = sink;
        _logger = logger;
        _nowNanos = nowNanos ?? DefaultNowNanos;
        _options = options ?? EntryPointSessionOptions.Default;
        _options.Validate();
        _onClosed = onClosed;
        _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(sendQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        long now = NowMs();
        Volatile.Write(ref _lastInboundMs, now);
        Volatile.Write(ref _lastOutboundMs, now);
    }

    public void Start()
    {
        _logger.LogInformation("entrypoint session opened: connectionId={ConnectionId} sessionId={SessionId} firm={EnteringFirm}",
            ConnectionId, SessionId, EnteringFirm);
        _recvTask = Task.Run(() => RunReceiveLoopAsync(_cts.Token));
        _sendTask = Task.Run(() => RunSendLoopAsync(_cts.Token));
        _watchdogTask = Task.Run(() => RunWatchdogLoopAsync(_cts.Token));
    }

    private static ulong DefaultNowNanos()
        => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;

    private static long NowMs() => Environment.TickCount64;

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        var headerBuf = new byte[InboundHeaderSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadExactlyAsync(_stream, headerBuf, ct).ConfigureAwait(false);
                if (!EntryPointFrameReader.TryParseInboundHeader(headerBuf, out var info, out var headerError, out var hdrMsg))
                {
                    _sink.OnDecodeError(this, hdrMsg ?? headerError.ToString());
                    await TerminateAndCloseAsync(MapHeaderErrorToTerminationCode(headerError), $"decode-error:{headerError}").ConfigureAwait(false);
                    return;
                }
                var bodyBuf = ArrayPool<byte>.Shared.Rent(info.BodyLength);
                try
                {
                    await ReadExactlyAsync(_stream, bodyBuf.AsMemory(0, info.BodyLength), ct).ConfigureAwait(false);
                    // Any well-framed inbound frame counts as liveness, including
                    // session-layer Sequence (heartbeat) frames.
                    Volatile.Write(ref _lastInboundMs, NowMs());
                    Volatile.Write(ref _probeOutstanding, 0);
                    var body = bodyBuf.AsSpan(0, info.BodyLength);
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
            _logger.LogWarning(ex, "entrypoint session {ConnectionId} receive IO error", ConnectionId);
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
    /// directly to the underlying stream (bypassing the send queue, which
    /// might be racing with an in-flight write or about to be cancelled),
    /// and closes the session. Acquires <see cref="_streamWriteLock"/> so
    /// it does not collide with the regular send loop.
    /// </summary>
    private async Task TerminateAndCloseAsync(byte terminationCode, string reason)
    {
        if (!IsOpen) { Close(reason); return; }
        var frame = new byte[SessionRejectEncoder.TerminateTotal];
        SessionRejectEncoder.EncodeTerminate(frame, SessionId, 0, terminationCode);
        try
        {
            await _streamWriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(frame).ConfigureAwait(false);
                Volatile.Write(ref _lastOutboundMs, NowMs());
            }
            finally
            {
                _streamWriteLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "entrypoint session {ConnectionId} failed to write Terminate({Code})", ConnectionId, terminationCode);
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
            _sink.OnDecodeError(this, varErr ?? "decode error: varData");
            await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:varData").ConfigureAwait(false);
            return false;
        }

        switch (info.TemplateId)
        {
            case EntryPointFrameReader.TidSequence:
                // Session-layer heartbeat / sequence sync. Liveness already
                // recorded by the receive loop; nothing else to do.
                return true;
            case EntryPointFrameReader.TidSimpleNewOrder:
                if (InboundMessageDecoder.TryDecodeNewOrder(fixedBlock, EnteringFirm, now, out var no, out var noClOrd, out var noErr))
                {
                    _sink.EnqueueNewOrder(no, this, noClOrd);
                    return true;
                }
                _sink.OnDecodeError(this, noErr ?? "decode error: SimpleNewOrder");
                await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:SimpleNewOrder").ConfigureAwait(false);
                return false;
            case EntryPointFrameReader.TidSimpleModifyOrder:
                if (InboundMessageDecoder.TryDecodeReplace(fixedBlock, now, out var rp, out var rpClOrd, out var rpOrigClOrd, out var rpErr))
                {
                    _sink.EnqueueReplace(rp, this, rpClOrd, rpOrigClOrd);
                    return true;
                }
                _sink.OnDecodeError(this, rpErr ?? "decode error: SimpleModifyOrder");
                await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:SimpleModifyOrder").ConfigureAwait(false);
                return false;
            case EntryPointFrameReader.TidOrderCancelRequest:
                if (InboundMessageDecoder.TryDecodeCancel(fixedBlock, now, out var cn, out var cnClOrd, out var cnOrigClOrd, out var cnErr))
                {
                    _sink.EnqueueCancel(cn, this, cnClOrd, cnOrigClOrd);
                    return true;
                }
                _sink.OnDecodeError(this, cnErr ?? "decode error: OrderCancelRequest");
                await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.DecodingError, "decode-error:OrderCancelRequest").ConfigureAwait(false);
                return false;
            default:
                // Unreachable: TryParseInboundHeader has already screened out
                // unknown templates and returned UnsupportedTemplate.
                _sink.OnDecodeError(this, $"unsupported templateId={info.TemplateId}");
                await TerminateAndCloseAsync(SessionRejectEncoder.TerminationCode.UnrecognizedMessage, "decode-error:unsupported").ConfigureAwait(false);
                return false;
        }
    }

    private async Task RunSendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _streamWriteLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _streamWriteLock.Release();
                    }
                    Volatile.Write(ref _lastOutboundMs, NowMs());
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "entrypoint session {ConnectionId} send IO error; closing", ConnectionId);
                    Close("send-io-error");
                    return;
                }
                // Frames enqueued here are plain `new byte[]` (see TryEnqueueExact),
                // never pool-owned, so we do not return them to ArrayPool.
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Close("send-loop-exit");
        }
    }

    /// <summary>
    /// Periodic watchdog that drives the FIXP-style heartbeat + idle-timeout
    /// policy. Runs on its own task; never touches the socket directly —
    /// instead it enqueues <c>Sequence</c> frames into the same bounded
    /// channel the send loop drains, so all writes remain serialised.
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
                long sinceOut = now - Volatile.Read(ref _lastOutboundMs);

                // Idle teardown: if a probe is outstanding and grace elapsed
                // without any inbound, close. The grace clock starts from the
                // moment the probe was sent (i.e. _lastOutboundMs was bumped),
                // so we measure the additional silence beyond idleTimeoutMs.
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
        TryEnqueue(frame);
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
        Close("send-queue-full");
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
        return TryEnqueue(exact);
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
        _logger.LogInformation("entrypoint session {ConnectionId} closing", ConnectionId);
        _sendQueue.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        // Notify the engine sink so it releases any cached references to this
        // session (see IInboundCommandSink.OnSessionClosed). Without this the
        // ChannelDispatcher's order-owners map keeps the session rooted for
        // the lifetime of every resting order it placed → unbounded memory.
        try { _sink.OnSessionClosed(this); } catch { }
        try { _onClosed?.Invoke(this, reason); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        Close("dispose");
        try { if (_recvTask != null) await _recvTask.ConfigureAwait(false); } catch { }
        try { if (_sendTask != null) await _sendTask.ConfigureAwait(false); } catch { }
        try { if (_watchdogTask != null) await _watchdogTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
