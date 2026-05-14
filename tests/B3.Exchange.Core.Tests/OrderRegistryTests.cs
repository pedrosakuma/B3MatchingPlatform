using B3.Exchange.Contracts;
using B3.Exchange.Core;
using Side = B3.Exchange.Matching.Side;

namespace B3.Exchange.Core.Tests;

public class OrderRegistryTests
{
    private static SessionId S(string s) => new SessionId(s);

    [Fact]
    public void Register_Then_TryResolve_ReturnsOwner()
    {
        var map = new OrderRegistry();
        map.Register(orderId: 100, S("a"), clOrdId: 7, firm: 1, Side.Buy, securityId: 42);

        Assert.True(map.TryResolve(100, out var owner));
        Assert.Equal(S("a"), owner.Session);
        Assert.Equal(7UL, owner.ClOrdId);
        Assert.Equal(1u, owner.Firm);
        Assert.Equal(Side.Buy, owner.Side);
        Assert.Equal(42, owner.SecurityId);
    }

    [Fact]
    public void TryResolveByClOrdId_FindsOrderId()
    {
        var map = new OrderRegistry();
        map.Register(100, S("a"), 7, 1, Side.Buy, 42);

        Assert.True(map.TryResolveByClOrdId(1, 7, out var oid));
        Assert.Equal(100, oid);

        Assert.False(map.TryResolveByClOrdId(1, 999, out _));
        Assert.False(map.TryResolveByClOrdId(2, 7, out _)); // wrong firm
        Assert.False(map.TryResolveByClOrdId(1, 0, out _)); // zero is sentinel
    }

    [Fact]
    public void Evict_RemovesBothIndices()
    {
        var map = new OrderRegistry();
        map.Register(100, S("a"), 7, 1, Side.Buy, 42);

        Assert.True(map.Evict(100));
        Assert.False(map.TryResolve(100, out _));
        Assert.False(map.TryResolveByClOrdId(1, 7, out _));
        Assert.False(map.Evict(100)); // idempotent
    }

    [Fact]
    public void FilterMassCancel_ScopesBySessionFirmSideInstrument()
    {
        var map = new OrderRegistry();
        map.Register(1, S("a"), 1, 1, Side.Buy, 42);
        map.Register(2, S("a"), 2, 1, Side.Sell, 42);
        map.Register(3, S("a"), 3, 1, Side.Buy, 99);
        map.Register(4, S("b"), 4, 1, Side.Buy, 42); // other session
        map.Register(5, S("a"), 5, 2, Side.Buy, 42); // other firm

        // Session-A + firm 1, all sides, all instruments
        var all = map.FilterMassCancel(S("a"), 1, sideFilter: null, securityIdFilter: 0);
        Assert.Equal(new[] { 1L, 2L, 3L }, all.Select(x => x.OrderId).OrderBy(x => x));

        // With Side.Buy filter
        var buys = map.FilterMassCancel(S("a"), 1, Side.Buy, 0);
        Assert.Equal(new[] { 1L, 3L }, buys.Select(x => x.OrderId).OrderBy(x => x));

        // With instrument filter
        var inst42 = map.FilterMassCancel(S("a"), 1, null, 42);
        Assert.Equal(new[] { 1L, 2L }, inst42.Select(x => x.OrderId).OrderBy(x => x));

        // No matches → empty (not null)
        var none = map.FilterMassCancel(S("c"), 1, null, 0);
        Assert.Empty(none);
    }

    [Fact]
    public void EvictSession_DropsOnlyMatchingSessionEntries()
    {
        var map = new OrderRegistry();
        map.Register(1, S("a"), 1, 1, Side.Buy, 42);
        map.Register(2, S("a"), 2, 1, Side.Buy, 42);
        map.Register(3, S("b"), 3, 1, Side.Buy, 42);

        int removed = map.EvictSession(S("a"));
        Assert.Equal(2, removed);
        Assert.False(map.TryResolve(1, out _));
        Assert.False(map.TryResolve(2, out _));
        Assert.True(map.TryResolve(3, out _));
        Assert.False(map.TryResolveByClOrdId(1, 1, out _));
        Assert.True(map.TryResolveByClOrdId(1, 3, out _));
    }

    [Fact]
    public void Register_OverwritesSameOrderId()
    {
        var map = new OrderRegistry();
        map.Register(100, S("a"), 7, 1, Side.Buy, 42);
        map.Register(100, S("b"), 8, 2, Side.Sell, 99);

        Assert.True(map.TryResolve(100, out var owner));
        Assert.Equal(S("b"), owner.Session);
        Assert.Equal(8UL, owner.ClOrdId);
        Assert.Equal(2u, owner.Firm);
    }

    // Issue #319: cumQty/leavesQty are accumulated per-order on the
    // dispatcher's dispatch thread via OrderRegistry.OnTrade so wire ER
    // frames carry monotonically-cumulative values.
    [Fact]
    public void OnTrade_AccumulatesCumQty_AndComputesLeavesAgainstOriginalQty()
    {
        var map = new OrderRegistry();
        map.Register(1, S("a"), 7, 1, Side.Sell, 42, originalQty: 200);

        Assert.True(map.OnTrade(1, 60, out var cum1, out var lv1));
        Assert.Equal((60L, 140L), (cum1, lv1));

        Assert.True(map.OnTrade(1, 80, out var cum2, out var lv2));
        Assert.Equal((140L, 60L), (cum2, lv2));

        Assert.True(map.OnTrade(1, 60, out var cum3, out var lv3));
        Assert.Equal((200L, 0L), (cum3, lv3));

        Assert.True(map.TryResolve(1, out var st));
        Assert.Equal(200, st.CumQty);
        Assert.Equal(200, st.OriginalQty);
    }

    [Fact]
    public void OnTrade_SaturatesLeavesAtZero_WhenLastQtyOverflowsOriginalQty()
    {
        var map = new OrderRegistry();
        map.Register(1, S("a"), 7, 1, Side.Buy, 42, originalQty: 100);

        Assert.True(map.OnTrade(1, 150, out var cum, out var lv));
        Assert.Equal(150, cum);
        Assert.Equal(0, lv);
    }

    [Fact]
    public void OnTrade_WithZeroOriginalQty_FallsBackToCumWithZeroLeaves()
    {
        // Restored snapshot / pre-#319 owners may have OriginalQty == 0;
        // OnTrade must still produce a sensible (cum, leaves=0) tuple.
        var map = new OrderRegistry();
        map.Register(1, S("a"), 7, 1, Side.Buy, 42);

        Assert.True(map.OnTrade(1, 30, out var cum, out var lv));
        Assert.Equal(30, cum);
        Assert.Equal(0, lv);
    }

    [Fact]
    public void OnTrade_OnUnknownOrderId_ReturnsFalse()
    {
        var map = new OrderRegistry();
        Assert.False(map.OnTrade(99, 10, out var cum, out var lv));
        Assert.Equal(0, cum);
        Assert.Equal(0, lv);
    }

    [Fact]
    public void UpdateOriginalQty_RotatesDenominator_PreservesCumQty()
    {
        var map = new OrderRegistry();
        map.Register(1, S("a"), 7, 1, Side.Sell, 42, originalQty: 300);
        map.OnTrade(1, 100, out _, out _);

        // Replace down: new wire OrderQty = cum(100) + newLeaves(100) = 200.
        Assert.True(map.UpdateOriginalQty(1, 200));

        Assert.True(map.TryResolve(1, out var st));
        Assert.Equal(100, st.CumQty);
        Assert.Equal(200, st.OriginalQty);

        Assert.True(map.OnTrade(1, 100, out var cum, out var lv));
        Assert.Equal((200L, 0L), (cum, lv));
    }

    [Fact]
    public void UpdateOriginalQty_OnUnknownOrderId_ReturnsFalse()
    {
        var map = new OrderRegistry();
        Assert.False(map.UpdateOriginalQty(42, 100));
    }
}
