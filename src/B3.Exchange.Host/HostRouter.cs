using B3.Exchange.EntryPoint;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Host;

/// <summary>
/// <see cref="IInboundCommandSink"/> that fans inbound commands from one
/// TCP session out to the right <see cref="ChannelDispatcher"/> based on the
/// command's <c>SecurityId</c>. A single session may submit orders for any
/// instrument across any channel; the host owns the SecurityId → Channel
/// routing table built at startup from per-channel instrument files.
///
/// On unknown SecurityId the router synthesizes a reject ER directly to the
/// session (no engine is involved).
/// </summary>
public sealed class HostRouter : IInboundCommandSink
{
    private readonly IReadOnlyDictionary<long, ChannelDispatcher> _bySecId;
    private readonly IReadOnlyList<ChannelDispatcher> _allDispatchers;
    private readonly ILogger<HostRouter> _logger;
    private readonly Func<ulong> _nowNanos;

    public HostRouter(IReadOnlyDictionary<long, ChannelDispatcher> routing, ILogger<HostRouter> logger,
        Func<ulong>? nowNanos = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _bySecId = routing;
        // De-duplicate by reference: the routing map keys by SecurityId but
        // many securities share one dispatcher.
        _allDispatchers = routing.Values.Distinct().ToList();
        _logger = logger;
        _nowNanos = nowNanos ?? (() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    }

    public void EnqueueNewOrder(in NewOrderCommand cmd, IGatewayResponseChannel reply, ulong clOrdIdValue)
    {
        if (_bySecId.TryGetValue(cmd.SecurityId, out var disp))
            disp.EnqueueNewOrder(cmd, reply, clOrdIdValue);
        else
            RejectUnknownInstrument(cmd.SecurityId, reply, clOrdIdValue);
    }

    public void EnqueueCancel(in CancelOrderCommand cmd, IGatewayResponseChannel reply, ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        if (_bySecId.TryGetValue(cmd.SecurityId, out var disp))
            disp.EnqueueCancel(cmd, reply, clOrdIdValue, origClOrdIdValue);
        else
            RejectUnknownInstrument(cmd.SecurityId, reply, clOrdIdValue);
    }

    public void EnqueueReplace(in ReplaceOrderCommand cmd, IGatewayResponseChannel reply, ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        if (_bySecId.TryGetValue(cmd.SecurityId, out var disp))
            disp.EnqueueReplace(cmd, reply, clOrdIdValue, origClOrdIdValue);
        else
            RejectUnknownInstrument(cmd.SecurityId, reply, clOrdIdValue);
    }

    public void OnDecodeError(IGatewayResponseChannel reply, string error)
    {
        // Logging hook only. The EntryPointSession itself emits the
        // appropriate SessionReject (Terminate) or BusinessMessageReject
        // and decides whether to close the connection — the router has no
        // additional context to add here.
        _logger.LogWarning("inbound decode error from connection {ConnectionId}: {Error}", reply.ConnectionId, error);
    }

    public void OnSessionClosed(IGatewayResponseChannel reply)
    {
        // A single session may have placed orders on any channel, so fan the
        // notification out to ALL dispatchers. Each one enqueues a release
        // command on its own dispatch thread (see
        // ChannelDispatcher.OnSessionClosed) so the engine's
        // single-threaded contract is preserved.
        foreach (var disp in _allDispatchers)
            disp.OnSessionClosed(reply);
    }

    private void RejectUnknownInstrument(long secId, IGatewayResponseChannel reply, ulong clOrdIdValue)
    {
        _logger.LogWarning("rejecting clOrdId={ClOrdId} from connection {ConnectionId}: unknown securityId={SecurityId}",
            clOrdIdValue, reply.ConnectionId, secId);
        reply.WriteExecutionReportReject(
            new RejectEvent(ClOrdId: clOrdIdValue.ToString(), SecurityId: secId, OrderIdOrZero: 0,
                Reason: RejectReason.UnknownInstrument, TransactTimeNanos: _nowNanos()),
            clOrdIdValue);
    }
}
