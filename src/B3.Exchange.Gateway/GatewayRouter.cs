using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using ContractsSessionId = B3.Exchange.Contracts.SessionId;
using RejectEvent = B3.Exchange.Matching.RejectEvent;
using Side = B3.Exchange.Matching.Side;

namespace B3.Exchange.Gateway;

/// <summary>
/// Gateway-side implementation of <see cref="ICoreOutbound"/>: receives
/// per-order ExecutionReport callbacks from <c>ChannelDispatcher</c>
/// stamped with a <see cref="SessionId"/>, resolves that to the live
/// <see cref="FixpSession"/> via the <see cref="SessionRegistry"/>, and
/// invokes the session's encoders.
///
/// <para>If the session is no longer registered (peer disconnected
/// between the inbound command and the engine emitting the event) the
/// report is dropped silently. Phase 3 (Suspended sessions) will route
/// these into a retransmission ring instead.</para>
///
/// <para>Thread-safety: invoked from any
/// <see cref="ChannelDispatcher"/> dispatch thread; the registry lookup
/// is concurrent-safe and the per-session encoders + transport send
/// queues are themselves thread-safe.</para>
/// </summary>
public sealed class GatewayRouter : ICoreOutbound
{
    private readonly SessionRegistry _registry;
    private readonly ILogger<GatewayRouter> _logger;

    public GatewayRouter(SessionRegistry registry, ILogger<GatewayRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    public bool WriteExecutionReportNew(ContractsSessionId session, ulong clOrdIdValue, in OrderAcceptedEvent e,
        ulong receivedTimeNanos = ulong.MaxValue)
    {
        if (!_registry.TryGet(session, out var s)) { LogMiss(session, "ExecReportNew"); return false; }
        return s.WriteExecutionReportNew(e, receivedTimeNanos);
    }

    public bool WriteExecutionReportTrade(ContractsSessionId session, in TradeEvent e, bool isAggressor,
        long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty)
    {
        if (!_registry.TryGet(session, out var s)) { LogMiss(session, "ExecReportTrade"); return false; }
        return s.WriteExecutionReportTrade(e, isAggressor, ownerOrderId, clOrdIdValue, leavesQty, cumQty);
    }

    public bool WriteExecutionReportCancel(ContractsSessionId session, in OrderCanceledEvent e,
        ulong clOrdIdValue, ulong origClOrdIdValue,
        ulong receivedTimeNanos = ulong.MaxValue)
    {
        if (!_registry.TryGet(session, out var s)) { LogMiss(session, "ExecReportCancel"); return false; }
        return s.WriteExecutionReportCancel(e, clOrdIdValue, origClOrdIdValue, receivedTimeNanos);
    }

    public bool WriteExecutionReportModify(ContractsSessionId session, long securityId, long orderId,
        ulong clOrdIdValue, ulong origClOrdIdValue,
        Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq,
        ulong receivedTimeNanos = ulong.MaxValue)
    {
        if (!_registry.TryGet(session, out var s)) { LogMiss(session, "ExecReportModify"); return false; }
        return s.WriteExecutionReportModify(securityId, orderId, clOrdIdValue, origClOrdIdValue,
            side, newPriceMantissa, newRemainingQty, transactTimeNanos, rptSeq, receivedTimeNanos);
    }

    public bool WriteExecutionReportReject(ContractsSessionId session, in RejectEvent e, ulong clOrdIdValue)
    {
        if (!_registry.TryGet(session, out var s)) { LogMiss(session, "ExecReportReject"); return false; }
        return s.WriteExecutionReportReject(e, clOrdIdValue);
    }

    private void LogMiss(ContractsSessionId session, string kind)
    {
        // Common at session-close races; keep at trace so /metrics doesn't
        // get spammed in soak runs.
        _logger.LogTrace("dropping {Kind} for unknown session {Session}", kind, session);
    }
}
