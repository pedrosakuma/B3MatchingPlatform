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
    private readonly OrderOwnershipMap _ownership;
    private readonly ILogger<GatewayRouter> _logger;

    public GatewayRouter(SessionRegistry registry, OrderOwnershipMap ownership, ILogger<GatewayRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _ownership = ownership;
        _logger = logger;
    }

    public bool WriteExecutionReportNew(ContractsSessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e,
        ulong receivedTimeNanos = ulong.MaxValue)
    {
        // Register ownership before sending so a passive trade emitted in
        // the same dispatch turn (single-threaded by construction) finds the
        // entry. Eviction lives on the cancel/full-fill paths below.
        _ownership.Register(e.OrderId, session, clOrdIdValue, enteringFirm, e.Side, e.SecurityId);
        if (!_registry.TryGet(session, out var s)) { LogMiss(session, "ExecReportNew"); return false; }
        return s.WriteExecutionReportNew(e, receivedTimeNanos);
    }

    public bool WriteExecutionReportTrade(ContractsSessionId session, in TradeEvent e, bool isAggressor,
        long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty)
    {
        if (!_registry.TryGet(session, out var s)) { LogMiss(session, "ExecReportTrade"); return false; }
        return s.WriteExecutionReportTrade(e, isAggressor, ownerOrderId, clOrdIdValue, leavesQty, cumQty);
    }

    public bool WriteExecutionReportPassiveTrade(long restingOrderId, in TradeEvent e,
        long leavesQty, long cumQty)
    {
        if (!_ownership.TryResolve(restingOrderId, out var owner))
        {
            _logger.LogTrace("dropping passive ExecReportTrade for unknown orderId {OrderId}", restingOrderId);
            return false;
        }
        if (!_registry.TryGet(owner.Session, out var s)) { LogMiss(owner.Session, "ExecReportPassiveTrade"); return false; }
        return s.WriteExecutionReportTrade(e, isAggressor: false, restingOrderId, owner.ClOrdId, leavesQty, cumQty);
    }

    public bool WriteExecutionReportPassiveCancel(long orderId, in OrderCanceledEvent e,
        ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue)
    {
        if (!_ownership.TryResolve(orderId, out var owner))
        {
            _logger.LogTrace("dropping passive ExecReportCancel for unknown orderId {OrderId}", orderId);
            return false;
        }
        // Always evict — the order is gone from the book regardless of
        // whether the routing succeeds.
        _ownership.Evict(orderId);
        if (!_registry.TryGet(owner.Session, out var s)) { LogMiss(owner.Session, "ExecReportPassiveCancel"); return false; }
        ulong clOrdIdOnWire = requesterClOrdIdOrZero != 0 ? requesterClOrdIdOrZero : owner.ClOrdId;
        return s.WriteExecutionReportCancel(e, clOrdIdOnWire, owner.ClOrdId, receivedTimeNanos);
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

    public void NotifyOrderTerminal(long orderId) => _ownership.Evict(orderId);

    private void LogMiss(ContractsSessionId session, string kind)
    {
        // Common at session-close races; keep at trace so /metrics doesn't
        // get spammed in soak runs.
        _logger.LogTrace("dropping {Kind} for unknown session {Session}", kind, session);
    }
}
