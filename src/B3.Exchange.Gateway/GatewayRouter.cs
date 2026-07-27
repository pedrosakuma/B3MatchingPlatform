using B3.Exchange.Contracts;
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
/// <para>Issue #167: the canonical per-order owner state lives in Core's
/// per-channel <c>OrderRegistry</c>; <see cref="ChannelDispatcher"/>
/// resolves the owning session on the dispatch thread and passes the
/// pre-resolved <c>(SessionId, ClOrdId)</c> on every passive ER call. The
/// Gateway no longer holds an ownership map.</para>
///
/// <para>If the session is no longer registered (peer disconnected
/// between the inbound command and the engine emitting the event) the
/// report is dropped silently. While the session is merely
/// <c>Suspended</c> the session is still registered, so the report is
/// still encoded; the encoder appends to the FIXP retransmit ring even
/// though the (dead) transport rejects it, and the frame is replayed on
/// re-Establish (issue #217 / Onda L · L4).</para>
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

    public bool WriteExecutionReportNew(ContractsSessionId session, uint enteringFirm, ulong clOrdIdValue, in OrderAcceptedEvent e,
        ulong receivedTimeNanos = ulong.MaxValue,
        DurabilityHandle durability = default)
    {
        var report = e;
        return Route(session, "ExecReportNew", s =>
            s.WriteExecutionReportNew(report, receivedTimeNanos, durability, clOrdIdValue,
                report.Memo ?? ReadOnlyMemory<byte>.Empty));
    }

    public bool WriteExecutionReportTrade(ContractsSessionId session, in TradeEvent e, bool isAggressor,
        long ownerOrderId, ulong clOrdIdValue, long leavesQty, long cumQty,
        DurabilityHandle durability = default)
    {
        var report = e;
        return Route(session, "ExecReportTrade", s =>
            s.WriteExecutionReportTrade(report, isAggressor, ownerOrderId, clOrdIdValue,
                leavesQty, cumQty, durability,
                isAggressor
                    ? report.AggressorMemo ?? ReadOnlyMemory<byte>.Empty
                    : report.RestingMemo ?? ReadOnlyMemory<byte>.Empty));
    }

    public bool WriteExecutionReportPassiveTrade(ContractsSessionId ownerSession, ulong ownerClOrdId, long restingOrderId,
        in TradeEvent e, long leavesQty, long cumQty,
        DurabilityHandle durability = default)
    {
        var report = e;
        return Route(ownerSession, "ExecReportPassiveTrade", s =>
            s.WriteExecutionReportTrade(report, isAggressor: false, restingOrderId,
                ownerClOrdId, leavesQty, cumQty, durability,
                report.RestingMemo ?? ReadOnlyMemory<byte>.Empty));
    }

    public OrderedStreamWriteResult WriteExecutionReportPassiveCancel(ContractsSessionId ownerSession, ulong ownerClOrdId, long orderId,
        in OrderCanceledEvent e, ulong requesterClOrdIdOrZero, ulong receivedTimeNanos = ulong.MaxValue,
        DurabilityHandle durability = default)
    {
        var report = e;
        ulong clOrdIdOnWire = requesterClOrdIdOrZero != 0 ? requesterClOrdIdOrZero : ownerClOrdId;
        return RouteOrdered(ownerSession, "ExecReportPassiveCancel", s =>
            s.WriteExecutionReportCancel(report, clOrdIdOnWire, ownerClOrdId,
                receivedTimeNanos, durability, report.Memo ?? ReadOnlyMemory<byte>.Empty));
    }

    public bool WriteExecutionReportModify(ContractsSessionId session, long securityId, long orderId,
        ulong clOrdIdValue, ulong origClOrdIdValue,
        Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq,
        ulong receivedTimeNanos = ulong.MaxValue,
        DurabilityHandle durability = default,
        Matching.InvestorId? investorId = null)
        => Route(session, "ExecReportModify", s =>
            s.WriteExecutionReportModify(securityId, orderId, clOrdIdValue,
                origClOrdIdValue, side, newPriceMantissa, newRemainingQty,
                transactTimeNanos, rptSeq, receivedTimeNanos, durability,
                memo: default, investorId: investorId));

    public bool WriteExecutionReportModify(ContractsSessionId session, long securityId, long orderId,
        ulong clOrdIdValue, ulong origClOrdIdValue,
        Side side, long newPriceMantissa, long newRemainingQty, ulong transactTimeNanos, uint rptSeq,
        OrderType ordType, long? protectionPriceMantissa,
        ulong receivedTimeNanos = ulong.MaxValue,
        DurabilityHandle durability = default,
        Matching.InvestorId? investorId = null)
        => Route(session, "ExecReportModify", s =>
            s.WriteExecutionReportModify(securityId, orderId, clOrdIdValue,
                origClOrdIdValue, side, newPriceMantissa, newRemainingQty,
                transactTimeNanos, rptSeq, receivedTimeNanos, durability,
                memo: default, investorId: investorId, ordType: ordType,
                protectionPriceMantissa: protectionPriceMantissa));

    public bool WriteExecutionReportReject(ContractsSessionId session, in RejectEvent e, ulong clOrdIdValue,
        DurabilityHandle durability = default)
    {
        var report = e;
        return Route(session, "ExecReportReject", s =>
            s.WriteExecutionReportReject(report, clOrdIdValue, durability,
                report.Memo ?? ReadOnlyMemory<byte>.Empty));
    }

    public bool WriteExecutionReportRestate(ContractsSessionId ownerSession, ulong ownerClOrdId,
        in OrderRestatedEvent e, DurabilityHandle durability = default)
    {
        var report = e;
        return Route(ownerSession, "ExecReportRestate", s =>
            s.WriteExecutionReportRestate(report, ownerClOrdId, durability,
                report.Memo ?? ReadOnlyMemory<byte>.Empty));
    }

    private bool Route(
        ContractsSessionId session,
        string kind,
        Func<FixpSession, bool> write)
    {
        bool found = false;
        bool result = _registry.TryInvoke(session, current =>
        {
            found = true;
            return write(current);
        });
        if (!found) LogMiss(session, kind);
        return result;
    }

    private OrderedStreamWriteResult RouteOrdered(
        ContractsSessionId session,
        string kind,
        Func<FixpSession, OrderedStreamWriteResult> write)
    {
        bool found = false;
        var result = _registry.TryInvokeOrdered(session, current =>
        {
            found = true;
            try
            {
                return write(current);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ordered gateway write {Kind} failed for session {Session}; reporting NotCommitted",
                    kind, session);
                return OrderedStreamWriteResult.NotCommitted;
            }
        });
        if (!found) LogMiss(session, kind);
        return result;
    }

    private void LogMiss(ContractsSessionId session, string kind)
    {
        // Common at session-close races; keep at trace so /metrics doesn't
        // get spammed in soak runs.
        _logger.LogTrace("dropping {Kind} for unknown session {Session}", kind, session);
    }
}
