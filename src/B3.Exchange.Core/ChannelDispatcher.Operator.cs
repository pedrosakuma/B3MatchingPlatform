namespace B3.Exchange.Core;

using Microsoft.Extensions.Logging;

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
    private static readonly DateOnly LocalMktDateEpoch = new(1970, 1, 1);

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
        _orders.Clear();
        RecordAllBookCounts();
        ClearOpenOrderCounts();
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
        int n = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteChannelResetFrame(dst, _timeSource.NowNanos());
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
            _timeSource.NowNanos(), rptSeq);
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
    /// Attaches a <see cref="PriceBandPublisher"/> to this dispatcher. May only
    /// be called once. After this returns, any caller may invoke
    /// <see cref="EnqueuePriceBandTick"/> to schedule a publish on the
    /// dispatch thread.
    /// </summary>
    public void AttachPriceBandPublisher(PriceBandPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        if (_priceBandPublisher != null)
            throw new InvalidOperationException("price-band publisher already attached");
        _priceBandPublisher = publisher;
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
    /// Posts a price-band tick into the inbound queue. Returns <c>false</c>
    /// when no publisher is attached or the queue is full. Safe to call from
    /// any thread.
    /// </summary>
    public bool EnqueuePriceBandTick()
        => _priceBandPublisher != null
            && _inbound.Writer.TryWrite(new WorkItem(WorkKind.PriceBandPublish, default, 0, false,
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
    /// Issue #271: forces an on-disk snapshot persist on the next
    /// dispatcher cycle (separate from <see cref="EnqueueOperatorSnapshotNow"/>
    /// which only publishes a UMDF snapshot). Drives the
    /// <c>POST /admin/channels/{ch}/snapshot/force</c> endpoint —
    /// captures channel state on the loop thread and writes through
    /// the configured <see cref="IChannelStatePersister"/>, bypassing
    /// any throttle. Returns <c>false</c> if the inbound queue is
    /// full. Safe to call from any thread.
    /// </summary>
    public bool EnqueueOperatorPersistSnapshot()
        => _inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorPersistSnapshot, default, 0, false,
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
    {
        if (RejectIfWalHalted(WorkKind.OperatorBumpVersion)) return false;
        return _inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorBumpVersion, default, 0, false,
            0, 0, null, null, null, null));
    }

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
    {
        if (RejectIfWalHalted(WorkKind.OperatorTradeBust)) return false;
        return _inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorTradeBust, default, 0, false,
            0, 0, null, null, null, null,
            TradeBust: new OperatorTradeBust(securityId, priceMantissa, size, tradeId, tradeDate)));
    }

    /// <summary>
    /// ADR 0008 PR-2: schedules an operator bust through the post-trade
    /// validator + audit-log path. The dispatch thread runs
    /// <see cref="B3.Exchange.PostTrade.BustValidator"/> against the
    /// (per-channel) audit files and the in-memory dedup index; on accept
    /// it writes a bust record + emits TradeBust_57, on reject it writes
    /// a reject-attempt record + returns the validator's verdict via the
    /// supplied <paramref name="completion"/> source. Returns <c>false</c>
    /// if the inbound queue is full or the channel is WAL-halted; in
    /// both cases the completion source is faulted.
    /// </summary>
    public bool EnqueueOperatorBustV2(
        uint tradeId, DateOnly tradeDate, ulong correlationId,
        ushort reasonCode, uint busterFirm, long? securityIdEcho,
        ulong attemptTransactTimeNanos,
        TaskCompletionSource<OperatorBustV2Outcome> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (_postTradeOrch is null)
        {
            completion.TrySetException(new InvalidOperationException(
                $"channel {ChannelNumber} bust-v2 endpoint requires audit-log configuration; not wired"));
            return false;
        }
        if (RejectIfWalHalted(WorkKind.OperatorBustV2))
        {
            completion.TrySetException(new InvalidOperationException(
                $"channel {ChannelNumber} WAL-halted; OperatorBustV2 rejected"));
            return false;
        }
        var payload = new OperatorBustV2(tradeId, tradeDate, correlationId, reasonCode,
            busterFirm, securityIdEcho, attemptTransactTimeNanos);
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorBustV2, default, 0, false,
            0, 0, null, null, null, null,
            BustV2: payload, BustCompletion: completion)))
        {
            return true;
        }
        completion.TrySetException(new InvalidOperationException(
            $"channel {ChannelNumber} inbound queue full; OperatorBustV2 rejected"));
        return false;
    }

    private void ProcessOperatorBustV2(OperatorBustV2 op, TaskCompletionSource<OperatorBustV2Outcome>? completion)
    {
        AssertOnLoopThread();
        try
        {
            // UMDF TradeBust_57 carries tradeDate as LocalMktDate (uint16
            // days since 1970-01-01); the reject-attempt record stores
            // the same value as int32. DateOnly.DayNumber is days since
            // 0001-01-01 so a normal date overflows ushort — convert
            // relative to the Unix epoch and range-check BEFORE invoking
            // the orchestrator so we never persist a half-applied accept.
            int tradeDateDaysSinceEpoch = op.TradeDate.DayNumber - LocalMktDateEpoch.DayNumber;
            if (tradeDateDaysSinceEpoch < 0 || tradeDateDaysSinceEpoch > ushort.MaxValue)
            {
                completion?.TrySetException(new ArgumentOutOfRangeException(
                    nameof(op.TradeDate),
                    $"tradeDate {op.TradeDate:yyyy-MM-dd} outside LocalMktDate range (1970-01-01..{LocalMktDateEpoch.AddDays(ushort.MaxValue):yyyy-MM-dd})"));
                return;
            }

            var request = new B3.Exchange.PostTrade.BustRequest(
                op.TradeId, op.TradeDate, op.CorrelationId, op.SecurityIdEcho,
                op.ReasonCode, op.BusterFirm, op.AttemptTransactTimeNanos);
            // ADR 0010: validation, dedup, audit write, file-routing and
            // amendments republish are owned by the orchestrator. The
            // dispatcher only owns the wire-emission half (TradeBust_57
            // frame on Accept).
            var outcome = _postTradeOrch!.ProcessBust(request, ChannelNumber, tradeDateDaysSinceEpoch);

            if (outcome.Kind == B3.Exchange.PostTrade.BustValidationKind.Accept)
            {
                // Emit TradeBust_57 frame; price/size echo come from the
                // matched fill so consumers see the same data the
                // original Trade_53 carried.
                uint rptSeq = _engine.AllocateNextRptSeq();
                _packetWritten = 0;
                int frameSize = B3.Umdf.WireEncoder.WireOffsets.FramingHeaderSize
                    + B3.Umdf.WireEncoder.WireOffsets.SbeMessageHeaderSize
                    + B3.Umdf.WireEncoder.WireOffsets.TradeBustBlockLength;
                var dst = ReserveOrFlush(frameSize);
                ushort tradeDateDays = (ushort)tradeDateDaysSinceEpoch;
                int written = B3.Umdf.WireEncoder.UmdfWireEncoder.WriteTradeBustFrame(dst,
                    outcome.MatchedFill.SecurityId, outcome.MatchedFill.PriceMantissa,
                    outcome.MatchedFill.Quantity, op.TradeId, tradeDateDays,
                    _timeSource.NowNanos(), rptSeq);
                Commit(written);
                FlushPacket();
                // ADR 0008 §4: post-EOD amendments republish runs AFTER
                // the UMDF frame is flushed so a frame-emit failure can
                // never leave amendments.csv announcing a bust the
                // consumers never saw. The orchestrator owns the failure-
                // swallow + retry-on-replay semantics.
                if (outcome.IsPostEodAccept)
                {
                    _postTradeOrch!.PublishPostEodAmendments(request, ChannelNumber);
                }
            }

            completion?.TrySetResult(new OperatorBustV2Outcome(outcome.Kind, outcome.ExistingCorrelationId));
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Operator command (gap-functional §5 / issue #201): transitions the
    /// supplied instrument to the requested <see cref="TradingPhase"/>. The
    /// transition is applied on the dispatch thread; if it changes the
    /// current phase the engine emits a <c>TradingPhaseChangedEvent</c>
    /// which is published as a <c>SecurityStatus_3</c> frame on the
    /// incremental channel under the current <c>SequenceVersion</c>.
    /// Idempotent: a no-op transition emits no frame. Returns <c>false</c>
    /// if the inbound queue is full. Safe to call from any thread.
    /// </summary>
    public bool EnqueueOperatorSetTradingPhase(long securityId, B3.Exchange.Matching.TradingPhase phase)
        => EnqueueOperatorSetTradingPhase(securityId, phase, completion: null);

    /// <summary>
    /// Issue #321: overload accepting a <see cref="TaskCompletionSource{TResult}"/>
    /// that is completed on the dispatch thread once the phase change
    /// has been applied (or faulted if the engine throws / the channel
    /// is WAL-halted). HTTP admin endpoint uses this to <c>await</c> the
    /// outcome with a bounded timeout.
    /// </summary>
    public bool EnqueueOperatorSetTradingPhase(long securityId, B3.Exchange.Matching.TradingPhase phase,
        TaskCompletionSource<PhaseChangeOutcome>? completion)
    {
        if (RejectIfWalHalted(WorkKind.OperatorSetTradingPhase))
        {
            completion?.TrySetException(new InvalidOperationException(
                $"channel {ChannelNumber} WAL-halted; SetTradingPhase rejected"));
            return false;
        }
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorSetTradingPhase, default, 0, false,
            0, 0, null, null, null, null,
            TradingPhase: new OperatorTradingPhase(securityId, phase),
            PhaseCompletion: completion)))
        {
            return true;
        }
        completion?.TrySetException(new InvalidOperationException(
            $"channel {ChannelNumber} inbound queue full; SetTradingPhase rejected"));
        return false;
    }

    /// <summary>
    /// Issue #321: operator-issued auction uncross. Allowed transitions
    /// are <c>Reserved → Open</c> (opening call) and
    /// <c>FinalClosingCall → Close</c> (closing call); other transitions
    /// fault the supplied completion source with
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool EnqueueOperatorUncrossAuction(long securityId, B3.Exchange.Matching.TradingPhase targetPhase,
        TaskCompletionSource<PhaseChangeOutcome>? completion = null)
    {
        if (RejectIfWalHalted(WorkKind.OperatorUncrossAuction))
        {
            completion?.TrySetException(new InvalidOperationException(
                $"channel {ChannelNumber} WAL-halted; UncrossAuction rejected"));
            return false;
        }
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorUncrossAuction, default, 0, false,
            0, 0, null, null, null, null,
            UncrossAuction: new OperatorUncrossAuction(securityId, targetPhase),
            PhaseCompletion: completion)))
        {
            return true;
        }
        completion?.TrySetException(new InvalidOperationException(
            $"channel {ChannelNumber} inbound queue full; UncrossAuction rejected"));
        return false;
    }

    private void ProcessSetTradingPhase(OperatorTradingPhase op, TaskCompletionSource<PhaseChangeOutcome>? completion)
    {
        AssertOnLoopThread();
        _pendingAuctionPrint = null;
        // Issue #322: halt overlay blocks operator phase commands. The
        // engine itself doesn't know about the halt overlay's "blocks
        // phase" semantics — the dispatcher enforces it so HTTP admin
        // callers see a deterministic 409 instead of a silent no-op.
        if (_haltSnapshot.ContainsKey(op.SecurityId))
        {
            completion?.TrySetException(new InvalidOperationException("instrument halted"));
            return;
        }
        try
        {
            B3.Exchange.Matching.TradingPhase prev;
            try { prev = _engine.GetTradingPhase(op.SecurityId); }
            catch (KeyNotFoundException ex)
            {
                completion?.TrySetException(ex);
                return;
            }
            // The engine emits the TradingPhaseChangedEvent inline (if the
            // transition is non-trivial); the sink writes a SecurityStatus_3
            // frame into the per-command packet buffer. Flush it as a
            // single-message packet so consumers see the status update
            // immediately, mirroring the trade-bust pattern.
            _packetWritten = 0;
            bool applied;
            try
            {
                applied = _engine.SetTradingPhase(op.SecurityId, op.Phase, _timeSource.NowNanos());
            }
            catch (Exception ex)
            {
                completion?.TrySetException(ex);
                return;
            }
            if (_packetWritten > 0) FlushPacket();
            var current = _engine.GetTradingPhase(op.SecurityId);
            completion?.TrySetResult(new PhaseChangeOutcome(applied, prev, current, UncrossPrint: null));
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Issue #321: process a queued <see cref="OperatorUncrossAuction"/>.
    /// Wraps <see cref="MatchingEngine.UncrossAuction"/>, captures the
    /// previous/current phase snapshot for the outcome, and surfaces
    /// engine validation faults
    /// (<see cref="InvalidOperationException"/>,
    /// <see cref="KeyNotFoundException"/>) through the optional
    /// completion source instead of crashing the dispatch loop.
    /// </summary>
    private void ProcessUncrossAuction(OperatorUncrossAuction op, TaskCompletionSource<PhaseChangeOutcome>? completion)
    {
        AssertOnLoopThread();
        _pendingAuctionPrint = null;
        _packetWritten = 0;
        if (_haltSnapshot.ContainsKey(op.SecurityId))
        {
            completion?.TrySetException(new InvalidOperationException("instrument halted"));
            return;
        }
        try
        {
            B3.Exchange.Matching.TradingPhase prev;
            try { prev = _engine.GetTradingPhase(op.SecurityId); }
            catch (KeyNotFoundException ex)
            {
                completion?.TrySetException(ex);
                return;
            }
            try
            {
                _engine.UncrossAuction(op.SecurityId, op.TargetPhase, _timeSource.NowNanos());
            }
            catch (InvalidOperationException ex)
            {
                completion?.TrySetException(ex);
                return;
            }
            catch (KeyNotFoundException ex)
            {
                completion?.TrySetException(ex);
                return;
            }
            if (_packetWritten > 0) FlushPacket();
            var current = _engine.GetTradingPhase(op.SecurityId);
            bool applied = current != prev;
            completion?.TrySetResult(new PhaseChangeOutcome(applied, prev, current, _pendingAuctionPrint));
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Issue #322: enqueue an administrative halt for
    /// <paramref name="securityId"/>. Sets the overlay on the dispatch
    /// thread, emits an <c>InstrumentHaltedEvent</c> through the engine
    /// (driving a UMDF <c>SecurityStatus_3</c>), and completes the
    /// supplied task with the resulting <see cref="HaltOutcome"/>.
    /// </summary>
    public bool EnqueueOperatorHalt(long securityId, B3.Exchange.Matching.HaltReason reason, string? note,
        TaskCompletionSource<HaltOutcome>? completion = null)
    {
        if (RejectIfWalHalted(WorkKind.OperatorHaltInstrument))
        {
            completion?.TrySetException(new InvalidOperationException(
                $"channel {ChannelNumber} WAL-halted; HaltInstrument rejected"));
            return false;
        }
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorHaltInstrument, default, 0, false,
            0, 0, null, null, null, null,
            Halt: new OperatorHalt(securityId, reason, note),
            HaltCompletion: completion)))
        {
            return true;
        }
        completion?.TrySetException(new InvalidOperationException(
            $"channel {ChannelNumber} inbound queue full; HaltInstrument rejected"));
        return false;
    }

    /// <summary>
    /// Issue #322: enqueue a resume for <paramref name="securityId"/>.
    /// </summary>
    public bool EnqueueOperatorResume(long securityId,
        TaskCompletionSource<HaltOutcome>? completion = null)
    {
        if (RejectIfWalHalted(WorkKind.OperatorResumeInstrument))
        {
            completion?.TrySetException(new InvalidOperationException(
                $"channel {ChannelNumber} WAL-halted; ResumeInstrument rejected"));
            return false;
        }
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorResumeInstrument, default, 0, false,
            0, 0, null, null, null, null,
            Resume: new OperatorResume(securityId),
            HaltCompletion: completion)))
        {
            return true;
        }
        completion?.TrySetException(new InvalidOperationException(
            $"channel {ChannelNumber} inbound queue full; ResumeInstrument rejected"));
        return false;
    }

    private void ProcessHalt(OperatorHalt op, TaskCompletionSource<HaltOutcome>? completion)
    {
        AssertOnLoopThread();
        _packetWritten = 0;
        try
        {
            B3.Exchange.Matching.HaltState prevState;
            bool wasHalted = _engine.IsHalted(op.SecurityId, out prevState);
            bool stateChanged;
            try
            {
                stateChanged = _engine.HaltInstrument(op.SecurityId, op.Reason, op.Note, _timeSource.NowNanos());
            }
            catch (KeyNotFoundException ex)
            {
                completion?.TrySetException(ex);
                return;
            }
            catch (InvalidOperationException ex)
            {
                completion?.TrySetException(ex);
                return;
            }
            if (stateChanged)
            {
                _haltSnapshot[op.SecurityId] = new HaltSnapshot(op.Reason, _engine.IsHalted(op.SecurityId, out var nowState) ? nowState.HaltedAtNanos : 0UL, op.Note);
            }
            if (_packetWritten > 0) FlushPacket();
            // Re-read so the outcome reflects the canonical engine state
            // (handles the no-op case where stateChanged=false but the
            // instrument was already halted with a different reason/note).
            _engine.IsHalted(op.SecurityId, out var current);
            if (stateChanged)
            {
                completion?.TrySetResult(new HaltOutcome(
                    StateChanged: true,
                    IsHaltedNow: true,
                    Reason: current.Reason,
                    HaltedAtNanos: current.HaltedAtNanos,
                    Note: current.Note));
            }
            else
            {
                completion?.TrySetResult(new HaltOutcome(
                    StateChanged: false,
                    IsHaltedNow: wasHalted,
                    Reason: wasHalted ? prevState.Reason : null,
                    HaltedAtNanos: wasHalted ? prevState.HaltedAtNanos : 0UL,
                    Note: wasHalted ? prevState.Note : null));
            }
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
            throw;
        }
    }

    private void ProcessResume(OperatorResume op, TaskCompletionSource<HaltOutcome>? completion)
    {
        AssertOnLoopThread();
        _packetWritten = 0;
        try
        {
            B3.Exchange.Matching.HaltState prevState;
            bool wasHalted = _engine.IsHalted(op.SecurityId, out prevState);
            bool stateChanged;
            try
            {
                stateChanged = _engine.ResumeInstrument(op.SecurityId, _timeSource.NowNanos());
            }
            catch (KeyNotFoundException ex)
            {
                completion?.TrySetException(ex);
                return;
            }
            catch (InvalidOperationException ex)
            {
                completion?.TrySetException(ex);
                return;
            }
            if (stateChanged)
            {
                _haltSnapshot.TryRemove(op.SecurityId, out _);
            }
            if (_packetWritten > 0) FlushPacket();
            completion?.TrySetResult(new HaltOutcome(
                StateChanged: stateChanged,
                IsHaltedNow: false,
                Reason: null,
                HaltedAtNanos: stateChanged && wasHalted ? prevState.HaltedAtNanos : 0UL,
                Note: stateChanged && wasHalted ? prevState.Note : null));
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// OPT-03 (ADR 0014): enqueue an end-of-trading-day expiry sweep for
    /// <paramref name="securityId"/>. On the dispatch thread the
    /// dispatcher (a) snapshots every resting <c>OrderID</c> for the
    /// security from the local <see cref="OrderRegistry"/>, (b) calls
    /// <see cref="MatchingEngine.MassCancel(IReadOnlyCollection{long}, MassCancelCommand)"/>
    /// to cancel them (driving per-order <c>ER_Cancel</c> +
    /// UMDF <c>OrderDelete</c> frames via the existing
    /// <c>OnOrderCanceled</c> sink path), and (c) calls
    /// <see cref="MatchingEngine.SetTradingPhase"/> to transition the
    /// security to <see cref="B3.Exchange.Matching.TradingPhase.Close"/>
    /// (emitting one terminal UMDF <c>SecurityStatus_3</c> CLOSE). Both
    /// effects pack into one packet via the standard per-command
    /// <c>FlushPacket</c> flow, so consumers observe the cancellations
    /// and the terminal status atomically.
    /// <para>
    /// Idempotent: a no-op call (no resting orders + already
    /// <c>Close</c>) returns <c>ExpireSecurityOutcome(0, false)</c>
    /// and emits no frames. Safe to call from any thread.
    /// </para>
    /// </summary>
    public bool EnqueueOperatorExpireSecurity(long securityId,
        TaskCompletionSource<ExpireSecurityOutcome>? completion = null)
    {
        if (RejectIfWalHalted(WorkKind.OperatorExpireSecurity))
        {
            completion?.TrySetException(new InvalidOperationException(
                $"channel {ChannelNumber} WAL-halted; ExpireSecurity rejected"));
            return false;
        }
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorExpireSecurity, default, 0, false,
            0, 0, null, null, null, null,
            ExpireSecurity: new OperatorExpireSecurity(securityId),
            ExpireCompletion: completion)))
        {
            return true;
        }
        completion?.TrySetException(new InvalidOperationException(
            $"channel {ChannelNumber} inbound queue full; ExpireSecurity rejected"));
        return false;
    }

    private void ProcessExpireSecurity(OperatorExpireSecurity op,
        TaskCompletionSource<ExpireSecurityOutcome>? completion)
    {
        AssertOnLoopThread();
        _packetWritten = 0;
        try
        {
            // Snapshot every order owned for this security (regardless of
            // session/firm). The registry is single-writer per ADR 0009,
            // so iterating it on the dispatch thread is race-free.
            var orderIds = _orders.SnapshotForSecurity(op.SecurityId);
            int cancelled = 0;
            if (orderIds.Count > 0)
            {
                try
                {
                    cancelled = _engine.MassCancel(orderIds,
                        new B3.Exchange.Matching.MassCancelCommand(op.SecurityId, null, _timeSource.NowNanos()));
                }
                catch (KeyNotFoundException ex)
                {
                    completion?.TrySetException(ex);
                    return;
                }
            }

            bool phaseChanged;
            try
            {
                phaseChanged = _engine.SetTradingPhase(op.SecurityId,
                    B3.Exchange.Matching.TradingPhase.Close, _timeSource.NowNanos());
            }
            catch (KeyNotFoundException ex)
            {
                // Flush any cancel frames already buffered so we don't
                // drop them on the floor when the phase transition fails.
                if (_packetWritten > 0) FlushPacket();
                completion?.TrySetException(ex);
                return;
            }

            if (_packetWritten > 0) FlushPacket();
            completion?.TrySetResult(new ExpireSecurityOutcome(cancelled, phaseChanged));
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// GAP-23 / issue #499: enqueue an end-of-trading-day Good-Till-Date
    /// expiry sweep for the trading day being closed
    /// (<paramref name="currentDate"/>, a B3 <c>LocalMktDate</c> = days
    /// since Unix epoch). On the dispatch thread the dispatcher cancels
    /// every resting GTD order across the channel's books whose
    /// <c>ExpireDate</c> is on or before that date, driving per-order
    /// <c>ER_Cancel</c> + UMDF <c>OrderDelete</c> frames via the existing
    /// <c>OnOrderCanceled</c> sink path, packed into one packet. No trading
    /// phase changes. Idempotent and safe to call from any thread.
    /// </summary>
    public bool EnqueueOperatorExpireGtd(ushort currentDate,
        TaskCompletionSource<ExpireGtdOutcome>? completion = null)
    {
        if (RejectIfWalHalted(WorkKind.OperatorExpireGtd))
        {
            completion?.TrySetException(new InvalidOperationException(
                $"channel {ChannelNumber} WAL-halted; ExpireGtd rejected"));
            return false;
        }
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorExpireGtd, default, 0, false,
            0, 0, null, null, null, null,
            ExpireGtd: new OperatorExpireGtd(currentDate),
            ExpireGtdCompletion: completion)))
        {
            return true;
        }
        completion?.TrySetException(new InvalidOperationException(
            $"channel {ChannelNumber} inbound queue full; ExpireGtd rejected"));
        return false;
    }

    private void ProcessExpireGtd(OperatorExpireGtd op,
        TaskCompletionSource<ExpireGtdOutcome>? completion)
    {
        AssertOnLoopThread();
        _packetWritten = 0;
        try
        {
            int cancelled = _engine.ExpireGtdOrders(op.CurrentDate, _timeSource.NowNanos());
            if (_packetWritten > 0) FlushPacket();
            completion?.TrySetResult(new ExpireGtdOutcome(cancelled));
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// GAP-26 / issue #498: enqueue a daily Good-Till restatement sweep for
    /// the trading day being closed (<paramref name="currentDate"/>, a B3
    /// <c>LocalMktDate</c> = days since the Unix epoch). On the dispatch
    /// thread the engine emits a private restatement
    /// <c>ExecutionReport_Modify</c> (OrdStatus=RESTATED,
    /// ExecRestatementReason=GT_RESTATEMENT) for every surviving GTC order
    /// and every GTD order whose <c>ExpireDate</c> is strictly after
    /// <paramref name="currentDate"/>, routed back to the owning session via
    /// the <c>OnOrderRestated</c> sink path. The book is unchanged: no UMDF
    /// frame, no <c>RptSeq</c> advance, no phase change. Idempotent and safe
    /// to call from any thread.
    /// </summary>
    public bool EnqueueOperatorRestateGt(ushort currentDate,
        TaskCompletionSource<RestateGtOutcome>? completion = null)
    {
        if (RejectIfWalHalted(WorkKind.OperatorRestateGt))
        {
            completion?.TrySetException(new InvalidOperationException(
                $"channel {ChannelNumber} WAL-halted; RestateGt rejected"));
            return false;
        }
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.OperatorRestateGt, default, 0, false,
            0, 0, null, null, null, null,
            RestateGt: new OperatorRestateGt(currentDate),
            RestateGtCompletion: completion)))
        {
            return true;
        }
        completion?.TrySetException(new InvalidOperationException(
            $"channel {ChannelNumber} inbound queue full; RestateGt rejected"));
        return false;
    }

    private void ProcessRestateGt(OperatorRestateGt op,
        TaskCompletionSource<RestateGtOutcome>? completion)
    {
        AssertOnLoopThread();
        _packetWritten = 0;
        try
        {
            int restated = _engine.RestateGtOrders(op.CurrentDate, _timeSource.NowNanos());
            // Restatement emits no UMDF frames (the book is unchanged), so
            // _packetWritten stays 0; the guard mirrors the sibling sweeps.
            if (_packetWritten > 0) FlushPacket();
            completion?.TrySetResult(new RestateGtOutcome(restated));
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
            throw;
        }
    }
}
