using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using RejectEvent = B3.Exchange.Matching.RejectEvent;

namespace B3.Exchange.Core;

/// <summary>
/// Dispatch-loop facet of <see cref="ChannelDispatcher"/> (issue #168 split):
/// the per-work-item entry point invoked by <c>RunLoopAsync</c>, plus the
/// helper that emits an UnknownOrderId reject when the gateway-side
/// resolver did not pre-fill an OrderId.
/// </summary>
public sealed partial class ChannelDispatcher
{
    internal void ProcessOne(in WorkItem item)
    {
        AssertOnLoopThread();
        // Issue #173: dispatch_wait = enqueue → loop pickup. EnqueueTicks
        // is 0 for in-process synthetic items (e.g. snapshot tick scheduled
        // via Timer); skip the observation in that case to avoid skewing
        // the histogram with 1970-style "ages".
        long pickupTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (item.EnqueueTicks > 0 && _metrics != null)
            _metrics.DispatchWait.ObserveTicks(pickupTicks - item.EnqueueTicks);

        if (item.Kind == WorkKind.SnapshotRotation || item.Kind == WorkKind.OperatorSnapshotNow)
        {
            // Snapshot ticks bypass the per-command incremental packet buffer
            // entirely — they have their own sink + sequence space owned by
            // the rotator and emit one or more complete packets directly.
            _snapshotRotator?.PublishNext();
            return;
        }

        if (item.Kind == WorkKind.PriceBandPublish)
        {
            AssertOnLoopThread();
            if (Volatile.Read(ref _walHalted) != 0)
            {
                _metrics?.IncWalHaltReject();
                LogWalHalted(ChannelNumber, item.Kind);
                return;
            }

            if (_priceBandPublisher?.PublishOnce(FrameSink, _engine.AllocateNextRptSeq, _timeSource.NowNanos()) > 0)
            {
                FlushPacket();
                // PriceBand_22 republish consumes engine RptSeq values, so persist
                // them like the other synthetic incremental emissions.
                OnAfterCommandFlushed(force: true);
            }
            return;
        }

        if (item.Kind == WorkKind.OperatorPersistSnapshot)
        {
            // Issue #271: admin "snapshot/force" — capture + persist the
            // engine state on the loop thread and write through the
            // configured persister. Bypasses the throttle.
            OnAfterCommandFlushed(force: true);
            return;
        }

        if (item.Kind == WorkKind.AuditCheckpoint)
        {
            ProcessAuditCheckpoint(item.AuditCheckpoint!);
            return;
        }

        if (item.Kind == WorkKind.ShutdownBarrier)
        {
            FlushPendingSnapshotOnShutdownSafely();
            item.ShutdownBarrier!.TrySetResult(true);
            return;
        }

        // Issue #286 follow-up: producer-side gates (RejectIfWalHalted)
        // catch most halts, but a work item already in the channel when
        // the WAL flips to halted would still reach here and mutate the
        // engine. Drop any state-mutating work item observed on the loop
        // after halt; snapshot/persist items above are intentionally
        // allowed through. This is the last line of defence — readiness
        // probes already report the channel as unhealthy.
        if (Volatile.Read(ref _walHalted) != 0)
        {
            _metrics?.IncWalHaltReject();
            LogWalHalted(ChannelNumber, item.Kind);
            // Issue #321 review (gpt-5.5): a phase work item already in the
            // queue when WAL flips to halted is dropped here — but the HTTP
            // caller is awaiting item.PhaseCompletion. Fail the TCS so the
            // request fails fast with a real reason instead of timing out.
            item.PhaseCompletion?.TrySetException(
                new InvalidOperationException(
                    $"channel {ChannelNumber} WAL-halted; phase command rejected"));
            // Issue #322: same applies to halt/resume HTTP callers.
            item.HaltCompletion?.TrySetException(
                new InvalidOperationException(
                    $"channel {ChannelNumber} WAL-halted; halt/resume command rejected"));
            // OPT-03 (ADR 0014): expiry sweep awaits ExpireCompletion.
            item.ExpireCompletion?.TrySetException(
                new InvalidOperationException(
                    $"channel {ChannelNumber} WAL-halted; ExpireSecurity rejected"));
            // GAP-23 (#499) / GAP-26 (#498): GTD-expiry and GT-restatement
            // sweeps await their completions too.
            item.ExpireGtdCompletion?.TrySetException(
                new InvalidOperationException(
                    $"channel {ChannelNumber} WAL-halted; ExpireGtd rejected"));
            item.RestateGtCompletion?.TrySetException(
                new InvalidOperationException(
                    $"channel {ChannelNumber} WAL-halted; RestateGt rejected"));
            // Issue #506: the Day-expiry sweep awaits its completion too.
            item.ExpireDayCompletion?.TrySetException(
                new InvalidOperationException(
                    $"channel {ChannelNumber} WAL-halted; ExpireDay rejected"));
            return;
        }

        if (item.Kind == WorkKind.OperatorBumpVersion)
        {
            ProcessBumpVersion();
            // Issue #260 follow-up (review feedback on PR #261): operator
            // BumpVersion publishes ChannelReset_11 and clears engine
            // state. Persist post-flush so a crash after the reset cannot
            // resurrect the pre-reset book on next boot.
            // Issue #267: operator commands always force-persist, bypassing
            // the throttle policy.
            OnAfterCommandFlushed(force: true);
            return;
        }

        if (item.Kind == WorkKind.OperatorTradeBust)
        {
            ProcessTradeBust(item.TradeBust!);
            // Persist after publishing TradeBust_57 so the consumed RptSeq
            // (engine.AllocateNextRptSeq) survives restart — otherwise a
            // restart would re-issue the same RptSeq for a different event.
            OnAfterCommandFlushed(force: true);
            return;
        }

        if (item.Kind == WorkKind.OperatorBustV2)
        {
            ProcessOperatorBustV2(item.BustV2!, item.BustCompletion);
            // Persist after every accept so the consumed RptSeq + dedup
            // index state mirror durability of the audit-log write.
            OnAfterCommandFlushed(force: true);
            return;
        }

        if (item.Kind == WorkKind.OperatorSetTradingPhase)
        {
            ProcessSetTradingPhase(item.TradingPhase!, item.PhaseCompletion);
            // Persist after the phase mutation so the engine's per-symbol
            // _phaseById map (captured into EngineStateSnapshot.Phases)
            // and any RptSeq advance from SecurityStatus_3 emission
            // survive restart.
            OnAfterCommandFlushed(force: true);
            return;
        }

        if (item.Kind == WorkKind.OperatorUncrossAuction)
        {
            ProcessUncrossAuction(item.UncrossAuction!, item.PhaseCompletion);
            OnAfterCommandFlushed(force: true);
            return;
        }

        if (item.Kind == WorkKind.OperatorHaltInstrument)
        {
            ProcessHalt(item.Halt!, item.HaltCompletion);
            OnAfterCommandFlushed(force: true);
            return;
        }

        if (item.Kind == WorkKind.OperatorResumeInstrument)
        {
            ProcessResume(item.Resume!, item.HaltCompletion);
            OnAfterCommandFlushed(force: true);
            return;
        }

        if (item.Kind == WorkKind.OperatorExpireSecurity)
        {
            ProcessExpireSecurity(item.ExpireSecurity!, item.ExpireCompletion);
            // OPT-03 (ADR 0014): force-persist after expiry so the engine's
            // _phaseById flip to Close and any RptSeq consumed by the
            // SecurityStatus_3 + per-order ER_Cancel frames survive a
            // restart — mirroring the OperatorSetTradingPhase path.
            OnAfterCommandFlushed(force: true);
            return;
        }

        if (item.Kind == WorkKind.OperatorExpireGtd)
        {
            ProcessExpireGtd(item.ExpireGtd!, item.ExpireGtdCompletion);
            // GAP-23 (#499): force-persist after the sweep so the cancelled
            // GTD orders are gone from the engine snapshot and any RptSeq
            // consumed by the per-order ER_Cancel frames survive a restart.
            // Operator commands are not WAL-logged; the forced snapshot is
            // the durability boundary — mirrors the ExpireSecurity path.
            OnAfterCommandFlushed(force: true);
            return;
        }

        if (item.Kind == WorkKind.OperatorRestateGt)
        {
            ProcessRestateGt(item.RestateGt!, item.RestateGtCompletion);
            // GAP-26 (#498): a restatement does not mutate engine state (no
            // book change, no RptSeq advance), so the forced snapshot is a
            // no-op delta. We still force-persist to keep the operator-command
            // durability contract uniform with the sibling sweeps above.
            OnAfterCommandFlushed(force: true);
            return;
        }

        if (item.Kind == WorkKind.OperatorExpireDay)
        {
            ProcessExpireDay(item.ExpireDay!, item.ExpireDayCompletion);
            // Issue #506: force-persist after the sweep so the cancelled Day
            // orders are gone from the engine snapshot and any RptSeq consumed
            // by the per-order ER + UMDF OrderDelete frames survive a restart.
            // Operator commands are not WAL-logged; the forced snapshot is the
            // durability boundary — mirrors the GTD-expiry path.
            OnAfterCommandFlushed(force: true);
            return;
        }

        _currentSession = item.Session;
        _currentFirm = item.Firm;
        _hasCurrentSession = item.HasSession;
        _currentClOrdId = item.ClOrdId;
        _currentOrigClOrdId = item.OrigClOrdId;
        _currentReceivedTimeNanos = item.Kind switch
        {
            WorkKind.New => item.NewOrder?.EnteredAtNanos ?? ulong.MaxValue,
            WorkKind.Cancel => item.Cancel?.EnteredAtNanos ?? ulong.MaxValue,
            WorkKind.Replace => item.Replace?.EnteredAtNanos ?? ulong.MaxValue,
            WorkKind.Cross => item.Cross?.Buy.EnteredAtNanos ?? ulong.MaxValue,
            WorkKind.MassCancel => item.MassCancel?.Command.EnteredAtNanos ?? ulong.MaxValue,
            _ => ulong.MaxValue,
        };
        _packetWritten = 0;
        bool succeeded = false;
        // Issue #269: write-ahead the command before the engine
        // observes it. Only New/Cancel/Replace are durable today;
        // operator commands force-snapshot post-flush so the
        // resulting state is captured directly. _lastAppliedSeq is
        // advanced by WalAppendIfEnabled regardless, including in
        // replay mode, so the on-loop counter stays consistent.
        // Issue #286: under WalAppendFailurePolicy.Halt the call
        // returns false on the first append failure — the command
        // must NOT reach the engine (state would diverge from the
        // WAL on next replay). We bail out of ProcessOne entirely.
        if (item.Kind == WorkKind.New && WouldExceedOpenOrderLimit(item.Firm))
        {
            _metrics?.IncOrdersIn();
            EmitOpenOrderLimitReject(item.NewOrder!);
            return;
        }
        if (item.Kind == WorkKind.Cross && WouldExceedOpenOrderLimit(item.Firm, requiredSlots: 2))
        {
            _metrics?.IncOrdersIn();
            EmitOpenOrderLimitReject(item.Cross!);
            return;
        }
        if (item.Kind is WorkKind.New or WorkKind.Cancel or WorkKind.Replace)
        {
            if (!WalAppendIfEnabled(in item))
            {
                _metrics?.IncWalHaltReject();
                return;
            }
        }
        long engineStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // Issue #175: open engine.process as a child of the dispatch.enqueue
        // span captured at enqueue time. The dispatch loop crosses thread
        // boundaries from the gateway IO thread, so propagation must be
        // explicit — Activity.Current would not carry the context here.
        // Fast path: skip the virtual StartActivity call when no listeners
        // are attached (round-2 perf #11).
        System.Diagnostics.Activity? engineSpan = null;
        if (ExchangeTelemetry.Source.HasListeners())
        {
            engineSpan = ExchangeTelemetry.Source.StartActivity(
                ExchangeTelemetry.SpanEngineProcess,
                System.Diagnostics.ActivityKind.Internal,
                item.ParentContext);
            if (engineSpan is not null)
            {
                engineSpan.SetTag(ExchangeTelemetry.TagChannel, (int)ChannelNumber);
                engineSpan.SetTag(ExchangeTelemetry.TagWorkKind, WorkKindName(item.Kind));
                if (item.HasSession) engineSpan.SetTag(ExchangeTelemetry.TagSession, item.Session.Value);
                if (item.ClOrdId != 0) engineSpan.SetTag(ExchangeTelemetry.TagClOrdId, (long)item.ClOrdId);
            }
        }
        try
        {
            switch (item.Kind)
            {
                case WorkKind.New:
                    {
                        _metrics?.IncOrdersIn();
                        var newOrder = item.NewOrder!;
                        BeginAggressor(newOrder.Quantity);
                        // Issue #484: IOC/FOK orders are always terminal — never rest.
                        // Flag so OnTrade forces leavesQty=0 on every fill.
                        _aggressorIsIoc = newOrder.Tif is B3.Exchange.Matching.TimeInForce.IOC or B3.Exchange.Matching.TimeInForce.FOK;
                        _engine.Submit(newOrder);
                        break;
                    }
                case WorkKind.Cancel:
                    {
                        _metrics?.IncOrdersIn();
                        var cancel = item.Cancel!;
                        if (cancel.OrderId == 0)
                        {
                            // HostRouter is expected to pre-resolve OrigClOrdID
                            // → OrderId via the Gateway-side OrderOwnershipMap.
                            // Defensive guard: surface a deterministic reject if
                            // an unresolved command still reaches the engine.
                            EmitUnknownOrderIdReject(cancel.ClOrdId, cancel.SecurityId, cancel.EnteredAtNanos);
                            break;
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
                            EmitUnknownOrderIdReject(replace.ClOrdId, replace.SecurityId, replace.EnteredAtNanos);
                            break;
                        }
                        // Issue #482: for priority-lost Replace (price change → DEL + NEW),
                        // the engine emits OrderAcceptedEvent for the replacement order.
                        // Pre-seed the aggressor tracker with the original order's CumQty
                        // so OnOrderAccepted registers the new order with inherited fills
                        // instead of resetting to zero.
                        if (_orders.TryResolve(replace.OrderId, out var orig))
                        {
                            _aggressorOrigQty = orig.CumQty + replace.NewQuantity;
                            _aggressorCumQty = orig.CumQty;
                        }
                        // Issue #484: if the Replace changes TIF to IOC/FOK the
                        // replacement order is terminal and leavesQty must be 0
                        // on every ER_Trade (see OnTrade / _aggressorIsIoc).
                        _aggressorIsIoc = replace.NewTif is B3.Exchange.Matching.TimeInForce.IOC or B3.Exchange.Matching.TimeInForce.FOK;
                        _engine.Replace(replace);
                        break;
                    }
                case WorkKind.Cross:
                    {
                        // Atomic two-leg submission. Both legs share the
                        // same _currentReceivedTimeNanos so receivedTime on
                        // each ER frame matches the original cross frame's
                        // ingress timestamp. _currentClOrdId is rebound
                        // before each leg so ER routing uses the correct
                        // per-leg ClOrdID. The packet buffer accumulates
                        // both legs' UMDF events and flushes once at the
                        // end of this dispatch turn.
                        //
                        // Issue #218 (Onda L · L5): respect
                        // CrossPrioritization (BuyPrioritized / None →
                        // Buy first; SellPrioritized → Sell first), and
                        // implement CrossType=AgainstBook by inserting a
                        // sweep phase before the cross prints internally.
                        var cross = item.Cross!;
                        bool buyFirst = cross.CrossPrioritization != CrossPrioritization.SellPrioritized;
                        var prioLeg = buyFirst ? cross.Buy : cross.Sell;
                        ulong prioClOrd = buyFirst ? cross.BuyClOrdIdValue : cross.SellClOrdIdValue;
                        var otherLeg = buyFirst ? cross.Sell : cross.Buy;
                        ulong otherClOrd = buyFirst ? cross.SellClOrdIdValue : cross.BuyClOrdIdValue;

                        if (prioLeg.PreTradeRejectReason is not null || otherLeg.PreTradeRejectReason is not null)
                        {
                            _metrics?.IncOrdersIn();
                            _currentClOrdId = prioClOrd;
                            BeginAggressor(prioLeg.Quantity);
                            _engine.Submit(prioLeg);
                            _metrics?.IncOrdersIn();
                            _currentClOrdId = otherClOrd;
                            BeginAggressor(otherLeg.Quantity);
                            _engine.Submit(otherLeg);
                            break;
                        }

                        if (cross.CrossType == CrossType.AgainstBook && cross.MaxSweepQty > 0)
                        {
                            // Phase 1: prioritized leg sweeps the opposing
                            // public book at cross price (or better) up to
                            // min(MaxSweepQty, OrderQty) via Limit IOC.
                            // Any leftover from the cap is canceled (IOC).
                            long sweepQty = Math.Min(cross.MaxSweepQty, prioLeg.Quantity);
                            var sweepLeg = prioLeg with
                            {
                                Type = B3.Exchange.Matching.OrderType.Limit,
                                Tif = B3.Exchange.Matching.TimeInForce.IOC,
                                Quantity = sweepQty,
                            };
                            _metrics?.IncOrdersIn();
                            _currentClOrdId = prioClOrd;
                            long swept = 0;
                            _crossSweepFilledQty = 0;
                            _crossSweepAggressorOrderId = _engine.PeekNextOrderId;
                            try
                            {
                                BeginAggressor(sweepLeg.Quantity);
                                _engine.SubmitCrossSweep(sweepLeg);
                                swept = _crossSweepFilledQty.GetValueOrDefault();
                            }
                            finally
                            {
                                _crossSweepFilledQty = null;
                                _crossSweepAggressorOrderId = 0;
                            }

                            // Phase 2: residual prioritized leg as the
                            // original Limit Day at cross price; rests on
                            // the book ready for the other leg to cross.
                            long residual = prioLeg.Quantity - swept;
                            if (residual > 0)
                            {
                                _metrics?.IncOrdersIn();
                                _currentClOrdId = prioClOrd;
                                BeginAggressor(residual);
                                _engine.Submit(prioLeg with { Quantity = residual });
                            }

                            // Phase 3: the other leg at full OrderQty —
                            // crosses against the resting prioritized
                            // residual; any remainder rests on the book
                            // (price-time priority handles the rest).
                            _metrics?.IncOrdersIn();
                            _currentClOrdId = otherClOrd;
                            BeginAggressor(otherLeg.Quantity);
                            _engine.Submit(otherLeg);
                        }
                        else
                        {
                            // AON cross (default): existing behavior. The
                            // prioritized side rests, the other side
                            // crosses against it → 1 internal trade at
                            // cross price.
                            _metrics?.IncOrdersIn();
                            _currentClOrdId = prioClOrd;
                            BeginAggressor(prioLeg.Quantity);
                            _engine.Submit(prioLeg);
                            _metrics?.IncOrdersIn();
                            _currentClOrdId = otherClOrd;
                            BeginAggressor(otherLeg.Quantity);
                            _engine.Submit(otherLeg);
                        }
                        break;
                    }
                case WorkKind.MassCancel:
                    {
                        // OrderIds are pre-resolved by HostRouter against
                        // the Gateway-side OrderOwnershipMap (filter is
                        // session/firm/Side/SecurityId; spec §4.8 / #GAP-19).
                        // Engine sees only a flat orderId list and emits one
                        // ER_Cancel per matching order via OnOrderCanceled,
                        // routed back to the originating session by the
                        // Gateway router.
                        var mc = item.MassCancel!;
                        if (mc.OrderIds.Count > 0)
                            _engine.MassCancel(mc.OrderIds, mc.Command);
                        break;
                    }
                case WorkKind.DecodeError:
                    if (_hasCurrentSession)
                    {
                        // RejectEvent.ClOrdId is unused by the encoder when
                        // clOrdIdValue is passed explicitly (the encoder
                        // prefers the ulong over ulong.TryParse(e.ClOrdId));
                        // skip the alloc.
                        _outbound.WriteExecutionReportReject(_currentSession,
                            new RejectEvent(string.Empty, 0, 0, RejectReason.UnknownInstrument, _timeSource.NowNanos()),
                            _currentClOrdId, CurrentDurability);
                        _metrics?.IncExecutionReport(ExecutionReportKind.Reject);
                    }
                    break;
            }
            LogCommandProcessed(ChannelNumber, item.Kind, _currentClOrdId);
            succeeded = true;
        }
        finally
        {
            // Issue #173: engine_process = engine entry → engine exit
            // (regardless of crash/success). outbound_emit measured
            // separately around FlushPacket so the two phases are
            // distinguishable in the histogram.
            long engineEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_metrics != null)
                _metrics.EngineProcess.ObserveTicks(engineEnd - engineStart);
            // Issue #170: do not publish a half-built UMDF packet if the
            // command crashed mid-flight — the dispatcher loop will catch
            // the exception, count the crash, and move on; flushing
            // partial state would corrupt downstream consumers.
            if (succeeded)
            {
                RecordBookCountsAfterWorkItem(in item);
                long flushStart = engineEnd;
                // Fast path: skip the virtual StartActivity call when no
                // listeners are attached (round-2 perf #11).
                if (ExchangeTelemetry.Source.HasListeners())
                {
                    using var flushSpan = ExchangeTelemetry.Source.StartActivity(
                        ExchangeTelemetry.SpanOutboundEmit,
                        System.Diagnostics.ActivityKind.Producer);
                    if (flushSpan is not null)
                        flushSpan.SetTag(ExchangeTelemetry.TagChannel, (int)ChannelNumber);
                    int bytes = _packetWritten;
                    FlushPacket();
                    if (flushSpan is not null)
                        flushSpan.SetTag(ExchangeTelemetry.TagBytes, bytes);
                }
                else
                {
                    FlushPacket();
                }
                if (_metrics != null)
                    _metrics.OutboundEmit.ObserveTicks(System.Diagnostics.Stopwatch.GetTimestamp() - flushStart);
                // Issue #260: persist post-flush so any consumer-visible
                // event corresponds to a durable snapshot on disk before
                // the next command is observed. Best-effort: failures are
                // logged and swallowed by the helper.
                OnAfterCommandFlushed();
            }
            else
            {
                _packetWritten = 0;
            }
            _currentSession = default;
            _currentFirm = 0;
            _hasCurrentSession = false;
            _currentClOrdId = 0;
            _currentOrigClOrdId = 0;
            _currentReceivedTimeNanos = ulong.MaxValue;
            _aggressorOrigQty = 0;
            _aggressorCumQty = 0;
            // Issue #484: flush the last buffered IOC aggressor ER with
            // leavesQty=0 before clearing the IOC flag and session context.
            if (_hasPendingIocER)
            {
                _outbound.WriteExecutionReportTrade(_pendingIocER.Session, _pendingIocER.Event,
                    isAggressor: true, ownerOrderId: _pendingIocER.OwnerOrderId,
                    clOrdIdValue: _pendingIocER.ClOrdId,
                    leavesQty: 0, cumQty: _pendingIocER.CumQty,
                    durability: _pendingIocER.Durability);
                _hasPendingIocER = false;
            }
            _aggressorIsIoc = false;
            engineSpan?.Dispose();
        }
    }

    /// <summary>
    /// Issue #319: rebinds the outermost-command aggressor tracker to a
    /// new submit (NewOrder, or one leg of a Cross). Resets
    /// <c>_aggressorCumQty</c> to 0 and stamps
    /// <c>_aggressorOrigQty</c> with the order quantity the engine is
    /// about to process. Subsequent <c>OnTrade</c> sink invocations on
    /// the aggressor side accumulate against this counter so
    /// <c>ER_Trade</c> emits monotonically-cumulative
    /// <c>cumQty</c>/<c>leavesQty</c>.
    /// </summary>
    private void BeginAggressor(long originalQty)
    {
        _aggressorOrigQty = originalQty;
        _aggressorCumQty = 0;
    }

    private void RecordBookCountsAfterWorkItem(in WorkItem item)
    {
        if (_metrics is null) return;
        switch (item.Kind)
        {
            case WorkKind.New when item.NewOrder is not null:
                RecordBookCount(item.NewOrder.SecurityId);
                break;
            case WorkKind.Cancel when item.Cancel is not null:
                RecordBookCount(item.Cancel.SecurityId);
                break;
            case WorkKind.Replace when item.Replace is not null:
                RecordBookCount(item.Replace.SecurityId);
                break;
            case WorkKind.Cross when item.Cross is not null:
                RecordBookCount(item.Cross.Buy.SecurityId);
                break;
            case WorkKind.MassCancel when item.MassCancel is not null && item.MassCancel.Command.SecurityId != 0:
                RecordBookCount(item.MassCancel.Command.SecurityId);
                break;
            case WorkKind.MassCancel:
                RecordAllBookCounts();
                break;
        }
    }

    private void RecordBookCount(long securityId)
    {
        if (_metrics is null) return;
        try { _metrics.SetBookLiveOrders(securityId, _engine.OrderCount(securityId)); }
        catch (KeyNotFoundException) { }
    }

    private void RecordAllBookCounts()
    {
        if (_metrics is null) return;
        foreach (var sample in _engine.OrderCountsSnapshot())
            _metrics.SetBookLiveOrders(sample.SecurityId, sample.Count);
    }

    private void EmitUnknownOrderIdReject(string clOrdId, long securityId, ulong nowNanos)
    {
        // Surface the failure directly as an ER_Reject so the client gets a
        // clear, deterministic response instead of a silently dropped command.
        if (_hasCurrentSession)
        {
            _outbound.WriteExecutionReportReject(_currentSession,
                new RejectEvent(clOrdId, securityId, 0, RejectReason.UnknownOrderId, nowNanos),
                _currentClOrdId, CurrentDurability);
            _metrics?.IncExecutionReport(ExecutionReportKind.Reject);
        }
    }

    private bool WouldExceedOpenOrderLimit(uint enteringFirm, int requiredSlots = 1)
    {
        int current = _openOrdersByFirm.TryGetValue(enteringFirm, out int count) ? count : 0;
        return current + requiredSlots > _maxOpenOrdersPerFirm;
    }

    private void EmitOpenOrderLimitReject(NewOrderCommand order)
    {
        if (!_hasCurrentSession) return;
        _outbound.WriteExecutionReportReject(_currentSession,
            new RejectEvent(order.ClOrdId, order.SecurityId, 0,
                RejectReason.OrderExceedsLimit, order.EnteredAtNanos, order.Memo),
            _currentClOrdId, CurrentDurability);
        _metrics?.IncExecutionReport(ExecutionReportKind.Reject);
    }

    private void EmitOpenOrderLimitReject(CrossOrderCommand cross)
    {
        if (!_hasCurrentSession) return;

        _currentClOrdId = cross.BuyClOrdIdValue;
        EmitOpenOrderLimitReject(cross.Buy);
        _currentClOrdId = cross.SellClOrdIdValue;
        EmitOpenOrderLimitReject(cross.Sell);
    }
}
