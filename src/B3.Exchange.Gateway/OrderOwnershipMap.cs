using System.Collections.Concurrent;
using B3.Exchange.Contracts;
using Side = B3.Exchange.Matching.Side;

namespace B3.Exchange.Gateway;

/// <summary>
/// Per-process map keyed by engine-assigned <c>OrderId</c> that tracks
/// which <see cref="SessionId"/> + (EnteringFirm, ClOrdId) tuple owns each
/// resting order and which side / instrument it sits on.
///
/// <para>Lives in the Gateway because the Core (matching engine + channel
/// dispatchers) intentionally does not hold any session/transport state.
/// Populated by <see cref="GatewayRouter"/> on every
/// <see cref="ICoreOutbound.WriteExecutionReportNew"/> callback and evicted
/// on terminal events (<c>OnOrderCanceled</c>, full fill, session close).
/// Also serves as the resolver for <c>OrigClOrdID → OrderId</c> on
/// inbound Cancel/Replace and for the mass-cancel filter in
/// <see cref="HostRouter"/>.</para>
///
/// <para>Thread-safety: invoked from any <c>ChannelDispatcher</c> dispatch
/// thread (writes via <see cref="GatewayRouter"/>) and from any
/// <c>FixpSession</c> recv loop thread (reads + filter via
/// <c>HostRouter</c>). Backed by <see cref="ConcurrentDictionary{TKey,
/// TValue}"/> for both indices.</para>
///
/// <para>Ordering invariant: every passive trade emitted by the engine
/// against a resting order is preceded — on the same dispatch thread — by
/// the resting order's <c>OrderAccepted</c> event, so
/// <see cref="GatewayRouter"/>'s <c>Register</c> always runs before the
/// first <see cref="TryResolve"/> for that <c>OrderId</c>.</para>
/// </summary>
public sealed class OrderOwnershipMap
{
    public readonly record struct OrderOwnership(
        SessionId Session, ulong ClOrdId, uint Firm, Side Side, long SecurityId);

    private readonly ConcurrentDictionary<long, OrderOwnership> _byOrderId = new();
    private readonly ConcurrentDictionary<(uint Firm, ulong ClOrdId), long> _byClOrdId = new();

    public int Count => _byOrderId.Count;

    public void Register(long orderId, SessionId session, ulong clOrdId, uint firm, Side side, long securityId)
    {
        _byOrderId[orderId] = new OrderOwnership(session, clOrdId, firm, side, securityId);
        if (clOrdId != 0)
            _byClOrdId[(firm, clOrdId)] = orderId;
    }

    public bool TryResolve(long orderId, out OrderOwnership owner)
        => _byOrderId.TryGetValue(orderId, out owner);

    public bool TryResolveByClOrdId(uint firm, ulong origClOrdId, out long orderId)
    {
        if (origClOrdId != 0 && _byClOrdId.TryGetValue((firm, origClOrdId), out orderId))
            return true;
        orderId = 0;
        return false;
    }

    /// <summary>
    /// Removes the entry for <paramref name="orderId"/> and the matching
    /// reverse <c>(Firm, ClOrdId)</c> index entry (if any).
    /// </summary>
    public bool Evict(long orderId)
    {
        if (!_byOrderId.TryRemove(orderId, out var owner))
            return false;
        if (owner.ClOrdId != 0)
            _byClOrdId.TryRemove(new KeyValuePair<(uint, ulong), long>((owner.Firm, owner.ClOrdId), orderId));
        return true;
    }

    /// <summary>
    /// Returns every <c>(OrderId, SecurityId)</c> tuple owned by
    /// <paramref name="session"/> + <paramref name="firm"/> that matches
    /// the optional <paramref name="sideFilter"/> and the optional
    /// <paramref name="securityIdFilter"/> (zero ⇒ "any instrument").
    /// Empty list ⇒ no matching orders. The caller is responsible for
    /// grouping the result by channel before forwarding to the relevant
    /// <c>ChannelDispatcher</c>(s).
    /// </summary>
    public IReadOnlyList<(long OrderId, long SecurityId)> FilterMassCancel(
        SessionId session, uint firm, Side? sideFilter, long securityIdFilter)
    {
        List<(long, long)>? matches = null;
        foreach (var kv in _byOrderId)
        {
            var owner = kv.Value;
            if (owner.Session != session) continue;
            if (owner.Firm != firm) continue;
            if (sideFilter.HasValue && owner.Side != sideFilter.Value) continue;
            if (securityIdFilter != 0 && owner.SecurityId != securityIdFilter) continue;
            (matches ??= new List<(long, long)>()).Add((kv.Key, owner.SecurityId));
        }
        return (IReadOnlyList<(long, long)>?)matches ?? Array.Empty<(long, long)>();
    }

    /// <summary>
    /// Drops every entry owned by <paramref name="session"/>. Called when
    /// a transport closes so the orders themselves stay on the book but
    /// passive fills against them no longer route to a (now-defunct)
    /// session. Returns the number of entries removed.
    /// </summary>
    public int EvictSession(SessionId session)
    {
        int removed = 0;
        foreach (var kv in _byOrderId)
        {
            if (kv.Value.Session != session) continue;
            if (_byOrderId.TryRemove(new KeyValuePair<long, OrderOwnership>(kv.Key, kv.Value)))
            {
                removed++;
                if (kv.Value.ClOrdId != 0)
                    _byClOrdId.TryRemove(new KeyValuePair<(uint, ulong), long>((kv.Value.Firm, kv.Value.ClOrdId), kv.Key));
            }
        }
        return removed;
    }
}
