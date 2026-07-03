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

    [Fact]
    public void SeedLastVersion_records_initial_version_for_unknown_session()
    {
        var r = new SessionClaimRegistry();
        r.SeedLastVersion(42, 9);
        Assert.Equal(9UL, r.CurrentSessionVerId(42));
    }

    [Fact]
    public void SeedLastVersion_keeps_max_across_calls()
    {
        var r = new SessionClaimRegistry();
        r.SeedLastVersion(42, 5);
        r.SeedLastVersion(42, 3);
        r.SeedLastVersion(42, 7);
        r.SeedLastVersion(42, 6);
        Assert.Equal(7UL, r.CurrentSessionVerId(42));
    }

    [Fact]
    public void SeedLastVersion_zero_is_noop()
    {
        var r = new SessionClaimRegistry();
        r.SeedLastVersion(42, 0);
        Assert.Equal(0UL, r.CurrentSessionVerId(42));
    }

    [Fact]
    public void TryClaim_after_seed_rejects_stale_version()
    {
        var r = new SessionClaimRegistry();
        r.SeedLastVersion(42, 10);
        var owner = new object();
        var result = r.TryClaim(42, sessionVerId: 10, owner);
        Assert.Equal(SessionClaimRegistry.ClaimResult.StaleVersion, result);
    }

    [Fact]
    public void TryClaim_after_seed_accepts_strictly_greater_version()
    {
        var r = new SessionClaimRegistry();
        r.SeedLastVersion(42, 10);
        var owner = new object();
        var result = r.TryClaim(42, sessionVerId: 11, owner);
        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted, result);
        Assert.Equal(11UL, r.CurrentSessionVerId(42));
    }

    // ===== TryForceTakeOver (issue #488) =====

    [Fact]
    public void TryForceTakeOver_HigherVersion_EvictsExistingClaim()
    {
        var r = new SessionClaimRegistry();
        var oldToken = new object();
        r.TryClaim(42, 5, oldToken);

        var newToken = new object();
        var result = r.TryForceTakeOver(42, 6, newToken, out var evicted);

        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted, result);
        Assert.Same(oldToken, evicted);
        Assert.Equal(6UL, r.CurrentSessionVerId(42));
        Assert.Equal(1, r.ActiveCount);
        // New token is now the claim holder.
        Assert.True(r.TryGetActiveClaim(42, out var holder, out _));
        Assert.Same(newToken, holder);
    }

    [Fact]
    public void TryForceTakeOver_SameVersion_RejectsAsStale()
    {
        var r = new SessionClaimRegistry();
        var oldToken = new object();
        r.TryClaim(42, 5, oldToken);

        var result = r.TryForceTakeOver(42, 5, new object(), out var evicted);

        Assert.Equal(SessionClaimRegistry.ClaimResult.StaleVersion, result);
        Assert.Null(evicted);
        // Old claim is still active.
        Assert.Equal(1, r.ActiveCount);
    }

    [Fact]
    public void TryForceTakeOver_LowerVersion_RejectsAsStale()
    {
        var r = new SessionClaimRegistry();
        var oldToken = new object();
        r.TryClaim(42, 10, oldToken);

        var result = r.TryForceTakeOver(42, 9, new object(), out var evicted);

        Assert.Equal(SessionClaimRegistry.ClaimResult.StaleVersion, result);
        Assert.Null(evicted);
    }

    [Fact]
    public void TryForceTakeOver_ZeroVersion_RejectsAsZero()
    {
        var r = new SessionClaimRegistry();
        r.TryClaim(42, 5, new object());
        var result = r.TryForceTakeOver(42, 0, new object(), out var evicted);
        Assert.Equal(SessionClaimRegistry.ClaimResult.ZeroVersion, result);
        Assert.Null(evicted);
    }

    [Fact]
    public void TryForceTakeOver_NoClaim_AcceptsAndRegisters()
    {
        // No existing claim: TryForceTakeOver behaves like a regular TryClaim.
        var r = new SessionClaimRegistry();
        var token = new object();
        var result = r.TryForceTakeOver(42, 7, token, out var evicted);

        Assert.Equal(SessionClaimRegistry.ClaimResult.Accepted, result);
        Assert.Null(evicted); // nothing was evicted
        Assert.Equal(7UL, r.CurrentSessionVerId(42));
    }

    [Fact]
    public void TryForceTakeOver_OldSessionRelease_IsNoopAfterTakeover()
    {
        // After TryForceTakeOver, the old session calls Release. Since the
        // registry now holds the new token, Release must be a no-op (it
        // should NOT evict the new session's claim).
        var r = new SessionClaimRegistry();
        var oldToken = new object();
        r.TryClaim(42, 5, oldToken);

        var newToken = new object();
        r.TryForceTakeOver(42, 6, newToken, out _);
        // Simulate old session calling Release during its CloseLocked.
        r.Release(42, oldToken);

        // New claim must still be active.
        Assert.Equal(1, r.ActiveCount);
        Assert.True(r.TryGetActiveClaim(42, out var holder, out _));
        Assert.Same(newToken, holder);
    }

    // ===== TryRestoreTakeOver (issue #492) =====

    [Fact]
    public void TryRestoreTakeOver_WhenNewTokenStillActive_RestoresOldClaimAndVerId()
    {
        // A takeover was force-applied, then its Negotiate persist failed.
        // Rolling it back must hand the claim (and verId) back to the
        // evicted old session and report success.
        var r = new SessionClaimRegistry();
        var oldToken = new object();
        r.TryClaim(42, 5, oldToken);

        var newToken = new object();
        r.TryForceTakeOver(42, 6, newToken, out _);
        Assert.Equal(6UL, r.CurrentSessionVerId(42));

        var restored = r.TryRestoreTakeOver(42, newToken, oldToken, oldVerId: 5);

        Assert.True(restored);
        Assert.True(r.TryGetActiveClaim(42, out var holder, out _));
        Assert.Same(oldToken, holder);
        Assert.Equal(5UL, r.CurrentSessionVerId(42));
    }

    [Fact]
    public void TryRestoreTakeOver_WhenRacingTakeoverWon_DoesNotClobberAndReturnsFalse()
    {
        // While the failed takeover was rolling back, a second takeover won
        // the registry. Restore must be a no-op (return false) so the caller
        // does not roll the SessionRegistry back over the racing winner.
        var r = new SessionClaimRegistry();
        var oldToken = new object();
        r.TryClaim(42, 5, oldToken);

        var newToken = new object();
        r.TryForceTakeOver(42, 6, newToken, out _);

        var racingToken = new object();
        r.TryForceTakeOver(42, 7, racingToken, out _);

        var restored = r.TryRestoreTakeOver(42, newToken, oldToken, oldVerId: 5);

        Assert.False(restored);
        Assert.True(r.TryGetActiveClaim(42, out var holder, out _));
        Assert.Same(racingToken, holder);
        Assert.Equal(7UL, r.CurrentSessionVerId(42));
    }
}
