using B3.Exchange.Gateway;

namespace B3.Exchange.Gateway.Tests;

public class FirmRegistryTests
{
    private static Firm Firm(string id, uint code = 1) => new(id, id, code);

    private static SessionCredential Cred(string sid, string firmId, string ak = "")
        => new(sid, firmId, ak, AllowedSourceCidrs: null, Policy: SessionPolicy.Default);

    [Fact]
    public void Construction_succeeds_with_valid_firms_and_sessions()
    {
        var r = new FirmRegistry(
            new[] { Firm("F1", 100), Firm("F2", 200) },
            new[] { Cred("1", "F1"), Cred("2", "F2") });

        Assert.Equal(2, r.Firms.Count);
        Assert.Equal(2, r.Credentials.Count);
        Assert.Equal(100u, r.FindFirm("F1")!.EnteringFirmCode);
        Assert.Equal("F1", r.FindSession("1")!.FirmId);
    }

    [Fact]
    public void FindSession_returns_null_when_unknown()
    {
        var r = new FirmRegistry(new[] { Firm("F1") }, new[] { Cred("1", "F1") });
        Assert.Null(r.FindSession("99999"));
    }

    [Fact]
    public void FindFirm_returns_null_when_unknown()
    {
        var r = new FirmRegistry(new[] { Firm("F1") }, Array.Empty<SessionCredential>());
        Assert.Null(r.FindFirm("99999"));
    }

    [Fact]
    public void FirmOf_resolves_firm_by_session_id()
    {
        var r = new FirmRegistry(new[] { Firm("F1", 42) }, new[] { Cred("1", "F1") });
        Assert.Equal(42u, r.FirmOf("1")!.EnteringFirmCode);
    }

    [Fact]
    public void FirmOf_returns_null_for_unknown_session()
    {
        var r = new FirmRegistry(new[] { Firm("F1") }, new[] { Cred("1", "F1") });
        Assert.Null(r.FirmOf("99999"));
    }

    [Fact]
    public void Duplicate_firm_id_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new FirmRegistry(new[] { Firm("F1"), Firm("F1") }, Array.Empty<SessionCredential>()));
        Assert.Contains("duplicate firm id", ex.Message);
    }

    [Fact]
    public void Duplicate_session_id_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new FirmRegistry(new[] { Firm("F1") }, new[] { Cred("1", "F1"), Cred("1", "F1") }));
        Assert.Contains("duplicate session id", ex.Message);
    }

    [Fact]
    public void Session_referencing_unknown_firm_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new FirmRegistry(new[] { Firm("F1") }, new[] { Cred("1", "GHOST") }));
        Assert.Contains("unknown firm 'GHOST'", ex.Message);
    }

    [Fact]
    public void Empty_firm_id_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FirmRegistry(new[] { Firm("") }, Array.Empty<SessionCredential>()));
    }

    [Fact]
    public void Empty_session_id_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FirmRegistry(new[] { Firm("F1") }, new[] { Cred("", "F1") }));
    }

    [Fact]
    public void Non_uint32_session_id_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new FirmRegistry(new[] { Firm("F1") }, new[] { Cred("FIRM01-SESS-01", "F1") }));
        Assert.Contains("uint32", ex.Message);
    }

    [Fact]
    public void Zero_session_id_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FirmRegistry(new[] { Firm("F1") }, new[] { Cred("0", "F1") }));
    }

    [Fact]
    public void FindSessionByWire_resolves_credential()
    {
        var r = new FirmRegistry(new[] { Firm("F1", 100) },
            new[] { Cred("4242", "F1", "secret") });
        var c = r.FindSessionByWire(4242u);
        Assert.NotNull(c);
        Assert.Equal("F1", c!.FirmId);
        Assert.Equal("secret", c.AccessKey);
    }

    [Fact]
    public void FindSessionByWire_returns_null_for_unknown()
    {
        var r = new FirmRegistry(new[] { Firm("F1") }, new[] { Cred("1", "F1") });
        Assert.Null(r.FindSessionByWire(999u));
    }

    [Fact]
    public void Invalid_policy_propagates_validation_error()
    {
        var bad = new SessionCredential("1", "F1", "", null,
            new SessionPolicy(KeepAliveIntervalMs: 0));
        Assert.Throws<InvalidOperationException>(() =>
            new FirmRegistry(new[] { Firm("F1") }, new[] { bad }));
    }

    [Fact]
    public void Empty_registry_is_valid()
    {
        var r = new FirmRegistry(Array.Empty<Firm>(), Array.Empty<SessionCredential>());
        Assert.Empty(r.Firms);
        Assert.Empty(r.Credentials);
    }

    [Fact]
    public void SessionPolicy_default_validates()
    {
        SessionPolicy.Default.Validate();
    }

    [Theory]
    [InlineData(0, 30_000, 5_000, 10_000)]   // KeepAlive
    [InlineData(30_000, 0, 5_000, 10_000)]   // IdleTimeout
    [InlineData(30_000, 30_000, 0, 10_000)]  // TestRequestGrace
    [InlineData(30_000, 30_000, 5_000, 0)]   // RetransmitBuffer
    public void SessionPolicy_rejects_zero_or_negative_durations(
        int keepAlive, int idle, int grace, int retxBuf)
    {
        var p = new SessionPolicy(0, keepAlive, idle, grace, retxBuf);
        Assert.Throws<InvalidOperationException>(p.Validate);
    }

    [Fact]
    public void SessionPolicy_rejects_negative_throttle()
    {
        var p = new SessionPolicy(ThrottleMessagesPerSecond: -1);
        Assert.Throws<InvalidOperationException>(p.Validate);
    }
}
