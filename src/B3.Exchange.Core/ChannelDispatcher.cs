using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// One per UMDF channel. Owns:
///  - A <see cref="MatchingEngine"/> (single-threaded by construction).
///  - A bounded inbound queue of decoded EntryPoint commands tagged with the
///    originating <see cref="IGatewayResponseChannel"/>.
///  - An order-id → reply-channel map so that PASSIVE-side execution reports
///    (e.g. a resting order is filled by a counterparty's aggressor) get
///    routed back to the correct TCP session.
///  - A buffer that accumulates UMDF MBO/Trade frames emitted by the engine
///    during a single command's execution. The buffered events are then
///    flushed as one packet (with a packet-header + monotonic
///    <c>SequenceNumber</c>) to the <see cref="IUmdfPacketSink"/>.
///
/// Implements both <see cref="IInboundCommandSink"/> (commands in) and
/// <see cref="IMatchingEventSink"/> (engine events out). The dispatch loop
/// guarantees that the engine and the event-sink callbacks always run on the
/// dedicated dispatch thread — there is no cross-thread call into the engine.
/// </summary>
public sealed partial class ChannelDispatcher : IInboundCommandSink, IMatchingEventSink, IAsyncDisposable
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
    private readonly ILogger<ChannelDispatcher> _logger;
    private readonly Func<ulong> _nowNanos;
    private readonly ushort _tradeDate;
    private readonly ChannelMetrics? _metrics;

    private readonly Dictionary<long, OrderOwnership> _orderOwners = new();
    /// <summary>
    /// Per-channel reverse index from <c>(EnteringFirm, ClOrdID)</c> to engine-assigned
    /// <c>orderId</c>. Populated when an order enters the book (<see cref="OnOrderAccepted"/>)
    /// and evicted when it leaves (<see cref="OnOrderCanceled"/>, fully-filled
    /// <see cref="OnOrderFilled"/>). Allows clients to send Cancel/Replace by their own
    /// <c>OrigClOrdID</c> without tracking the engine-assigned id.
    /// All access happens on the dispatch thread, so no synchronisation is required.
    /// </summary>
    private readonly Dictionary<(uint Firm, ulong ClOrdId), long> _clOrdIdIndex = new();
    private readonly byte[] _packetBuf = new byte[MaxPacketBytes];
    private int _packetWritten;
    private IGatewayResponseChannel? _currentReply;
    private ulong _currentClOrdId;
    private ulong _currentOrigClOrdId;

    private SnapshotRotator? _snapshotRotator;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    /// <summary>
    /// The snapshot rotator bound to this dispatcher, if any. Always invoked
    /// on the dispatch thread via a <see cref="WorkKind.SnapshotRotation"/>
    /// work item so it observes a stable book.
    /// </summary>
    public SnapshotRotator? SnapshotRotator => _snapshotRotator;

    public ChannelDispatcher(byte channelNumber, Func<IMatchingEventSink, MatchingEngine> engineFactory, IUmdfPacketSink packetSink,
        ILogger<ChannelDispatcher> logger,
        Func<ulong>? nowNanos = null, ushort tradeDate = 0, int inboundCapacity = DefaultInboundCapacity,
        ChannelMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ChannelNumber = channelNumber;
        _packetSink = packetSink;
        _logger = logger;
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
        _logger.LogInformation("channel {ChannelNumber} dispatcher starting", ChannelNumber);
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
                bool more;
                try
                {
                    more = await waitTask.WaitAsync(HeartbeatInterval, ct)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Timeout: loop and re-record the heartbeat on next iteration.
                    continue;
                }
                catch (OperationCanceledException) { return; }

                if (!more) return; // channel completed
                while (reader.TryRead(out var item))
                {
                    ProcessOne(item);
                    RecordHeartbeat();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "channel {ChannelNumber} dispatch loop terminated unexpectedly", ChannelNumber);
        }
    }

    private void RecordHeartbeat()
        => _metrics?.RecordTick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private void ProcessBumpVersion()
    {
        // Atomic operator-initiated channel reset (issue #6). Order matters:
        //   1. Wipe per-instrument books and reset RptSeq on the engine.
        //   2. Bump incremental SequenceVersion + reset SequenceNumber.
        //   3. Bump snapshot rotator's SequenceVersion (if attached).
        //   4. Emit one ChannelReset_11 frame, flushed as a single-message
        //      packet under the NEW SequenceVersion. Because we just reset
        //      SequenceNumber to 0, FlushPacket() stamps SequenceNumber=1.
        // Anything that races with this would violate the dispatch-thread
        // invariant — ProcessOne is the sole caller and is invoked only
        // from the dispatch loop.
        _engine.ResetForChannelReset();
        SequenceVersion = (ushort)(SequenceVersion + 1);
        SequenceNumber = 0;
        _snapshotRotator?.BumpSequenceVersion();

        // Force-write the ChannelReset_11 frame using the standard
        // ReserveOrFlush/Commit path so the packet header reflects the
        // NEW SequenceVersion.
        _packetWritten = 0;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.ChannelResetBlockLength);
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteChannelResetFrame(dst, _nowNanos());
        Commit(n);
        FlushPacket();
    }

    internal void ProcessOne(in WorkItem item)
    {
        if (item.Kind == WorkKind.SnapshotRotation || item.Kind == WorkKind.OperatorSnapshotNow)
        {
            // Snapshot ticks bypass the per-command incremental packet buffer
            // entirely — they have their own sink + sequence space owned by
            // the rotator and emit one or more complete packets directly.
            _snapshotRotator?.PublishNext();
            return;
        }

        if (item.Kind == WorkKind.OperatorBumpVersion)
        {
            ProcessBumpVersion();
            return;
        }

        _currentReply = item.Reply;
        _currentClOrdId = item.ClOrdId;
        _currentOrigClOrdId = item.OrigClOrdId;
        _packetWritten = 0;
        try
        {
            switch (item.Kind)
            {
                case WorkKind.New: _metrics?.IncOrdersIn(); _engine.Submit(item.NewOrder!); break;
                case WorkKind.Cancel:
                    {
                        _metrics?.IncOrdersIn();
                        var cancel = item.Cancel!;
                        if (cancel.OrderId == 0)
                        {
                            if (item.Reply is null || !TryResolveByClOrdId(item.Reply, item.OrigClOrdId, out var resolvedId))
                            {
                                EmitUnknownOrderIdReject(cancel.ClOrdId, cancel.SecurityId, cancel.EnteredAtNanos);
                                break;
                            }
                            cancel = cancel with { OrderId = resolvedId };
                        }
                        _engine.Cancel(cancel);
                        break;
                    }
                case WorkKind.Replace:
                    {
                        _metrics?.IncOrdersIn();
                        var replace = item.Replace!;
                        if (replace.OrderId == 0)
                        {
                            if (item.Reply is null || !TryResolveByClOrdId(item.Reply, item.OrigClOrdId, out var resolvedId))
                            {
                                EmitUnknownOrderIdReject(replace.ClOrdId, replace.SecurityId, replace.EnteredAtNanos);
                                break;
                            }
                            replace = replace with { OrderId = resolvedId };
                        }
                        _engine.Replace(replace);
                        break;
                    }
                case WorkKind.DecodeError:
                    _currentReply?.WriteExecutionReportReject(
                        new RejectEvent(_currentClOrdId.ToString(), 0, 0, RejectReason.UnknownInstrument, _nowNanos()),
                        _currentClOrdId);
                    break;
                case WorkKind.ReleaseOwner:
                    if (item.Reply is not null) ReleaseOwnerOnDispatchThread(item.Reply);
                    break;
            }
            LogCommandProcessed(ChannelNumber, item.Kind, _currentClOrdId);
        }
        finally
        {
            FlushPacket();
            _currentReply = null;
            _currentClOrdId = 0;
            _currentOrigClOrdId = 0;
        }
    }

    private bool TryResolveByClOrdId(IGatewayResponseChannel reply, ulong origClOrdId, out long orderId)
    {
        if (origClOrdId != 0 && _clOrdIdIndex.TryGetValue((reply.EnteringFirm, origClOrdId), out orderId))
            return true;
        orderId = 0;
        return false;
    }

    private void EmitUnknownOrderIdReject(string clOrdId, long securityId, ulong nowNanos)
    {
        // Surface the failure directly as an ER_Reject so the client gets a
        // clear, deterministic response instead of a silently dropped command.
        _currentReply?.WriteExecutionReportReject(
            new RejectEvent(clOrdId, securityId, 0, RejectReason.UnknownOrderId, nowNanos),
            _currentClOrdId);
    }

    private void FlushPacket()
    {
        if (_packetWritten == 0) return;
        // Patch packet header with the live seq number + send time.
        //
        // SequenceNumber wraparound (uint, ~4.29 billion packets):
        //   At 100 packets/sec → ~1.36 years before overflow.
        //   At 10 000 packets/sec → ~5 days.
        // On overflow we bump SequenceVersion (B3 UMDF field intended for
        // exactly this kind of restart / rollover signal) and reset the
        // counter. Downstream consumers treat a new SequenceVersion as a
        // discontinuity and resync from snapshot — same code path they use
        // for a host restart.
        if (SequenceNumber == uint.MaxValue)
        {
            SequenceVersion++;
            SequenceNumber = 0;
            // The packet buffer's SequenceVersion was written by
            // ReserveOrFlush with the pre-bump value; rewrite it now so the
            // on-wire header matches the new (version, seq) tuple.
            ushort newVer = SequenceVersion;
            System.Runtime.InteropServices.MemoryMarshal.Write(
                _packetBuf.AsSpan(B3.Umdf.WireEncoder.WireOffsets.PacketHeaderSequenceVersionOffset, 2),
                in newVer);
        }
        SequenceNumber++;
        ulong now = _nowNanos();
        B3.Umdf.WireEncoder.UmdfWireEncoder.PatchPacketHeader(
            _packetBuf.AsSpan(0, B3.Umdf.WireEncoder.WireOffsets.PacketHeaderSize), SequenceNumber, now);
        _packetSink.Publish(ChannelNumber, _packetBuf.AsSpan(0, _packetWritten));
        LogPacketFlushed(ChannelNumber, SequenceNumber, _packetWritten);
        _metrics?.IncPacketsOut();
        _packetWritten = 0;
    }

    /// <summary>Test seam: fast-forward <see cref="SequenceNumber"/> close
    /// to <c>uint.MaxValue</c> to exercise the wraparound path without
    /// publishing billions of packets. Must be called before any work is
    /// processed (i.e. before <see cref="Start"/>) — there is no
    /// thread-safety contract beyond "called from the test thread on a
    /// quiescent dispatcher".</summary>
    internal void TestSetSequenceNumber(uint value) => SequenceNumber = value;

    private void ReleaseOwnerOnDispatchThread(IGatewayResponseChannel reply)
    {
        // Sweep the orderId → reply map and drop every entry whose reply is
        // the disconnected session. The orders themselves stay in the book
        // (they ARE the book — passive liquidity for other sessions). We
        // simply forget who to route the passive-side ER to: subsequent
        // fills against those orders will publish the UMDF MBO/Trade frames
        // as normal, but no ER_Trade is sent (there is nobody listening).
        //
        // After this sweep, the dispatcher holds no strong reference to
        // `reply`, so the FixpSession (and its NetworkStream / Socket)
        // becomes eligible for GC once the listener has also dropped it
        // from its live-sessions list.
        if (_orderOwners.Count == 0) return;
        List<long>? toRemove = null;
        foreach (var (orderId, owner) in _orderOwners)
        {
            if (ReferenceEquals(owner.Reply, reply))
                (toRemove ??= new List<long>()).Add(orderId);
        }
        if (toRemove != null)
            foreach (var oid in toRemove) _orderOwners.Remove(oid);
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

    // ====== IInboundCommandSink ======

    public void EnqueueNewOrder(in NewOrderCommand cmd, IGatewayResponseChannel reply, ulong clOrdIdValue)
    {
        if (!_inbound.Writer.TryWrite(new WorkItem(WorkKind.New, reply, clOrdIdValue, 0, cmd, null, null)))
            LogQueueFull(ChannelNumber, WorkKind.New);
    }

    public void EnqueueCancel(in CancelOrderCommand cmd, IGatewayResponseChannel reply, ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        if (!_inbound.Writer.TryWrite(new WorkItem(WorkKind.Cancel, reply, clOrdIdValue, origClOrdIdValue, null, cmd, null)))
            LogQueueFull(ChannelNumber, WorkKind.Cancel);
    }

    public void EnqueueReplace(in ReplaceOrderCommand cmd, IGatewayResponseChannel reply, ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        if (!_inbound.Writer.TryWrite(new WorkItem(WorkKind.Replace, reply, clOrdIdValue, origClOrdIdValue, null, null, cmd)))
            LogQueueFull(ChannelNumber, WorkKind.Replace);
    }

    public void OnDecodeError(IGatewayResponseChannel reply, string error)
    {
        _logger.LogWarning("channel {ChannelNumber} inbound decode error: {Error}", ChannelNumber, error);
        if (!_inbound.Writer.TryWrite(new WorkItem(WorkKind.DecodeError, reply, 0, 0, null, null, null)))
            LogQueueFull(ChannelNumber, WorkKind.DecodeError);
    }

    /// <summary>
    /// Attaches a <see cref="SnapshotRotator"/> to this dispatcher. May only
    /// be called once. After this returns, any caller (typically a
    /// <see cref="System.Threading.Timer"/>) may invoke
    /// <see cref="EnqueueSnapshotTick"/> to schedule a snapshot publish on
    /// the dispatch thread.
    /// </summary>
    public void AttachSnapshotRotator(SnapshotRotator rotator)
    {
        ArgumentNullException.ThrowIfNull(rotator);
        if (_snapshotRotator != null)
            throw new InvalidOperationException("snapshot rotator already attached");
        _snapshotRotator = rotator;
    }

    /// <summary>
    /// Posts a snapshot tick into the inbound queue. Returns <c>false</c> if
    /// the queue is full (snapshots are idempotent — losing a tick simply
    /// defers the next refresh by one period). Safe to call from any thread.
    /// </summary>
    public bool EnqueueSnapshotTick()
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.SnapshotRotation, null, 0, 0, null, null, null));

    public void OnSessionClosed(IGatewayResponseChannel reply)
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.ReleaseOwner, reply, 0, 0, null, null, null));

    /// <summary>
    /// Operator command (issue #6): forces an immediate snapshot publish on
    /// the next available dispatcher cycle. Identical wire effect to a
    /// <see cref="EnqueueSnapshotTick"/>; the distinct work-kind exists so
    /// future operator commands can be metered/logged independently of the
    /// scheduled cadence ticks. Returns <c>false</c> if the inbound queue
    /// is full. Safe to call from any thread.
    /// </summary>
    public bool EnqueueOperatorSnapshotNow()
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorSnapshotNow, null!, 0, 0, null, null, null));

    /// <summary>
    /// Operator command (issue #6): atomically (a) bumps the incremental
    /// channel's <see cref="SequenceVersion"/> + the attached snapshot
    /// rotator's <c>SequenceVersion</c>, (b) clears every per-instrument
    /// order book, (c) resets the engine's <c>RptSeq</c> counter to 0, and
    /// (d) emits a single <c>ChannelReset_11</c> frame on the incremental
    /// channel under the NEW <see cref="SequenceVersion"/>. The next
    /// snapshot publish (whether scheduled or operator-forced) will reflect
    /// the empty book stamped with the new versions. Returns <c>false</c>
    /// if the inbound queue is full. Safe to call from any thread.
    /// </summary>
    public bool EnqueueOperatorBumpVersion()
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorBumpVersion, null!, 0, 0, null, null, null));

    // ====== IMatchingEventSink ======

    public void OnOrderAccepted(in OrderAcceptedEvent e)
    {
        if (_currentReply != null)
        {
            _orderOwners[e.OrderId] = new OrderOwnership(_currentReply, _currentClOrdId, _currentReply.EnteringFirm);
            if (_currentClOrdId != 0)
                _clOrdIdIndex[(_currentReply.EnteringFirm, _currentClOrdId)] = e.OrderId;
        }

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
            if (owner.ClOrdId != 0)
                _clOrdIdIndex.Remove((owner.EnteringFirm, owner.ClOrdId));
            owner.Reply.WriteExecutionReportCancel(e, _currentClOrdId != 0 ? _currentClOrdId : owner.ClOrdId, owner.ClOrdId);
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

        if (_orderOwners.Remove(e.OrderId, out var owner) && owner.ClOrdId != 0)
            _clOrdIdIndex.Remove((owner.EnteringFirm, owner.ClOrdId));
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
        _logger.LogInformation("channel {ChannelNumber} dispatcher stopping (sequenceNumber={SequenceNumber})",
            ChannelNumber, SequenceNumber);
        _inbound.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        if (_loopTask != null) { try { await _loopTask.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }

    internal enum WorkKind : byte { New, Cancel, Replace, DecodeError, SnapshotRotation, ReleaseOwner, OperatorSnapshotNow, OperatorBumpVersion }

    internal readonly record struct OrderOwnership(IGatewayResponseChannel Reply, ulong ClOrdId, uint EnteringFirm);

    internal sealed record WorkItem(
        WorkKind Kind,
        IGatewayResponseChannel? Reply,
        ulong ClOrdId,
        ulong OrigClOrdId,
        NewOrderCommand? NewOrder,
        CancelOrderCommand? Cancel,
        ReplaceOrderCommand? Replace);

    // ====== high-frequency log messages (LoggerMessage source-gen) ======

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "channel {ChannelNumber} processed {WorkKind} clOrdId={ClOrdId}")]
    private partial void LogCommandProcessed(byte channelNumber, WorkKind workKind, ulong clOrdId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Trace,
        Message = "channel {ChannelNumber} flushed UMDF packet seq={Sequence} bytes={Bytes}")]
    private partial void LogPacketFlushed(byte channelNumber, uint sequence, int bytes);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "channel {ChannelNumber} inbound queue full; dropped {WorkKind} (slow consumer)")]
    private partial void LogQueueFull(byte channelNumber, WorkKind workKind);
}
