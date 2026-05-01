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
/// On unknown SecurityId the router synthesizes a reject ER directly to the
/// session (via the <see cref="GatewayRouter"/>) — no engine is involved.
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
        // De-duplicate by reference: the routing map keys by SecurityId but
        // many securities share one dispatcher.
        _allDispatchers = routing.Values.Distinct().ToList();
        _outbound = outbound;
        _logger = logger;
        _nowNanos = nowNanos ?? (() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    }

    public void EnqueueNewOrder(in NewOrderCommand cmd, SessionId session, uint enteringFirm, ulong clOrdIdValue)
    {
        if (_bySecId.TryGetValue(cmd.SecurityId, out var disp))
            disp.EnqueueNewOrder(cmd, session, enteringFirm, clOrdIdValue);
        else
            RejectUnknownInstrument(cmd.SecurityId, session, clOrdIdValue);
    }

    public void EnqueueCancel(in CancelOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        if (_bySecId.TryGetValue(cmd.SecurityId, out var disp))
            disp.EnqueueCancel(cmd, session, enteringFirm, clOrdIdValue, origClOrdIdValue);
        else
            RejectUnknownInstrument(cmd.SecurityId, session, clOrdIdValue);
    }

    public void EnqueueReplace(in ReplaceOrderCommand cmd, SessionId session, uint enteringFirm,
        ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        if (_bySecId.TryGetValue(cmd.SecurityId, out var disp))
            disp.EnqueueReplace(cmd, session, enteringFirm, clOrdIdValue, origClOrdIdValue);
        else
            RejectUnknownInstrument(cmd.SecurityId, session, clOrdIdValue);
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
        // A single session may have placed orders on any channel, so fan the
        // notification out to ALL dispatchers. Each one enqueues a release
        // command on its own dispatch thread (see
        // ChannelDispatcher.OnSessionClosed) so the engine's
        // single-threaded contract is preserved.
        foreach (var disp in _allDispatchers)
            disp.OnSessionClosed(session);
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
}
