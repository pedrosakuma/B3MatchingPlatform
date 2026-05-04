namespace B3.Exchange.Core;

/// <summary>
/// Operator-command facet of <see cref="ChannelDispatcher"/> (issue #168
/// split): the producer-side <c>EnqueueOperator*</c> APIs (channel-reset,
/// snapshot rotation, trade-bust replay), plus the <c>Process*</c>
/// counterparts that run on the dispatch thread when the resulting
/// <see cref="WorkItem"/>s are picked up. Snapshot-rotator attachment
/// also lives here since it is part of the operator surface.
/// </summary>
public sealed partial class ChannelDispatcher
{
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
        AssertOnLoopThread();
        _engine.ResetForChannelReset();
        Volatile.Write(ref _sequenceVersion, (ushort)(_sequenceVersion + 1));
        Volatile.Write(ref _sequenceNumber, 0u);
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

    /// <summary>
    /// Operator-triggered trade-bust replay (issue #15). Synthesises a
    /// <c>TradeBust_57</c> frame on the incremental channel using the
    /// next available <see cref="MatchingEngine.AllocateNextRptSeq"/>.
    /// The bust is flushed as a single-message packet under the current
    /// SequenceVersion. No engine state is mutated — the matching engine
    /// is unaware that a previously-emitted trade has been busted.
    /// </summary>
    private void ProcessTradeBust(OperatorTradeBust bust)
    {
        AssertOnLoopThread();
        uint rptSeq = _engine.AllocateNextRptSeq();
        _packetWritten = 0;
        var dst = ReserveOrFlush(B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
            + B3.Umdf.WireEncoder.WireOffsets.TradeBustBlockLength);
        int written = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteTradeBustFrame(dst,
            bust.SecurityId, bust.PriceMantissa, bust.Size, bust.TradeId, bust.TradeDate,
            _nowNanos(), rptSeq);
        Commit(written);
        FlushPacket();
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
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.SnapshotRotation, default, 0, false,
            0, 0, null, null, null, null));

    /// <summary>
    /// Operator command (issue #6): forces an immediate snapshot publish on
    /// the next available dispatcher cycle. Identical wire effect to a
    /// <see cref="EnqueueSnapshotTick"/>; the distinct work-kind exists so
    /// future operator commands can be metered/logged independently of the
    /// scheduled cadence ticks. Returns <c>false</c> if the inbound queue
    /// is full. Safe to call from any thread.
    /// </summary>
    public bool EnqueueOperatorSnapshotNow()
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorSnapshotNow, default, 0, false,
            0, 0, null, null, null, null));

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
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorBumpVersion, default, 0, false,
            0, 0, null, null, null, null));

    /// <summary>
    /// Operator command (issue #15): publishes a <c>TradeBust_57</c> frame
    /// for a previously-emitted trade identified by
    /// (<paramref name="securityId"/>, <paramref name="tradeId"/>). The
    /// price/size/date echo fields are caller-supplied — the simulator
    /// does not retain a per-trade audit log. The bust frame is stamped
    /// with the next available <c>RptSeq</c> from the channel's matching
    /// engine and emitted under the current <c>SequenceVersion</c>.
    /// Returns <c>false</c> if the inbound queue is full. Safe to call
    /// from any thread.
    /// </summary>
    public bool EnqueueOperatorTradeBust(long securityId, long priceMantissa, long size,
        uint tradeId, ushort tradeDate)
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorTradeBust, default, 0, false,
            0, 0, null, null, null, null,
            TradeBust: new OperatorTradeBust(securityId, priceMantissa, size, tradeId, tradeDate)));
}
