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
    /// <summary>
    /// Per-order canonical state. <see cref="OriginalQty"/> is the order
    /// quantity as last accepted/replaced by the engine (FIX OrderQty);
    /// <see cref="CumQty"/> is the running sum of executed quantity for
    /// this order (issue #319). Both default to 0 in legacy callers
    /// (e.g. tests that don't exercise multi-fill). The dispatcher
    /// updates <see cref="CumQty"/> on every passive-side
    /// <c>OnTrade</c> via <see cref="OnTrade"/> so subsequent
    /// <c>ER_PassiveTrade</c> frames carry monotonically-cumulative
    /// values per the FIX/B3 wire contract.
    /// </summary>
    public readonly record struct OrderState(
        SessionId Session, ulong ClOrdId, uint Firm, Side Side, long SecurityId,
        long OriginalQty = 0, long CumQty = 0);

    private readonly ConcurrentDictionary<long, OrderState> _byOrderId = new();
    private readonly ConcurrentDictionary<(uint Firm, ulong ClOrdId), long> _byClOrdId = new();

    public int Count => _byOrderId.Count;

    /// <summary>
    /// Enumerates every (OrderId, OrderState) tuple currently registered.
    /// Snapshot semantics: the underlying
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/> enumerator is
    /// weakly consistent, which is acceptable here because the only
    /// caller (<see cref="ChannelDispatcher.CaptureChannelState"/>) runs
    /// on the dispatch thread — the single writer — so concurrent
    /// mutation is impossible during enumeration. Issue #260.
    /// </summary>
    public IEnumerable<(long OrderId, OrderState State)> EnumerateAll()
    {
        foreach (var kv in _byOrderId)
            yield return (kv.Key, kv.Value);
    }

    public void Register(long orderId, SessionId session, ulong clOrdId, uint firm, Side side, long securityId,
        long originalQty = 0, long cumQty = 0)
    {
        _byOrderId[orderId] = new OrderState(session, clOrdId, firm, side, securityId, originalQty, cumQty);
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
    /// Issue #319: accumulates a trade fill against <paramref name="orderId"/>
    /// and returns the post-fill <c>(cumQty, leavesQty)</c> tuple. Callers
    /// (the dispatcher's <c>OnTrade</c> sink) use the result to populate
    /// the FIX/B3 wire fields on <c>ER_PassiveTrade</c> /
    /// <c>ER_Trade (aggressor)</c> with monotonically-cumulative values.
    ///
    /// <para>Single-writer: must run on the owning dispatcher's dispatch
    /// thread (mirrors <see cref="Register"/>). Returns <c>false</c> when
    /// the entry is unknown (callers fall back to legacy non-cumulative
    /// behaviour). When <see cref="OrderState.OriginalQty"/> is 0
    /// (uninitialised — pre-#319 callers / restored snapshots whose origin
    /// the dispatcher could not reconstruct) we treat
    /// <c>originalQty</c> as <c>cumQty + lastQty</c> so leaves still
    /// degrades gracefully to 0 on the final fill.</para>
    /// </summary>
    public bool OnTrade(long orderId, long lastQty, out long cumQty, out long leavesQty)
    {
        if (!_byOrderId.TryGetValue(orderId, out var owner))
        {
            cumQty = 0;
            leavesQty = 0;
            return false;
        }
        long newCum = owner.CumQty + lastQty;
        long origQty = owner.OriginalQty > 0 ? owner.OriginalQty : newCum;
        long leaves = origQty - newCum;
        if (leaves < 0) leaves = 0;
        _byOrderId[orderId] = owner with { CumQty = newCum };
        cumQty = newCum;
        leavesQty = leaves;
        return true;
    }

    /// <summary>
    /// Issue #319: updates the canonical <see cref="OrderState.OriginalQty"/>
    /// for <paramref name="orderId"/> after a successful Replace ack. The
    /// new wire <c>OrderQty</c> is the prior cumQty plus the engine's
    /// post-replace remainingQty; preserving cum keeps subsequent ER
    /// frames cumulative across the rotation. Returns <c>false</c> when
    /// the entry no longer exists.
    /// </summary>
    public bool UpdateOriginalQty(long orderId, long newOriginalQty)
    {
        if (!_byOrderId.TryGetValue(orderId, out var owner)) return false;
        if (owner.OriginalQty == newOriginalQty) return true;
        _byOrderId[orderId] = owner with { OriginalQty = newOriginalQty };
        return true;
    }

    /// <summary>
    /// Removes the entry for <paramref name="orderId"/> and the matching
    /// reverse <c>(Firm, ClOrdId)</c> index entry (if any).
    /// </summary>
    public bool Evict(long orderId)
        => TryEvict(orderId, out _);

    /// <summary>
    /// Single-pass evict: removes the entry for <paramref name="orderId"/>
    /// (and its reverse index entry) and returns the evicted owner via
    /// <paramref name="owner"/>. Use this in dispatcher sinks where the
    /// outbound ER routing needs the owner's session/ClOrdId — saves the
    /// dictionary lookup that <see cref="TryResolve"/> + <see cref="Evict"/>
    /// would do twice.
    /// </summary>
    public bool TryEvict(long orderId, out OrderState owner)
    {
        if (!_byOrderId.TryRemove(orderId, out owner))
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
    /// Returns every <c>OrderId</c> currently registered for
    /// <paramref name="securityId"/> on this channel, regardless of
    /// owning session/firm. Used by the end-of-day option-expiry sweep
    /// (OPT-03, ADR 0013) to enumerate the full set of orders to
    /// cancel before transitioning the security to <c>Close</c>. Must
    /// be called from the dispatch thread (the only writer to the
    /// registry under ADR 0009 single-writer semantics).
    /// </summary>
    public IReadOnlyList<long> SnapshotForSecurity(long securityId)
    {
        List<long>? matches = null;
        foreach (var kv in _byOrderId)
        {
            if (kv.Value.SecurityId != securityId) continue;
            (matches ??= new List<long>()).Add(kv.Key);
        }
        return (IReadOnlyList<long>?)matches ?? Array.Empty<long>();
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
