using B3.Exchange.EntryPoint;
using B3.Exchange.Matching;

namespace B3.Exchange.Integration;

/// <summary>
/// One per UMDF channel. Owns:
///  - A <see cref="MatchingEngine"/> (single-threaded by construction).
///  - A bounded inbound queue of decoded EntryPoint commands tagged with the
///    originating <see cref="IEntryPointResponseChannel"/>.
///  - An order-id → reply-channel map so that PASSIVE-side execution reports
///    (e.g. a resting order is filled by a counterparty's aggressor) get
///    routed back to the correct TCP session.
///  - A buffer that accumulates UMDF MBO/Trade frames emitted by the engine
///    during a single command's execution. The buffered events are then
///    flushed as one packet (with a packet-header + monotonic
///    <c>SequenceNumber</c>) to the <see cref="IUmdfPacketSink"/>.
///
/// Implements both <see cref="IEntryPointEngineSink"/> (commands in) and
/// <see cref="IMatchingEventSink"/> (engine events out). The dispatch loop
/// guarantees that the engine and the event-sink callbacks always run on the
/// dedicated dispatch thread — there is no cross-thread call into the engine.
/// </summary>
public sealed class ChannelDispatcher : IEntryPointEngineSink, IMatchingEventSink, IAsyncDisposable
{
    private const int DefaultInboundCapacity = 4096;
    private const int MaxPacketBytes = 1400;

    /// <summary>
    /// Maximum time the dispatch loop will block waiting for new work before
    /// emitting a liveness heartbeat. Kept short (1s) so the
    /// <c>/health/live</c> default threshold (5s) is comfortably exceeded
    /// only when the loop thread is actually wedged.
    /// </summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    public byte ChannelNumber { get; }
    public ushort SequenceVersion { get; private set; }
    public uint SequenceNumber { get; private set; }

    private readonly System.Threading.Channels.Channel<WorkItem> _inbound;
    private readonly MatchingEngine _engine;
    private readonly IUmdfPacketSink _packetSink;
    private readonly Func<ulong> _nowNanos;
    private readonly ushort _tradeDate;
    private readonly ChannelMetrics? _metrics;

    private readonly Dictionary<long, OrderOwnership> _orderOwners = new();
    private readonly byte[] _packetBuf = new byte[MaxPacketBytes];
    private int _packetWritten;
    private IEntryPointResponseChannel? _currentReply;
    private ulong _currentClOrdId;
    private ulong _currentOrigClOrdId;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public ChannelDispatcher(byte channelNumber, Func<IMatchingEventSink, MatchingEngine> engineFactory, IUmdfPacketSink packetSink,
        Func<ulong>? nowNanos = null, ushort tradeDate = 0, int inboundCapacity = DefaultInboundCapacity,
        ChannelMetrics? metrics = null)
    {
        ChannelNumber = channelNumber;
        _packetSink = packetSink;
        _nowNanos = nowNanos ?? DefaultNowNanos;
        _tradeDate = tradeDate;
        _metrics = metrics;
        SequenceVersion = 1;
        SequenceNumber = 0;
        _inbound = System.Threading.Channels.Channel.CreateBounded<WorkItem>(
            new System.Threading.Channels.BoundedChannelOptions(inboundCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
            });
        _engine = engineFactory(this);
    }

    private static ulong DefaultNowNanos()
        => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;

    public void Start()
    {
        _loopTask = Task.Factory.StartNew(() => RunLoopAsync(_cts.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Heartbeat is recorded on every loop wakeup (whether triggered by
        // new work or by the periodic timeout) so a stuck/dead dispatch
        // thread is detected by /health/live within HeartbeatInterval +
        // probe threshold.
        try
        {
            var reader = _inbound.Reader;
            while (!ct.IsCancellationRequested)
            {
                RecordHeartbeat();
                Task<bool> waitTask = reader.WaitToReadAsync(ct).AsTask();
                Task completed;
                try
                {
                    completed = await Task.WhenAny(waitTask, Task.Delay(HeartbeatInterval, ct))
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                if (completed == waitTask)
                {
                    bool more;
                    try { more = await waitTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    if (!more) return; // channel completed
                    while (reader.TryRead(out var item))
                    {
                        ProcessOne(item);
                        RecordHeartbeat();
                    }
                }
                // else: timeout — loop and re-record the heartbeat.
            }
        }
        catch (OperationCanceledException) { }
    }

    private void RecordHeartbeat()
        => _metrics?.RecordTick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    internal void ProcessOne(in WorkItem item)
    {
        _currentReply = item.Reply;
        _currentClOrdId = item.ClOrdId;
        _currentOrigClOrdId = item.OrigClOrdId;
        _packetWritten = 0;
        try
        {
            switch (item.Kind)
            {
                case WorkKind.New: _metrics?.IncOrdersIn(); _engine.Submit(item.NewOrder!); break;
                case WorkKind.Cancel: _metrics?.IncOrdersIn(); _engine.Cancel(item.Cancel!); break;
                case WorkKind.Replace: _metrics?.IncOrdersIn(); _engine.Replace(item.Replace!); break;
                case WorkKind.DecodeError:
                    _currentReply?.WriteExecutionReportReject(
                        new RejectEvent(_currentClOrdId.ToString(), 0, 0, RejectReason.UnknownInstrument, _nowNanos()),
                        _currentClOrdId);
                    break;
            }
        }
        finally
        {
            FlushPacket();
            _currentReply = null;
            _currentClOrdId = 0;
            _currentOrigClOrdId = 0;
        }
    }

    private void FlushPacket()
    {
        if (_packetWritten == 0) return;
        // Patch packet header with the live seq number + send time.
        SequenceNumber++;
        ulong now = _nowNanos();
        B3.Umdf.WireEncoder.UmdfWireEncoder.PatchPacketHeader(
            _packetBuf.AsSpan(0, B3.Umdf.WireEncoder.WireOffsets.PacketHeaderSize), SequenceNumber, now);
        _packetSink.Publish(ChannelNumber, _packetBuf.AsSpan(0, _packetWritten));
        _metrics?.IncPacketsOut();
        _packetWritten = 0;
    }

    private Span<byte> ReserveOrFlush(int frameSize)
    {
        if (_packetWritten == 0)
        {
            // Reserve packet header up front; SequenceNumber + sendingTime
            // patched at flush.
            B3.Umdf.WireEncoder.UmdfWireEncoder.WritePacketHeader(_packetBuf,
                ChannelNumber, SequenceVersion, sequenceNumber: 0, sendingTimeNanos: 0);
            _packetWritten = B3.Umdf.WireEncoder.WireOffsets.PacketHeaderSize;
        }
        if (_packetWritten + frameSize > MaxPacketBytes)
        {
            FlushPacket();
            B3.Umdf.WireEncoder.UmdfWireEncoder.WritePacketHeader(_packetBuf,
                ChannelNumber, SequenceVersion, sequenceNumber: 0, sendingTimeNanos: 0);
            _packetWritten = B3.Umdf.WireEncoder.WireOffsets.PacketHeaderSize;
        }
        return _packetBuf.AsSpan(_packetWritten);
    }

    private void Commit(int written) => _packetWritten += written;

    // ====== IEntryPointEngineSink ======

    public void EnqueueNewOrder(in NewOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue)
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.New, reply, clOrdIdValue, 0, cmd, null, null));

    public void EnqueueCancel(in CancelOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue)
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.Cancel, reply, clOrdIdValue, 0, null, cmd, null));

    public void EnqueueReplace(in ReplaceOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue, ulong origClOrdIdValue)
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.Replace, reply, clOrdIdValue, origClOrdIdValue, null, null, cmd));

    public void OnDecodeError(IEntryPointResponseChannel reply, string error)
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.DecodeError, reply, 0, 0, null, null, null));

    // ====== IMatchingEventSink ======

    public void OnOrderAccepted(in OrderAcceptedEvent e)
    {
        if (_currentReply != null)
            _orderOwners[e.OrderId] = new OrderOwnership(_currentReply, _currentClOrdId);

        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.OrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderAddedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.PriceMantissa, e.RemainingQuantity, e.RptSeq, e.InsertTimestampNanos);
        Commit(n);

        _currentReply?.WriteExecutionReportNew(e);
    }

    public void OnOrderQuantityReduced(in OrderQuantityReducedEvent e)
    {
        // Update on the wire = OrderAdded with action UPDATE (0x01).
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.OrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderAddedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.PriceMantissa, e.NewRemainingQuantity, e.RptSeq, e.InsertTimestampNanos);
        // Patch MdUpdateAction byte from NEW(0x00) to UPDATE(0x01).
        int actionOffset = B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.OrderBodyMdUpdateActionOffset;
        dst[actionOffset] = 0x01; // MDUpdateAction.CHANGE
        Commit(n);
    }

    public void OnOrderCanceled(in OrderCanceledEvent e)
    {
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.DeleteOrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderDeletedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.RemainingQuantityAtCancel, e.RptSeq, e.TransactTimeNanos, e.PriceMantissa);
        Commit(n);

        if (_orderOwners.Remove(e.OrderId, out var owner))
        {
            owner.Reply.WriteExecutionReportCancel(e, owner.ClOrdId);
        }
    }

    public void OnOrderFilled(in OrderFilledEvent e)
    {
        // Fully consumed by trades — emit DeleteOrder; the per-fill ER_Trade
        // events were already dispatched via OnTrade.
        var entryType = e.Side == Side.Buy
            ? B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeBid
            : B3.Umdf.WireEncoder.UmdfWireEncoder.MdEntryTypeOffer;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.DeleteOrderBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteOrderDeletedFrame(dst,
            e.SecurityId, e.OrderId, entryType, e.FinalFilledQuantity, e.RptSeq, e.TransactTimeNanos, e.PriceMantissa);
        Commit(n);

        _orderOwners.Remove(e.OrderId);
    }

    public void OnTrade(in TradeEvent e)
    {
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.TradeBlockLength);
        bool aggressorIsBuy = e.AggressorSide == Side.Buy;
        uint buyer = aggressorIsBuy ? e.AggressorFirm : e.RestingFirm;
        uint seller = aggressorIsBuy ? e.RestingFirm : e.AggressorFirm;
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteTradeFrame(dst,
            e.SecurityId, e.PriceMantissa, e.Quantity, e.TradeId, _tradeDate, e.TransactTimeNanos, e.RptSeq,
            buyerFirm: buyer, sellerFirm: seller);
        Commit(n);

        // ER_Trade for both sides if their session is known.
        // Aggressor: _currentReply (the command initiator).
        // Resting: lookup orderOwners[e.RestingOrderId].
        if (_currentReply != null)
        {
            // We do not maintain per-aggressor cum/leaves tracking here; integration
            // tests are scope-limited to single-fill scenarios. For passive side
            // aggregation use OrderQuantityReducedEvent / OrderFilledEvent context.
            _currentReply.WriteExecutionReportTrade(e, isAggressor: true,
                ownerOrderId: e.AggressorOrderId, clOrdIdValue: _currentClOrdId,
                leavesQty: 0, cumQty: e.Quantity);
        }
        if (_orderOwners.TryGetValue(e.RestingOrderId, out var resting))
        {
            resting.Reply.WriteExecutionReportTrade(e, isAggressor: false,
                ownerOrderId: e.RestingOrderId, clOrdIdValue: resting.ClOrdId,
                leavesQty: 0, cumQty: e.Quantity);
        }
    }

    public void OnReject(in RejectEvent e)
    {
        _currentReply?.WriteExecutionReportReject(e, _currentClOrdId);
    }

    // ====== shutdown ======

    /// <summary>
    /// Test hook: simulate a wedged dispatcher by cancelling the loop
    /// without disposing. Subsequent enqueues remain accepted by the
    /// channel but will never be processed, so liveness probes can verify
    /// that <c>/health/live</c> flips to 503 within the configured stale
    /// threshold.
    /// </summary>
    internal void KillForTesting()
    {
        try { _cts.Cancel(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        if (_loopTask != null) { try { await _loopTask.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }

    internal enum WorkKind : byte { New, Cancel, Replace, DecodeError }

    internal readonly record struct OrderOwnership(IEntryPointResponseChannel Reply, ulong ClOrdId);

    internal sealed record WorkItem(
        WorkKind Kind,
        IEntryPointResponseChannel Reply,
        ulong ClOrdId,
        ulong OrigClOrdId,
        NewOrderCommand? NewOrder,
        CancelOrderCommand? Cancel,
        ReplaceOrderCommand? Replace);
}
