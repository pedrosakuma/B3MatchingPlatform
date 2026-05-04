using B3.Exchange.Contracts;
using Side = B3.Exchange.Matching.Side;

namespace B3.Exchange.Core;

/// <summary>
/// OrderState facet of <see cref="ChannelDispatcher"/> (issue #167):
/// the cross-thread query surface that <c>HostRouter</c> uses to ask each
/// dispatcher about its own resting orders. All methods are safe to call
/// from inbound (Gateway recv-loop) threads; they delegate to the
/// per-channel <see cref="OrderRegistry"/>, which is single-writer
/// (dispatch thread) + multi-reader (cross-thread).
/// </summary>
public sealed partial class ChannelDispatcher
{
    /// <summary>
    /// Resolves <paramref name="origClOrdId"/> for <paramref name="firm"/>
    /// against this channel's registry. Returns <c>true</c> with the
    /// engine-assigned <paramref name="orderId"/> + the SecurityId of the
    /// resting order when an entry exists; <c>false</c> + zeroes otherwise.
    /// </summary>
    public bool TryResolveByClOrdId(uint firm, ulong origClOrdId, out long orderId, out long securityId)
    {
        if (_orders.TryResolveByClOrdId(firm, origClOrdId, out orderId)
            && _orders.TryResolve(orderId, out var owner))
        {
            securityId = owner.SecurityId;
            return true;
        }
        orderId = 0;
        securityId = 0;
        return false;
    }

    /// <summary>
    /// Returns every <c>(OrderId, SecurityId)</c> tuple in this channel
    /// owned by <paramref name="session"/> + <paramref name="firm"/> that
    /// matches the optional Side / SecurityId filters. See
    /// <see cref="OrderRegistry.FilterMassCancel"/>.
    /// </summary>
    public IReadOnlyList<(long OrderId, long SecurityId)> FilterMassCancelLocal(
        SessionId session, uint firm, Side? sideFilter, long securityIdFilter)
        => _orders.FilterMassCancel(session, firm, sideFilter, securityIdFilter);

    /// <summary>
    /// Drops every entry in this channel owned by <paramref name="session"/>.
    /// Returns the number of entries removed. Called from
    /// <c>HostRouter.OnSessionClosed</c> on the gateway recv-loop thread —
    /// the orders themselves stay on the book, but passive fills against
    /// them stop trying to route to a (now-defunct) session.
    /// </summary>
    public int EvictSessionLocal(SessionId session) => _orders.EvictSession(session);

    /// <summary>Total resting-order entries currently registered in this
    /// channel. Diagnostic only.</summary>
    public int OrderRegistryCount => _orders.Count;
}
