using B3.Exchange.Contracts;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Core;

/// <summary>
/// Inbox facet of <see cref="ChannelDispatcher"/> (issue #168 split):
/// the producer-side <c>IInboundCommandSink</c> implementation. Every
/// method is safe to call from any thread; each posts a
/// <see cref="WorkItem"/> to the bounded inbound channel and increments
/// the appropriate metrics on success/queue-full. Also owns the
/// <c>dispatch.enqueue</c> tracing span (issue #175).
/// </summary>
public sealed partial class ChannelDispatcher
{
    /// <summary>
    /// Issue #175: starts the <c>dispatch.enqueue</c> span. Returns
    /// <c>null</c> when no listener is subscribed (zero-overhead path so
    /// uninstrumented hosts pay nothing). Tags carry enough context for
    /// trace consumers to correlate with metrics &amp; logs without joining
    /// against another store.
    /// </summary>
    private System.Diagnostics.Activity? StartEnqueueSpan(
        WorkKind kind, SessionId session, uint enteringFirm, ulong clOrdId, long securityId)
    {
        var act = ExchangeTelemetry.Source.StartActivity(
            ExchangeTelemetry.SpanDispatchEnqueue,
            System.Diagnostics.ActivityKind.Producer);
        if (act is null) return null;
        act.SetTag(ExchangeTelemetry.TagChannel, (int)ChannelNumber);
        act.SetTag(ExchangeTelemetry.TagSession, session.Value);
        act.SetTag(ExchangeTelemetry.TagFirm, (long)enteringFirm);
        act.SetTag(ExchangeTelemetry.TagWorkKind, kind.ToString());
        if (clOrdId != 0) act.SetTag(ExchangeTelemetry.TagClOrdId, (long)clOrdId);
        if (securityId != 0) act.SetTag(ExchangeTelemetry.TagSecurityId, securityId);
        return act;
    }

    public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue)
    {
        using var act = StartEnqueueSpan(WorkKind.New, session, enteringFirm, clOrdIdValue, cmd.SecurityId);
        var parent = act?.Context ?? System.Diagnostics.Activity.Current?.Context ?? default;
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.New, session, enteringFirm, true,
            clOrdIdValue, 0, cmd, null, null, null,
            EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp(), ParentContext: parent)))
        { _metrics?.IncInboundMessage(InboundMessageKind.New); _sessionFirmCounters?.Inc(enteringFirm, session.Value); return true; }
        _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.New);
        act?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "queue_full");
        return false;
    }

    public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        using var act = StartEnqueueSpan(WorkKind.Cancel, session, enteringFirm, clOrdIdValue, cmd.SecurityId);
        var parent = act?.Context ?? System.Diagnostics.Activity.Current?.Context ?? default;
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.Cancel, session, enteringFirm, true,
            clOrdIdValue, origClOrdIdValue, null, cmd, null, null,
            EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp(), ParentContext: parent)))
        { _metrics?.IncInboundMessage(InboundMessageKind.Cancel); _sessionFirmCounters?.Inc(enteringFirm, session.Value); return true; }
        _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.Cancel);
        act?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "queue_full");
        return false;
    }

    public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        using var act = StartEnqueueSpan(WorkKind.Replace, session, enteringFirm, clOrdIdValue, cmd.SecurityId);
        var parent = act?.Context ?? System.Diagnostics.Activity.Current?.Context ?? default;
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.Replace, session, enteringFirm, true,
            clOrdIdValue, origClOrdIdValue, null, null, cmd, null,
            EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp(), ParentContext: parent)))
        { _metrics?.IncInboundMessage(InboundMessageKind.Replace); _sessionFirmCounters?.Inc(enteringFirm, session.Value); return true; }
        _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.Replace);
        act?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "queue_full");
        return false;
    }

    public bool EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm)
    {
        using var act = StartEnqueueSpan(WorkKind.Cross, session, enteringFirm, cmd.BuyClOrdIdValue, cmd.Buy.SecurityId);
        var parent = act?.Context ?? System.Diagnostics.Activity.Current?.Context ?? default;
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.Cross, session, enteringFirm, true,
            cmd.BuyClOrdIdValue, cmd.SellClOrdIdValue, null, null, null, cmd,
            EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp(), ParentContext: parent)))
        { _metrics?.IncInboundMessage(InboundMessageKind.Cross); _sessionFirmCounters?.Inc(enteringFirm, session.Value); return true; }
        _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.Cross);
        act?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "queue_full");
        return false;
    }

    public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm)
    {
        // OrderId resolution + filtering is the HostRouter's job (it owns
        // access to the Gateway-side OrderOwnershipMap). Forwarding the
        // pre-filter form here would require Core to track ownership again,
        // which is exactly what #66 set out to remove. Surface the misuse
        // instead of silently dropping.
        throw new InvalidOperationException(
            "Mass-cancel must be pre-resolved by the gateway router; call EnqueueResolvedMassCancel(orderIds) instead.");
    }

    /// <summary>
    /// Channel-local mass-cancel entry point used by <c>HostRouter</c>
    /// after it has resolved the spec §4.8 filter
    /// (session/firm/Side/SecurityId) against the Gateway-side
    /// <c>OrderOwnershipMap</c> and grouped the resulting orderIds by
    /// channel. The dispatcher submits the list as a single
    /// <c>MatchingEngine.MassCancel</c> call so all DEL frames pack into
    /// one UMDF packet (atomic from the consumer's POV) and one
    /// <c>ER_Cancel</c> per order is routed back to the originating session
    /// via <c>OnOrderCanceled</c>.
    /// </summary>
    public bool EnqueueResolvedMassCancel(IReadOnlyList<long> orderIds, SessionId session, uint enteringFirm,
        ulong enteredAtNanos)
    {
        if (orderIds == null || orderIds.Count == 0) return true;
        var mc = new ResolvedMassCancel(orderIds, enteredAtNanos);
        using var act = StartEnqueueSpan(WorkKind.MassCancel, session, enteringFirm, 0, securityId: 0);
        var parent = act?.Context ?? System.Diagnostics.Activity.Current?.Context ?? default;
        if (_inbound.Writer.TryWrite(new WorkItem(WorkKind.MassCancel, session, enteringFirm, true,
            0, 0, null, null, null, null, mc,
            EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp(), ParentContext: parent)))
        { _metrics?.IncInboundMessage(InboundMessageKind.MassCancel); _sessionFirmCounters?.Inc(enteringFirm, session.Value); return true; }
        _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.MassCancel);
        act?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "queue_full");
        return false;
    }

    public void OnDecodeError(SessionId session, string error)
    {
        _logger.LogWarning("channel {ChannelNumber} inbound decode error: {Error}", ChannelNumber, error);
        _metrics?.IncDecodeErrors();
        _metrics?.IncInboundMessage(InboundMessageKind.DecodeError);
        if (!_inbound.Writer.TryWrite(new WorkItem(WorkKind.DecodeError, session, 0, true,
            0, 0, null, null, null, null, EnqueueTicks: System.Diagnostics.Stopwatch.GetTimestamp())))
        { _metrics?.IncDispatchQueueFull(); LogQueueFull(ChannelNumber, WorkKind.DecodeError); }
    }

    public void OnSessionClosed(SessionId session)
    {
        // Per-session order ownership lives in the Gateway-side
        // OrderOwnershipMap; the dispatcher holds no per-session state to
        // release. The Gateway listener invokes
        // OrderOwnershipMap.EvictSession(session) directly on transport
        // close, so this method is a no-op for the Core side.
    }
}
