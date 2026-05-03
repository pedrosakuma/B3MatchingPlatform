using B3.Exchange.Contracts;
using B3.Exchange.Gateway;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;
using RejectEvent = B3.Exchange.Matching.RejectEvent;

namespace B3.Exchange.Host;

/// <summary>
/// <see cref="IInboundCommandSink"/> that fans inbound commands from one
/// TCP session out to the right <see cref="ChannelDispatcher"/> based on the
/// command's <c>SecurityId</c>. A single session may submit orders for any
/// instrument across any channel; the host owns the SecurityId → Channel
/// routing table built at startup from per-channel instrument files.
///
/// <para>This is also the integration point for the Gateway-side
/// <see cref="OrderOwnershipMap"/>: Cancel/Replace by <c>OrigClOrdID</c>
/// and the mass-cancel filter (spec §4.8 / #GAP-19) are resolved here
/// against the map BEFORE the resolved command is enqueued onto a
/// dispatcher's inbound queue. The dispatchers themselves no longer hold
/// any per-session/per-order state (#66).</para>
///
/// On unknown SecurityId or unresolvable OrigClOrdID the router synthesizes
/// a reject ER directly back to the session via the
/// <see cref="GatewayRouter"/> — no engine is involved.
/// </summary>
public sealed class HostRouter : IInboundCommandSink
{
    private readonly IReadOnlyDictionary<long, ChannelDispatcher> _bySecId;
    private readonly ICoreOutbound _outbound;
    private readonly OrderOwnershipMap _ownership;
    private readonly ILogger<HostRouter> _logger;
    private readonly Func<ulong> _nowNanos;

    public HostRouter(IReadOnlyDictionary<long, ChannelDispatcher> routing, ICoreOutbound outbound,
        OrderOwnershipMap ownership,
        ILogger<HostRouter> logger,
        Func<ulong>? nowNanos = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(ownership);
        _bySecId = routing;
        _outbound = outbound;
        _ownership = ownership;
        _logger = logger;
        _nowNanos = nowNanos ?? (() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    }

    public bool EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue)
    {
        if (_bySecId.TryGetValue(cmd.SecurityId, out var disp))
            return disp.EnqueueNewOrder(cmd, session, enteringFirm, clOrdIdValue);
        // Unknown-instrument is a *deterministic business reject* the router
        // emits inline; from the caller's perspective the work is done, so
        // return true (the false return is reserved for backpressure only).
        RejectUnknownInstrument(cmd.SecurityId, session, clOrdIdValue);
        return true;
    }

    public bool EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        var resolved = ResolveOrderIdIfNeeded(cmd, enteringFirm, origClOrdIdValue, out var ok);
        if (!ok)
        {
            RejectUnknownOrderId(cmd.SecurityId, session, clOrdIdValue);
            return true;
        }
        if (_bySecId.TryGetValue(resolved.SecurityId, out var disp))
            return disp.EnqueueCancel(resolved, session, enteringFirm, clOrdIdValue, origClOrdIdValue);
        RejectUnknownInstrument(resolved.SecurityId, session, clOrdIdValue);
        return true;
    }

    public bool EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        var resolved = ResolveOrderIdIfNeeded(cmd, enteringFirm, origClOrdIdValue, out var ok);
        if (!ok)
        {
            RejectUnknownOrderId(cmd.SecurityId, session, clOrdIdValue);
            return true;
        }
        if (_bySecId.TryGetValue(resolved.SecurityId, out var disp))
            return disp.EnqueueReplace(resolved, session, enteringFirm, clOrdIdValue, origClOrdIdValue);
        RejectUnknownInstrument(resolved.SecurityId, session, clOrdIdValue);
        return true;
    }

    public bool EnqueueCross(in CrossOrderCommand cmd, SessionId session, uint enteringFirm)
    {
        // Both legs MUST belong to the same security (decoder enforces).
        if (_bySecId.TryGetValue(cmd.Buy.SecurityId, out var disp))
            return disp.EnqueueCross(cmd, session, enteringFirm);
        RejectUnknownInstrument(cmd.Buy.SecurityId, session, cmd.BuyClOrdIdValue);
        return true;
    }

    public bool EnqueueMassCancel(in MassCancelCommand cmd, SessionId session, uint enteringFirm)
    {
        // Spec §4.8 / #GAP-19. Resolve the (session, firm, Side?, SecurityId?)
        // filter against the Gateway-side OrderOwnershipMap, then group the
        // matching orderIds by channel before fanning out. The dispatchers
        // see only the resolved per-channel orderId list — no filter logic
        // and no per-order session state lives in Core anymore (#66).
        var matches = _ownership.FilterMassCancel(session, enteringFirm, cmd.SideFilter, cmd.SecurityId);
        if (matches.Count == 0) return true;

        Dictionary<ChannelDispatcher, List<long>>? perChannel = null;
        foreach (var (orderId, securityId) in matches)
        {
            if (!_bySecId.TryGetValue(securityId, out var disp))
                continue; // ownership map references an instrument no longer routed; skip silently
            (perChannel ??= new Dictionary<ChannelDispatcher, List<long>>())
                .TryGetValue(disp, out var list);
            if (list == null)
            {
                list = new List<long>();
                perChannel[disp] = list;
            }
            list.Add(orderId);
        }
        if (perChannel == null) return true;

        // Fan-out: report backpressure if ANY targeted channel rejected the
        // resolved batch. Partial accept is preserved (whatever made it
        // onto a queue stays there) — the gateway treats this as a single
        // reject from the client's POV (OrderMassActionReport REJECTED).
        bool allOk = true;
        foreach (var (disp, ids) in perChannel)
        {
            if (!disp.EnqueueResolvedMassCancel(ids, session, enteringFirm, cmd.EnteredAtNanos))
                allOk = false;
        }
        return allOk;
    }

    public void OnDecodeError(SessionId session, string error)
    {
        // Logging hook only. The FixpSession itself emits the
        // appropriate SessionReject (Terminate) or BusinessMessageReject
        // and decides whether to close the connection — the router has no
        // additional context to add here.
        _logger.LogWarning("inbound decode error from session {Session}: {Error}", session, error);
    }

    public void OnSessionClosed(SessionId session)
    {
        // Drop every ownership entry held for this session so passive
        // fills against its resting orders stop trying to route to a
        // disconnected peer. The orders themselves stay on the book —
        // they ARE the book — but ER_Trade / ER_Cancel for them will be
        // dropped in GatewayRouter (Phase 3 / #69 will replace this with
        // routing into a Suspended-session retx ring).
        int n = _ownership.EvictSession(session);
        if (n > 0)
            _logger.LogDebug("session {Session} closed; evicted {Count} ownership entries", session, n);
    }

    private CancelOrderCommand ResolveOrderIdIfNeeded(in CancelOrderCommand cmd, uint firm, ulong origClOrdId, out bool ok)
    {
        if (cmd.OrderId != 0) { ok = true; return cmd; }
        if (_ownership.TryResolveByClOrdId(firm, origClOrdId, out var orderId))
        {
            ok = true;
            return cmd with { OrderId = orderId };
        }
        ok = false;
        return cmd;
    }

    private ReplaceOrderCommand ResolveOrderIdIfNeeded(in ReplaceOrderCommand cmd, uint firm, ulong origClOrdId, out bool ok)
    {
        if (cmd.OrderId != 0) { ok = true; return cmd; }
        if (_ownership.TryResolveByClOrdId(firm, origClOrdId, out var orderId))
        {
            ok = true;
            return cmd with { OrderId = orderId };
        }
        ok = false;
        return cmd;
    }

    private void RejectUnknownInstrument(long secId, SessionId session, ulong clOrdIdValue)
    {
        _logger.LogWarning("rejecting clOrdId={ClOrdId} from session {Session}: unknown securityId={SecurityId}",
            clOrdIdValue, session, secId);
        _outbound.WriteExecutionReportReject(session,
            new RejectEvent(ClOrdId: clOrdIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SecurityId: secId, OrderIdOrZero: 0,
                Reason: RejectReason.UnknownInstrument, TransactTimeNanos: _nowNanos()),
            clOrdIdValue);
    }

    private void RejectUnknownOrderId(long secId, SessionId session, ulong clOrdIdValue)
    {
        _logger.LogWarning("rejecting clOrdId={ClOrdId} from session {Session}: unknown orderId / OrigClOrdID",
            clOrdIdValue, session);
        _outbound.WriteExecutionReportReject(session,
            new RejectEvent(ClOrdId: clOrdIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SecurityId: secId, OrderIdOrZero: 0,
                Reason: RejectReason.UnknownOrderId, TransactTimeNanos: _nowNanos()),
            clOrdIdValue);
    }
}
