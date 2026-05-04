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

        if (item.Kind == WorkKind.OperatorBumpVersion)
        {
            ProcessBumpVersion();
            return;
        }

        if (item.Kind == WorkKind.OperatorTradeBust)
        {
            ProcessTradeBust(item.TradeBust!);
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
            WorkKind.MassCancel => item.MassCancel?.EnteredAtNanos ?? ulong.MaxValue,
            _ => ulong.MaxValue,
        };
        _packetWritten = 0;
        bool succeeded = false;
        long engineStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // Issue #175: open engine.process as a child of the dispatch.enqueue
        // span captured at enqueue time. The dispatch loop crosses thread
        // boundaries from the gateway IO thread, so propagation must be
        // explicit — Activity.Current would not carry the context here.
        using var engineSpan = ExchangeTelemetry.Source.StartActivity(
            ExchangeTelemetry.SpanEngineProcess,
            System.Diagnostics.ActivityKind.Internal,
            item.ParentContext);
        if (engineSpan is not null)
        {
            engineSpan.SetTag(ExchangeTelemetry.TagChannel, (int)ChannelNumber);
            engineSpan.SetTag(ExchangeTelemetry.TagWorkKind, item.Kind.ToString());
            if (item.HasSession) engineSpan.SetTag(ExchangeTelemetry.TagSession, item.Session.Value);
            if (item.ClOrdId != 0) engineSpan.SetTag(ExchangeTelemetry.TagClOrdId, (long)item.ClOrdId);
        }
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
                        var cross = item.Cross!;
                        _metrics?.IncOrdersIn();
                        _currentClOrdId = cross.BuyClOrdIdValue;
                        _engine.Submit(cross.Buy);
                        _metrics?.IncOrdersIn();
                        _currentClOrdId = cross.SellClOrdIdValue;
                        _engine.Submit(cross.Sell);
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
                            _engine.MassCancel(mc.OrderIds, mc.EnteredAtNanos);
                        break;
                    }
                case WorkKind.DecodeError:
                    if (_hasCurrentSession)
                    {
                        _outbound.WriteExecutionReportReject(_currentSession,
                            new RejectEvent(_currentClOrdId.ToString(), 0, 0, RejectReason.UnknownInstrument, _nowNanos()),
                            _currentClOrdId);
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
                long flushStart = engineEnd;
                using (var flushSpan = ExchangeTelemetry.Source.StartActivity(
                    ExchangeTelemetry.SpanOutboundEmit,
                    System.Diagnostics.ActivityKind.Producer))
                {
                    if (flushSpan is not null)
                        flushSpan.SetTag(ExchangeTelemetry.TagChannel, (int)ChannelNumber);
                    int bytes = _packetWritten;
                    FlushPacket();
                    if (flushSpan is not null)
                        flushSpan.SetTag(ExchangeTelemetry.TagBytes, bytes);
                }
                if (_metrics != null)
                    _metrics.OutboundEmit.ObserveTicks(System.Diagnostics.Stopwatch.GetTimestamp() - flushStart);
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
        }
    }

    private void EmitUnknownOrderIdReject(string clOrdId, long securityId, ulong nowNanos)
    {
        // Surface the failure directly as an ER_Reject so the client gets a
        // clear, deterministic response instead of a silently dropped command.
        if (_hasCurrentSession)
        {
            _outbound.WriteExecutionReportReject(_currentSession,
                new RejectEvent(clOrdId, securityId, 0, RejectReason.UnknownOrderId, nowNanos),
                _currentClOrdId);
            _metrics?.IncExecutionReport(ExecutionReportKind.Reject);
        }
    }
}
