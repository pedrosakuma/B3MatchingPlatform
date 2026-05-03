using B3.Exchange.Contracts;
using B3.Exchange.Gateway;
using Side = B3.Exchange.Matching.Side;

namespace B3.Exchange.Gateway.Tests;

public class OrderOwnershipMapTests
{
    private static SessionId S(string s) => new SessionId(s);

    [Fact]
    public void Register_Then_TryResolve_ReturnsOwner()
    {
        var map = new OrderOwnershipMap();
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
        var map = new OrderOwnershipMap();
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
        var map = new OrderOwnershipMap();
        map.Register(100, S("a"), 7, 1, Side.Buy, 42);

        Assert.True(map.Evict(100));
        Assert.False(map.TryResolve(100, out _));
        Assert.False(map.TryResolveByClOrdId(1, 7, out _));
        Assert.False(map.Evict(100)); // idempotent
    }

    [Fact]
    public void FilterMassCancel_ScopesBySessionFirmSideInstrument()
    {
        var map = new OrderOwnershipMap();
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
        var map = new OrderOwnershipMap();
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
        var map = new OrderOwnershipMap();
        map.Register(100, S("a"), 7, 1, Side.Buy, 42);
        map.Register(100, S("b"), 8, 2, Side.Sell, 99);

        Assert.True(map.TryResolve(100, out var owner));
        Assert.Equal(S("b"), owner.Session);
        Assert.Equal(8UL, owner.ClOrdId);
        Assert.Equal(2u, owner.Firm);
    }
}
