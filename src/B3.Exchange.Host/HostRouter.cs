using B3.Exchange.EntryPoint;
using B3.Exchange.Integration;
using B3.Exchange.Matching;

namespace B3.Exchange.Host;

/// <summary>
/// <see cref="IEntryPointEngineSink"/> that fans inbound commands from one
/// TCP session out to the right <see cref="ChannelDispatcher"/> based on the
/// command's <c>SecurityId</c>. A single session may submit orders for any
/// instrument across any channel; the host owns the SecurityId → Channel
/// routing table built at startup from per-channel instrument files.
///
/// On unknown SecurityId the router synthesizes a reject ER directly to the
/// session (no engine is involved).
/// </summary>
public sealed class HostRouter : IEntryPointEngineSink
{
    private readonly IReadOnlyDictionary<long, ChannelDispatcher> _bySecId;
    private readonly Func<ulong> _nowNanos;

    public HostRouter(IReadOnlyDictionary<long, ChannelDispatcher> routing, Func<ulong>? nowNanos = null)
    {
        _bySecId = routing;
        _nowNanos = nowNanos ?? (() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    }

    public void EnqueueNewOrder(in NewOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue)
    {
        if (_bySecId.TryGetValue(cmd.SecurityId, out var disp))
            disp.EnqueueNewOrder(cmd, reply, clOrdIdValue);
        else
            RejectUnknownInstrument(cmd.SecurityId, reply, clOrdIdValue);
    }

    public void EnqueueCancel(in CancelOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue)
    {
        if (_bySecId.TryGetValue(cmd.SecurityId, out var disp))
            disp.EnqueueCancel(cmd, reply, clOrdIdValue);
        else
            RejectUnknownInstrument(cmd.SecurityId, reply, clOrdIdValue);
    }

    public void EnqueueReplace(in ReplaceOrderCommand cmd, IEntryPointResponseChannel reply, ulong clOrdIdValue, ulong origClOrdIdValue)
    {
        if (_bySecId.TryGetValue(cmd.SecurityId, out var disp))
            disp.EnqueueReplace(cmd, reply, clOrdIdValue, origClOrdIdValue);
        else
            RejectUnknownInstrument(cmd.SecurityId, reply, clOrdIdValue);
    }

    public void OnDecodeError(IEntryPointResponseChannel reply, string error)
    {
        // Best-effort: send a generic reject and rely on the session to close
        // its own connection if the decode error was fatal.
        reply.WriteExecutionReportReject(
            new RejectEvent(ClOrdId: "0", SecurityId: 0, OrderIdOrZero: 0,
                Reason: RejectReason.UnknownInstrument, TransactTimeNanos: _nowNanos()),
            clOrdIdValue: 0);
    }

    private void RejectUnknownInstrument(long secId, IEntryPointResponseChannel reply, ulong clOrdIdValue)
    {
        reply.WriteExecutionReportReject(
            new RejectEvent(ClOrdId: clOrdIdValue.ToString(), SecurityId: secId, OrderIdOrZero: 0,
                Reason: RejectReason.UnknownInstrument, TransactTimeNanos: _nowNanos()),
            clOrdIdValue);
    }
}
