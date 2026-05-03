using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using B3.Exchange.Gateway;
using B3.Exchange.SyntheticTrader.Fixp;

namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Lightweight EntryPoint TCP client used by the synthetic trader.
///
/// Connects to an EntryPoint listener, encodes outbound SimpleNewOrderV2 /
/// OrderCancelRequest frames (matching the offsets in
/// <c>InboundMessageDecoder</c>), and decodes inbound ExecutionReport
/// frames into typed events. Cancellation propagates cleanly through both
/// the receive loop and the send queue, so the caller can shut the client
/// down deterministically (used by integration tests).
///
/// Field offsets are duplicated verbatim from the host's
/// <c>InboundMessageDecoder</c> / <c>ExecutionReportEncoder</c>; the issue
/// instructions explicitly accept this duplication ("otherwise duplicate
/// the encoder calls (small)").
/// </summary>
public sealed class EntryPointClient : IAsyncDisposable
{
    // SimpleNewOrderV2 body offsets (BlockLength=82) — mirror of InboundMessageDecoder.
    private const int NewOrderClOrdID = 20;
    private const int NewOrderSecurityID = 48;
    private const int NewOrderSide = 56;
    private const int NewOrderOrdType = 57;
    private const int NewOrderTif = 58;
    private const int NewOrderQty = 60;
    private const int NewOrderPrice = 68;
    private const int NewOrderBlockLen = 82;

    // OrderCancelRequest body offsets (BlockLength=76).
    private const int CancelClOrdID = 20;
    private const int CancelSecurityID = 28;
    private const int CancelOrderID = 36;
    private const int CancelOrigClOrdID = 44;
    private const int CancelSide = 52;
    private const int CancelBlockLen = 76;

    // Inbound ExecutionReport body offsets we care about.
    private const int ErClOrdId = 20;             // all ER templates
    private const int ErSecurityId = 36;
    private const int ErSide = 18;
    // ER_New (200): OrderID @ 44.
    private const int ErNewOrderId = 44;
    // ER_Trade (203): LastQty @ 48, LastPx @ 56, LeavesQty @ 80, CumQty @ 88, OrderID @ 108.
    private const int ErTradeLastQty = 48;
    private const int ErTradeLastPx = 56;
    private const int ErTradeLeavesQty = 80;
    private const int ErTradeCumQty = 88;
    private const int ErTradeOrderId = 108;
    // ER_Cancel (202): OrderID @ 80.
    private const int ErCancelOrderId = 80;
    // ER_Reject (204): OrdRejReason @ 44, OrderID @ 64, OrigClOrdID @ 72.
    private const int ErRejectReason = 44;
    private const int ErRejectOrderId = 64;
    private const int ErRejectOrigClOrdId = 72;

    // Maximum ER body block length we are willing to allocate from the wire.
    // The largest known ExecutionReport block (ER_Modify) is 160 bytes; we
    // allow a generous upper bound to absorb minor schema bumps but reject
    // pathological values that would let a malformed peer force giant
    // allocations and OOM the process.
    private const int MaxAcceptedBlockLength = 1024;

    // Per-template expected block lengths (mirroring ExecutionReportEncoder
    // constants in B3.Exchange.Gateway, which is internal). Used as a
    // tighter, schema-aware validation before allocating the body buffer.
    // ER_New/Modify/Cancel were bumped to V3 in #49 (#GAP-11) to carry the
    // optional `receivedTime` (tag 35544) trailing field; ER_Trade and
    // ER_Reject continue to ride V2.
    // Returns -1 for unknown template IDs (caller falls back to MaxAcceptedBlockLength).
    private static int ExpectedBlockLength(ushort templateId) => templateId switch
    {
        EntryPointFrameReader.TidExecutionReportNew => 172,
        EntryPointFrameReader.TidExecutionReportModify => 183,
        EntryPointFrameReader.TidExecutionReportCancel => 182,
        EntryPointFrameReader.TidExecutionReportTrade => 154,
        EntryPointFrameReader.TidExecutionReportReject => 138,
        _ => -1,
    };

    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Channel<byte[]> _sendQueue;
    private readonly CancellationTokenSource _cts;
    private readonly Action<string>? _logDebug;
    private readonly Action<string>? _logWarn;
    private readonly FixpClient? _fixp;
    private Task? _recvTask;
    private Task? _sendTask;
    private Task? _heartbeatTask;
    private long _isOpen = 1;

    public event Action<ExecReportNew>? OnNew;
    public event Action<ExecReportTrade>? OnTrade;
    public event Action<ExecReportCancel>? OnCancel;
    public event Action<ExecReportReject>? OnReject;
    public event Action<string>? OnDisconnect;

    public bool IsOpen => Interlocked.Read(ref _isOpen) == 1;

    /// <summary>
    /// Exposes the FIXP session layer when this client was constructed
    /// with <see cref="FixpClientOptions"/>; <c>null</c> in legacy
    /// (no-handshake) mode. Read-only — callers should drive sends via
    /// <see cref="SendNewOrder"/>/<see cref="SendCancel"/>.
    /// </summary>
    public FixpClient? Fixp => _fixp;

    private EntryPointClient(TcpClient tcp, NetworkStream stream, Action<string>? logDebug, Action<string>? logWarn,
        CancellationToken externalCt, FixpClient? fixp = null)
    {
        _tcp = tcp;
        _stream = stream;
        _logDebug = logDebug;
        _logWarn = logWarn;
        _fixp = fixp;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public static async Task<EntryPointClient> ConnectAsync(string host, int port,
        Action<string>? logDebug = null, Action<string>? logWarn = null, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        var stream = tcp.GetStream();
        var client = new EntryPointClient(tcp, stream, logDebug, logWarn, ct);
        client.Start();
        return client;
    }

    /// <summary>
    /// Connects and performs a full FIXP handshake (Negotiate +
    /// Establish) before returning. After this call returns the client
    /// is in <see cref="FixpClientState.Established"/>; subsequent
    /// business sends will carry the assigned <c>msgSeqNum</c>, the
    /// client emits <c>Sequence</c> heartbeats, and a <c>Terminate</c>
    /// is sent on dispose. Throws <see cref="FixpHandshakeRejectedException"/>
    /// if the gateway rejects either handshake leg.
    /// </summary>
    public static async Task<EntryPointClient> ConnectAsync(string host, int port,
        FixpClientOptions fixpOptions,
        Action<string>? logDebug = null, Action<string>? logWarn = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fixpOptions);
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        var stream = tcp.GetStream();
        var fixp = new FixpClient(stream, fixpOptions, logDebug, logWarn);
        var client = new EntryPointClient(tcp, stream, logDebug, logWarn, ct, fixp);
        client.Start();
        try
        {
            await fixp.NegotiateAndEstablishAsync(client._cts.Token).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return client;
    }

    private void Start()
    {
        _recvTask = Task.Run(() => RunReceiveLoopAsync(_cts.Token));
        _sendTask = Task.Run(() => RunSendLoopAsync(_cts.Token));
        if (_fixp != null)
        {
            _heartbeatTask = Task.Run(() => RunHeartbeatLoopAsync(_cts.Token));
        }
    }

    public bool SendNewOrder(ulong clOrdId, long securityId, OrderSide side,
        OrderTypeIntent type, OrderTifIntent tif, long qty, long priceMantissa)
    {
        if (!IsOpen) return false;
        var frame = new byte[HeaderSize + NewOrderBlockLen];
        EncodeNewOrder(frame, clOrdId, securityId, side, type, tif, qty, priceMantissa);
        if (_fixp != null)
        {
            _fixp.AssignOutboundBusinessHeader(frame.AsSpan(HeaderSize, NewOrderBlockLen));
        }
        _logDebug?.Invoke($"send NEW clord={clOrdId} sec={securityId} side={side} qty={qty} px={priceMantissa} tif={tif}");
        return Enqueue(frame);
    }

    public bool SendCancel(ulong clOrdId, long securityId, ulong orderId, ulong origClOrdId, OrderSide side)
    {
        if (!IsOpen) return false;
        var frame = new byte[HeaderSize + CancelBlockLen];
        EncodeCancel(frame, clOrdId, securityId, orderId, origClOrdId, side);
        if (_fixp != null)
        {
            _fixp.AssignOutboundBusinessHeader(frame.AsSpan(HeaderSize, CancelBlockLen));
        }
        _logDebug?.Invoke($"send CXL clord={clOrdId} sec={securityId} orderId={orderId}");
        return Enqueue(frame);
    }

    /// <summary>
    /// Encodes a SimpleNewOrderV2 frame (8-byte SBE header + 82-byte body)
    /// into <paramref name="frame"/>. Exposed as <c>internal static</c> so
    /// wire-format compatibility tests can round-trip it through the host's
    /// <c>InboundMessageDecoder</c> without standing up a TCP connection.
    /// </summary>
    internal static int EncodeNewOrder(Span<byte> frame, ulong clOrdId, long securityId, OrderSide side,
        OrderTypeIntent type, OrderTifIntent tif, long qty, long priceMantissa)
    {
        if (frame.Length < HeaderSize + NewOrderBlockLen)
            throw new ArgumentException("buffer too small for SimpleNewOrderV2", nameof(frame));
        EntryPointFrameReader.WriteHeader(frame.Slice(0, HeaderSize),
            messageLength: (ushort)(HeaderSize + NewOrderBlockLen),
            blockLength: NewOrderBlockLen,
            templateId: EntryPointFrameReader.TidSimpleNewOrder,
            version: 2);
        var body = frame.Slice(HeaderSize, NewOrderBlockLen);
        body.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(NewOrderClOrdID, 8), clOrdId);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(NewOrderSecurityID, 8), securityId);
        body[NewOrderSide] = side == OrderSide.Buy ? (byte)'1' : (byte)'2';
        body[NewOrderOrdType] = type == OrderTypeIntent.Market ? (byte)'1' : (byte)'2';
        body[NewOrderTif] = tif switch
        {
            OrderTifIntent.Day => (byte)'0',
            OrderTifIntent.IOC => (byte)'3',
            OrderTifIntent.FOK => (byte)'4',
            _ => (byte)'0',
        };
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(NewOrderQty, 8), qty);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(NewOrderPrice, 8), priceMantissa);
        return HeaderSize + NewOrderBlockLen;
    }

    /// <summary>
    /// Encodes an OrderCancelRequest frame (8-byte SBE header + 76-byte body).
    /// </summary>
    internal static int EncodeCancel(Span<byte> frame, ulong clOrdId, long securityId, ulong orderId,
        ulong origClOrdId, OrderSide side)
    {
        if (frame.Length < HeaderSize + CancelBlockLen)
            throw new ArgumentException("buffer too small for OrderCancelRequest", nameof(frame));
        EntryPointFrameReader.WriteHeader(frame.Slice(0, HeaderSize),
            messageLength: (ushort)(HeaderSize + CancelBlockLen),
            blockLength: CancelBlockLen,
            templateId: EntryPointFrameReader.TidOrderCancelRequest,
            version: 0);
        var body = frame.Slice(HeaderSize, CancelBlockLen);
        body.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(CancelClOrdID, 8), clOrdId);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(CancelSecurityID, 8), securityId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(CancelOrderID, 8), orderId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(CancelOrigClOrdID, 8), origClOrdId);
        body[CancelSide] = side == OrderSide.Buy ? (byte)'1' : (byte)'2';
        return HeaderSize + CancelBlockLen;
    }

    /// <summary>Decodes an ER_New body. Exposed for wire-format tests.</summary>
    internal static ExecReportNew DecodeExecReportNew(ReadOnlySpan<byte> body) => new(
        ClOrdId: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(ErClOrdId, 8)),
        SecurityId: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErSecurityId, 8)),
        Side: SideFromByte(body[ErSide]),
        OrderId: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErNewOrderId, 8)));

    /// <summary>Decodes an ER_Trade body. Exposed for wire-format tests.</summary>
    internal static ExecReportTrade DecodeExecReportTrade(ReadOnlySpan<byte> body) => new(
        ClOrdId: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(ErClOrdId, 8)),
        SecurityId: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErSecurityId, 8)),
        Side: SideFromByte(body[ErSide]),
        OrderId: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErTradeOrderId, 8)),
        LastQty: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErTradeLastQty, 8)),
        LastPxMantissa: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErTradeLastPx, 8)),
        LeavesQty: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErTradeLeavesQty, 8)),
        CumQty: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErTradeCumQty, 8)));

    /// <summary>Decodes an ER_Cancel body. Exposed for wire-format tests.</summary>
    internal static ExecReportCancel DecodeExecReportCancel(ReadOnlySpan<byte> body) => new(
        ClOrdId: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(ErClOrdId, 8)),
        SecurityId: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErSecurityId, 8)),
        Side: SideFromByte(body[ErSide]),
        OrderId: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErCancelOrderId, 8)));

    /// <summary>Decodes an ER_Reject body. Exposed for wire-format tests.</summary>
    internal static ExecReportReject DecodeExecReportReject(ReadOnlySpan<byte> body) => new(
        ClOrdId: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(ErClOrdId, 8)),
        SecurityId: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErSecurityId, 8)),
        OrderId: BinaryPrimitives.ReadInt64LittleEndian(body.Slice(ErRejectOrderId, 8)),
        OrigClOrdId: BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(ErRejectOrigClOrdId, 8)),
        RejectReason: body[ErRejectReason]);

    private bool Enqueue(byte[] frame)
    {
        if (!IsOpen) return false;
        try
        {
            while (true)
            {
                if (_sendQueue.Writer.TryWrite(frame)) return true;
                // Bounded channel with FullMode=Wait: TryWrite returns false
                // both when the writer is closed AND when the queue is full.
                // Honor backpressure rather than silently dropping the frame.
                if (!_sendQueue.Writer.WaitToWriteAsync(_cts.Token).AsTask().GetAwaiter().GetResult())
                {
                    return false;
                }
            }
        }
        catch (ChannelClosedException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
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
                    if (_fixp != null)
                    {
                        await _fixp.WriteAsync(frame, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
                    }
                }
                catch (IOException ex)
                {
                    _logWarn?.Invoke($"send IO error: {ex.Message}");
                    Close($"send IO error: {ex.Message}");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logWarn?.Invoke($"send loop error: {ex.Message}");
        }
        finally
        {
            Close("send loop exit");
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        if (_fixp == null) return;
        var period = TimeSpan.FromMilliseconds(Math.Max(50, _fixp.KeepAlive.TotalMilliseconds / 2));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(period, ct).ConfigureAwait(false);
                try { await _fixp.TickHeartbeatAsync(ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logWarn?.Invoke($"fixp heartbeat error: {ex.Message}");
                }
                if (_fixp.IsPeerStale())
                {
                    _logWarn?.Invoke("fixp watchdog: peer stale, closing");
                    Close("fixp watchdog: peer stale");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        var headerBuf = new byte[HeaderSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadExactAsync(_stream, headerBuf, ct).ConfigureAwait(false);
                ushort messageLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(0, 2));
                ushort encodingType = BinaryPrimitives.ReadUInt16LittleEndian(headerBuf.AsSpan(2, 2));
                if (encodingType != EntryPointFrameReader.SofhEncodingType)
                {
                    _logWarn?.Invoke($"unexpected SOFH encodingType=0x{encodingType:X4}, dropping connection");
                    Close($"unexpected SOFH encodingType=0x{encodingType:X4}");
                    return;
                }
                if (messageLength < EntryPointFrameReader.WireHeaderSize ||
                    messageLength > EntryPointFrameReader.MaxInboundMessageLength)
                {
                    _logWarn?.Invoke($"unreasonable SOFH messageLength={messageLength}, dropping connection");
                    Close($"unreasonable SOFH messageLength={messageLength}");
                    return;
                }
                var sbe = headerBuf.AsSpan(EntryPointFrameReader.SofhSize, EntryPointFrameReader.SbeHeaderSize);
                ushort blockLength = BinaryPrimitives.ReadUInt16LittleEndian(sbe.Slice(0, 2));
                ushort templateId = BinaryPrimitives.ReadUInt16LittleEndian(sbe.Slice(2, 2));
                ushort schemaId = BinaryPrimitives.ReadUInt16LittleEndian(sbe.Slice(4, 2));
                ushort version = BinaryPrimitives.ReadUInt16LittleEndian(sbe.Slice(6, 2));
                if (schemaId != EntryPointFrameReader.SchemaId)
                {
                    _logWarn?.Invoke($"unexpected schemaId={schemaId}, dropping connection");
                    Close($"unexpected schemaId={schemaId}");
                    return;
                }
                int expected = ExpectedBlockLength(templateId);
                if (expected >= 0 && blockLength != expected)
                {
                    _logWarn?.Invoke($"unexpected blockLength={blockLength} for tid={templateId} (expected={expected}), dropping connection");
                    Close($"unexpected blockLength={blockLength} for tid={templateId}");
                    return;
                }
                if (expected < 0 && (blockLength == 0 || blockLength > MaxAcceptedBlockLength))
                {
                    _logWarn?.Invoke($"unreasonable blockLength={blockLength} for tid={templateId}, dropping connection");
                    Close($"unreasonable blockLength={blockLength}");
                    return;
                }
                int bodyLength = messageLength - EntryPointFrameReader.WireHeaderSize;
                if (bodyLength < blockLength || bodyLength > MaxAcceptedBlockLength)
                {
                    _logWarn?.Invoke($"SOFH messageLength inconsistent with body (msgLen={messageLength}, block={blockLength}), dropping");
                    Close($"messageLength inconsistent");
                    return;
                }
                // Rent the body buffer to avoid per-frame heap allocations under load.
                // Body may include trailing varData beyond BlockLength once GAP-02 lands;
                // we read the full bodyLength here so the cursor stays aligned.
                var body = ArrayPool<byte>.Shared.Rent(bodyLength);
                try
                {
                    await ReadExactAsync(_stream, body.AsMemory(0, bodyLength), ct).ConfigureAwait(false);
                    if (_fixp != null)
                    {
                        var disp = _fixp.HandleInboundFrame(templateId, body.AsSpan(0, bodyLength));
                        if (disp == InboundDisposition.HandledControl)
                            continue;
                        if (disp == InboundDisposition.PeerTerminated)
                        {
                            Close("peer Terminate");
                            return;
                        }
                        if (disp == InboundDisposition.ProtocolError)
                        {
                            Close("fixp protocol error");
                            return;
                        }
                        // PassThrough: track inbound business msgSeqNum
                        // (InboundBusinessHeader is 18 bytes; msgSeqNum at body[4..8]).
                        if (bodyLength >= 8)
                        {
                            uint msgSeq = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4, 4));
                            if (!_fixp.TrackInboundBusinessSeq(msgSeq))
                            {
                                // Duplicate; drop without dispatching to ER callbacks.
                                continue;
                            }
                        }
                    }
                    Dispatch(templateId, version, body, blockLength);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(body);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException ex)
        {
            _logWarn?.Invoke($"recv IO error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logWarn?.Invoke($"recv loop error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Close("recv loop exit");
        }
    }

    private void Dispatch(ushort templateId, ushort version, byte[] body, int blockLength)
    {
        ReadOnlySpan<byte> slice = body.AsSpan(0, blockLength);
        try
        {
            switch (templateId)
            {
                case EntryPointFrameReader.TidExecutionReportNew:
                    {
                        var er = DecodeExecReportNew(slice);
                        _logDebug?.Invoke($"recv ER_NEW clord={er.ClOrdId} orderId={er.OrderId} side={er.Side}");
                        OnNew?.Invoke(er);
                        break;
                    }
                case EntryPointFrameReader.TidExecutionReportTrade:
                    {
                        var er = DecodeExecReportTrade(slice);
                        _logDebug?.Invoke($"recv ER_TRADE clord={er.ClOrdId} orderId={er.OrderId} side={er.Side} lastQty={er.LastQty} lastPx={er.LastPxMantissa} leaves={er.LeavesQty}");
                        OnTrade?.Invoke(er);
                        break;
                    }
                case EntryPointFrameReader.TidExecutionReportCancel:
                    {
                        var er = DecodeExecReportCancel(slice);
                        _logDebug?.Invoke($"recv ER_CXL clord={er.ClOrdId} orderId={er.OrderId}");
                        OnCancel?.Invoke(er);
                        break;
                    }
                case EntryPointFrameReader.TidExecutionReportReject:
                    {
                        var er = DecodeExecReportReject(slice);
                        _logDebug?.Invoke($"recv ER_REJ clord={er.ClOrdId} origClord={er.OrigClOrdId} orderId={er.OrderId} reason={er.RejectReason}");
                        OnReject?.Invoke(er);
                        break;
                    }
                default:
                    _logDebug?.Invoke($"recv ignored tid={templateId} v={version}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logWarn?.Invoke($"dispatch error tid={templateId}: {ex.Message}");
        }
    }

    private static OrderSide SideFromByte(byte b) => b == (byte)'2' ? OrderSide.Sell : OrderSide.Buy;

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await stream.ReadAsync(buf.Slice(read), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _isOpen, 0) == 0) return;
        _sendQueue.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
        try { OnDisconnect?.Invoke(reason); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        // Send Terminate first when in FIXP mode so the gateway sees a
        // graceful close rather than a half-open TCP. Best-effort.
        if (_fixp != null)
        {
            try { await _fixp.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        Close("dispose");
        try { if (_recvTask != null) await _recvTask.ConfigureAwait(false); } catch { }
        try { if (_sendTask != null) await _sendTask.ConfigureAwait(false); } catch { }
        try { if (_heartbeatTask != null) await _heartbeatTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}

public readonly record struct ExecReportNew(ulong ClOrdId, long SecurityId, OrderSide Side, long OrderId);
public readonly record struct ExecReportTrade(ulong ClOrdId, long SecurityId, OrderSide Side, long OrderId,
    long LastQty, long LastPxMantissa, long LeavesQty, long CumQty);
public readonly record struct ExecReportCancel(ulong ClOrdId, long SecurityId, OrderSide Side, long OrderId);
public readonly record struct ExecReportReject(ulong ClOrdId, long SecurityId, long OrderId, ulong OrigClOrdId, byte RejectReason);
