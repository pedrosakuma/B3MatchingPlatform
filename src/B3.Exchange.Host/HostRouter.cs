using B3.Exchange.Contracts;
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
/// <para>Issue #167: per-channel <c>OrderRegistry</c> is the canonical
/// owner of per-order routing state. Cancel/Replace by <c>OrigClOrdID</c>
/// and the mass-cancel filter (spec §4.8 / #GAP-19) iterate the
/// dispatchers and resolve against each channel's local registry. Session
/// close fans an evict-session call out to every channel. The dispatchers
/// remain the single writer for their own registry on the dispatch
/// thread; this router only reads.</para>
///
/// On unknown SecurityId or unresolvable OrigClOrdID the router synthesizes
/// a reject ER directly back to the session via the
/// <see cref="ICoreOutbound"/> — no engine is involved.
/// </summary>
public sealed class HostRouter : IInboundCommandSink
{
    private readonly IReadOnlyDictionary<long, ChannelDispatcher> _bySecId;
    private readonly IReadOnlyList<ChannelDispatcher> _allDispatchers;
    private readonly ICoreOutbound _outbound;
    private readonly ILogger<HostRouter> _logger;
    private readonly Func<ulong> _nowNanos;

    public HostRouter(IReadOnlyDictionary<long, ChannelDispatcher> routing, ICoreOutbound outbound,
        ILogger<HostRouter> logger,
        Func<ulong>? nowNanos = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(outbound);
        _bySecId = routing;
        // Distinct dispatcher set (multiple SecurityIds may share one channel).
        _allDispatchers = routing.Values.Distinct().ToArray();
        _outbound = outbound;
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
        // filter against each channel's local OrderRegistry (#167); group
        // resolved orderIds by channel and fan out. Dispatchers see only
        // the resolved per-channel orderId list — no per-order session
        // state lives outside the owning channel.
        Dictionary<ChannelDispatcher, List<long>>? perChannel = null;
        foreach (var disp in _allDispatchers)
        {
            if (cmd.SecurityId != 0 && _bySecId.TryGetValue(cmd.SecurityId, out var byId) && byId != disp)
                continue;
            var matches = disp.FilterMassCancelLocal(session, enteringFirm, cmd.SideFilter, cmd.SecurityId);
            if (matches.Count == 0) continue;
            var list = new List<long>(matches.Count);
            foreach (var (orderId, _) in matches) list.Add(orderId);
            (perChannel ??= new Dictionary<ChannelDispatcher, List<long>>())[disp] = list;
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
        // Drop every ownership entry held for this session across every
        // channel so passive fills against its resting orders stop trying
        // to route to a disconnected peer. The orders themselves stay on
        // the book — they ARE the book — but ER_Trade / ER_Cancel for them
        // will be dropped in the dispatcher's OnTrade/OnOrderCanceled when
        // the local registry no longer resolves them (Phase 3 / #69 will
        // replace this with routing into a Suspended-session retx ring).
        int total = 0;
        foreach (var disp in _allDispatchers)
            total += disp.EvictSessionLocal(session);
        if (total > 0)
            _logger.LogDebug("session {Session} closed; evicted {Count} ownership entries", session, total);
    }

    private CancelOrderCommand ResolveOrderIdIfNeeded(in CancelOrderCommand cmd, uint firm, ulong origClOrdId, out bool ok)
    {
        if (cmd.OrderId != 0) { ok = true; return cmd; }
        if (TryResolveAcrossChannels(firm, origClOrdId, out var orderId, out var securityId))
        {
            ok = true;
            // Stamp the resolved SecurityId so downstream dispatch finds the
            // owning channel even when the client didn't send one.
            return cmd with { OrderId = orderId, SecurityId = securityId };
        }
        ok = false;
        return cmd;
    }

    private ReplaceOrderCommand ResolveOrderIdIfNeeded(in ReplaceOrderCommand cmd, uint firm, ulong origClOrdId, out bool ok)
    {
        if (cmd.OrderId != 0) { ok = true; return cmd; }
        if (TryResolveAcrossChannels(firm, origClOrdId, out var orderId, out var securityId))
        {
            ok = true;
            return cmd with { OrderId = orderId, SecurityId = securityId };
        }
        ok = false;
        return cmd;
    }

    private bool TryResolveAcrossChannels(uint firm, ulong origClOrdId, out long orderId, out long securityId)
    {
        // ClOrdId is unique per (firm) but its owning channel is unknown
        // here; iterate the (typically small) dispatcher set. The first
        // hit wins — registries are partitioned by SecurityId so collisions
        // are not expected in practice.
        foreach (var disp in _allDispatchers)
        {
            if (disp.TryResolveByClOrdId(firm, origClOrdId, out orderId, out securityId))
                return true;
        }
        orderId = 0;
        securityId = 0;
        return false;
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
