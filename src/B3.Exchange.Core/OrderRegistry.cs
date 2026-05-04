using System.Collections.Concurrent;
using B3.Exchange.Contracts;
using Side = B3.Exchange.Matching.Side;

namespace B3.Exchange.Core;

/// <summary>
/// Per-channel canonical order-state index keyed by engine-assigned
/// <c>OrderId</c>. Tracks which <see cref="SessionId"/> + (EnteringFirm,
/// ClOrdId) tuple owns each resting order and which side / instrument it
/// sits on. One instance per <see cref="ChannelDispatcher"/> (issue #167).
///
/// <para>Single writer: every mutation runs on the owning dispatcher's
/// dispatch thread (<see cref="ChannelDispatcher"/> calls
/// <see cref="Register"/> from <c>OnOrderAccepted</c> and
/// <see cref="Evict"/> from <c>OnOrderCanceled</c> / <c>OnOrderFilled</c>).
/// Multi-reader: <see cref="TryResolve"/>, <see cref="TryResolveByClOrdId"/>,
/// <see cref="FilterMassCancel"/>, and <see cref="EvictSession"/> are called
/// from inbound (Gateway recv-loop) threads via
/// <c>HostRouter</c>; backed by <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// for cross-thread reads.</para>
///
/// <para>Ordering invariant: every passive trade emitted by the engine
/// against a resting order is preceded — on the same dispatch thread — by
/// the resting order's <c>OrderAccepted</c> event, so <see cref="Register"/>
/// always runs before the first <see cref="TryResolve"/> for that
/// <c>OrderId</c>.</para>
/// </summary>
public sealed class OrderRegistry
{
    public readonly record struct OrderState(
        SessionId Session, ulong ClOrdId, uint Firm, Side Side, long SecurityId);

    private readonly ConcurrentDictionary<long, OrderState> _byOrderId = new();
    private readonly ConcurrentDictionary<(uint Firm, ulong ClOrdId), long> _byClOrdId = new();

    public int Count => _byOrderId.Count;

    public void Register(long orderId, SessionId session, ulong clOrdId, uint firm, Side side, long securityId)
    {
        _byOrderId[orderId] = new OrderState(session, clOrdId, firm, side, securityId);
        if (clOrdId != 0)
            _byClOrdId[(firm, clOrdId)] = orderId;
    }

    public bool TryResolve(long orderId, out OrderState owner)
        => _byOrderId.TryGetValue(orderId, out owner);

    public bool TryResolveByClOrdId(uint firm, ulong origClOrdId, out long orderId)
    {
        if (origClOrdId != 0 && _byClOrdId.TryGetValue((firm, origClOrdId), out orderId))
            return true;
        orderId = 0;
        return false;
    }

    /// <summary>
    /// Updates the canonical entry for <paramref name="orderId"/> so it
    /// owns <paramref name="newClOrdId"/> instead of its previous ClOrdId,
    /// and refreshes the reverse <c>(Firm, ClOrdId)</c> index accordingly.
    /// Used after a successful Replace ack so subsequent Cancel/Modify by
    /// <c>OrigClOrdID = newClOrdId</c> resolves to this order. Issue #251.
    /// Returns <c>false</c> when the entry no longer exists. Setting
    /// <paramref name="newClOrdId"/> to <c>0</c> is treated as "no
    /// change" and the call is a no-op.
    /// </summary>
    public bool Reregister(long orderId, ulong newClOrdId)
    {
        if (newClOrdId == 0) return false;
        if (!_byOrderId.TryGetValue(orderId, out var owner)) return false;
        if (owner.ClOrdId == newClOrdId) return true;
        var updated = owner with { ClOrdId = newClOrdId };
        _byOrderId[orderId] = updated;
        if (owner.ClOrdId != 0)
            _byClOrdId.TryRemove(new KeyValuePair<(uint, ulong), long>((owner.Firm, owner.ClOrdId), orderId));
        _byClOrdId[(owner.Firm, newClOrdId)] = orderId;
        return true;
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
    /// <paramref name="session"/> + <paramref name="firm"/> in this channel
    /// that matches the optional <paramref name="sideFilter"/> and the
    /// optional <paramref name="securityIdFilter"/> (zero ⇒ "any
    /// instrument"). Empty list ⇒ no matching orders. The caller (typically
    /// <c>HostRouter.EnqueueMassCancel</c>) is responsible for grouping the
    /// result by channel before forwarding to the relevant
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
            if (_byOrderId.TryRemove(new KeyValuePair<long, OrderState>(kv.Key, kv.Value)))
            {
                removed++;
                if (kv.Value.ClOrdId != 0)
                    _byClOrdId.TryRemove(new KeyValuePair<(uint, ulong), long>((kv.Value.Firm, kv.Value.ClOrdId), kv.Key));
            }
        }
        return removed;
    }

    /// <summary>
    /// Drops every entry. Called when the channel is reset (operator
    /// channel-reset / engine <c>ResetForChannelReset</c>) so stale
    /// ownership entries don't outlive the engine state.
    /// </summary>
    public int Clear()
    {
        int n = _byOrderId.Count;
        _byOrderId.Clear();
        _byClOrdId.Clear();
        return n;
    }
}
