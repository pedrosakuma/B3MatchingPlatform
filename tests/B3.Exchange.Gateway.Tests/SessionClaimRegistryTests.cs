using B3.Exchange.Gateway;

namespace B3.Exchange.Gateway.Tests;

public class SessionClaimRegistryTests
{
    [Fact]
    public void First_claim_accepted_and_records_version()
    {
        var r = new SessionClaimRegistry();
        var token = new object();
        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted, r.TryClaim(10101, 5, token));
        Assert.Equal(5UL, r.CurrentSessionVerId(10101));
        Assert.Equal(1, r.ActiveCount);
    }

    [Fact]
    public void Zero_version_rejected()
    {
        var r = new SessionClaimRegistry();
        Assert.Equal(SessionClaimRegistry.ClaimResult.ZeroVersion, r.TryClaim(10101, 0, new object()));
    }

    [Fact]
    public void Duplicate_claim_while_active_rejected()
    {
        var r = new SessionClaimRegistry();
        r.TryClaim(10101, 1, new object());
        Assert.Equal(SessionClaimRegistry.ClaimResult.DuplicateConnection, r.TryClaim(10101, 2, new object()));
    }

    [Fact]
    public void Stale_version_rejected_after_release()
    {
        var r = new SessionClaimRegistry();
        var t1 = new object();
        r.TryClaim(10101, 5, t1);
        r.Release(10101, t1);
        // Same or smaller version after release: reject as stale.
        Assert.Equal(SessionClaimRegistry.ClaimResult.StaleVersion, r.TryClaim(10101, 5, new object()));
        Assert.Equal(SessionClaimRegistry.ClaimResult.StaleVersion, r.TryClaim(10101, 1, new object()));
    }

    [Fact]
    public void Higher_version_accepted_after_release()
    {
        var r = new SessionClaimRegistry();
        var t1 = new object();
        r.TryClaim(10101, 5, t1);
        r.Release(10101, t1);
        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted, r.TryClaim(10101, 6, new object()));
        Assert.Equal(6UL, r.CurrentSessionVerId(10101));
    }

    [Fact]
    public void Release_by_non_owner_is_noop()
    {
        var r = new SessionClaimRegistry();
        var owner = new object();
        r.TryClaim(10101, 1, owner);
        r.Release(10101, new object());
        // Owner still holds it.
        Assert.Equal(SessionClaimRegistry.ClaimResult.DuplicateConnection, r.TryClaim(10101, 2, new object()));
    }

    [Fact]
    public void CurrentSessionVerId_unknown_session_returns_zero()
    {
        Assert.Equal(0UL, new SessionClaimRegistry().CurrentSessionVerId(99999));
    }

    [Fact]
    public void TryGetActiveClaim_returns_holder_and_version_for_active()
    {
        var r = new SessionClaimRegistry();
        var owner = new object();
        r.TryClaim(42, 7, owner);
        Assert.True(r.TryGetActiveClaim(42, out var holder, out var ver));
        Assert.Same(owner, holder);
        Assert.Equal(7UL, ver);
    }

    [Fact]
    public void TryGetActiveClaim_returns_false_when_unknown()
    {
        Assert.False(new SessionClaimRegistry().TryGetActiveClaim(42, out _, out _));
    }

    [Fact]
    public void TryGetActiveClaim_returns_false_after_release()
    {
        var r = new SessionClaimRegistry();
        var owner = new object();
        r.TryClaim(42, 7, owner);
        r.Release(42, owner);
        Assert.False(r.TryGetActiveClaim(42, out _, out _));
    }
}
