using B3.EntryPoint.Wire;
using B3.Exchange.Core;
using B3.Exchange.Gateway;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.Gateway.Tests;

public class NegotiationValidatorTests
{
    private static FirmRegistry BuildRegistry()
    {
        var firm = new Firm("BROKER1", "Broker One", EnteringFirmCode: 42);
        var sess = new SessionCredential("10101", "BROKER1", "secret", null, SessionPolicy.Default);
        return new FirmRegistry(new[] { firm }, new[] { sess });
    }

    private static (NegotiationValidator v, SessionClaimRegistry claims) Build(bool devMode = false)
    {
        var claims = new SessionClaimRegistry();
        var v = new NegotiationValidator(BuildRegistry(), claims, devMode,
            nowNanos: () => 1_000_000_000UL, timestampSkewToleranceNs: 0UL);
        return (v, claims);
    }

    private static NegotiateRequest Req(uint sid = 10101, ulong ver = 1, uint firm = 42, ulong ts = 0)
        => new(SessionId: sid, SessionVerId: ver, TimestampNanos: ts, EnteringFirm: firm, OnBehalfFirm: default);

    private static NegotiateCredentials Cred(string user = "10101", string key = "secret", string auth = "basic")
        => new(auth, user, key);

    [Fact]
    public void Accepts_valid_negotiate()
    {
        var (v, _) = Build();
        var o = v.Validate(Req(), Cred(), FixpState.Idle);
        Assert.True(o.IsAccepted);
        Assert.Equal(42u, o.Firm!.EnteringFirmCode);
    }

    [Fact]
    public void Already_negotiated_when_state_not_idle()
    {
        var (v, claims) = Build();
        claims.TryClaim(10101, 7, new object());
        var o = v.Validate(Req(), Cred(), FixpState.Negotiated);
        Assert.False(o.IsAccepted);
        Assert.Equal(FixpSbe.NegotiationRejectCode.ALREADY_NEGOTIATED, o.RejectCode);
        Assert.Equal(7UL, o.CurrentSessionVerId);
    }

    [Fact]
    public void Zero_session_id_rejected_invalid_sessionid()
    {
        var (v, _) = Build();
        var o = v.Validate(Req(sid: 0), Cred(user: "0"), FixpState.Idle);
        Assert.False(o.IsAccepted);
        Assert.Equal(FixpSbe.NegotiationRejectCode.INVALID_SESSIONID, o.RejectCode);
    }

    [Fact]
    public void Wrong_auth_type_rejected()
    {
        var (v, _) = Build();
        var o = v.Validate(Req(), Cred(auth: "oauth"), FixpState.Idle);
        Assert.Equal(FixpSbe.NegotiationRejectCode.CREDENTIALS, o.RejectCode);
    }

    [Fact]
    public void Username_mismatch_rejected()
    {
        var (v, _) = Build();
        var o = v.Validate(Req(), Cred(user: "wrong"), FixpState.Idle);
        Assert.Equal(FixpSbe.NegotiationRejectCode.CREDENTIALS, o.RejectCode);
    }

    [Fact]
    public void Unknown_session_rejected_credentials()
    {
        var (v, _) = Build();
        var o = v.Validate(Req(sid: 99999), Cred(user: "99999"), FixpState.Idle);
        Assert.Equal(FixpSbe.NegotiationRejectCode.CREDENTIALS, o.RejectCode);
    }

    [Fact]
    public void Wrong_firm_rejected_invalid_firm()
    {
        var (v, _) = Build();
        var o = v.Validate(Req(firm: 99), Cred(), FixpState.Idle);
        Assert.Equal(FixpSbe.NegotiationRejectCode.INVALID_FIRM, o.RejectCode);
    }

    [Fact]
    public void Wrong_access_key_rejected_credentials()
    {
        var (v, _) = Build();
        var o = v.Validate(Req(), Cred(key: "wrong"), FixpState.Idle);
        Assert.Equal(FixpSbe.NegotiationRejectCode.CREDENTIALS, o.RejectCode);
    }

    [Fact]
    public void DevMode_bypasses_access_key()
    {
        var (v, _) = Build(devMode: true);
        var o = v.Validate(Req(), Cred(key: "wrong"), FixpState.Idle);
        Assert.True(o.IsAccepted);
    }

    [Fact]
    public void DevMode_still_enforces_firm()
    {
        var (v, _) = Build(devMode: true);
        var o = v.Validate(Req(firm: 99), Cred(), FixpState.Idle);
        Assert.Equal(FixpSbe.NegotiationRejectCode.INVALID_FIRM, o.RejectCode);
    }

    [Fact]
    public void Zero_session_ver_id_rejected()
    {
        var (v, _) = Build();
        var o = v.Validate(Req(ver: 0), Cred(), FixpState.Idle);
        Assert.Equal(FixpSbe.NegotiationRejectCode.INVALID_SESSIONVERID, o.RejectCode);
    }

    [Fact]
    public void Stale_session_ver_id_rejected()
    {
        var (v, claims) = Build();
        claims.TryClaim(10101, 5, new object());
        // Token released so the active-claim check passes; the
        // last-seen rule should still trigger.
        var o = v.Validate(Req(ver: 5), Cred(), FixpState.Idle);
        Assert.Equal(FixpSbe.NegotiationRejectCode.INVALID_SESSIONVERID, o.RejectCode);
    }

    [Fact]
    public void Timestamp_skew_rejected_when_tolerance_set()
    {
        var claims = new SessionClaimRegistry();
        var v = new NegotiationValidator(BuildRegistry(), claims, devMode: false,
            nowNanos: () => 1_000_000_000UL,
            timestampSkewToleranceNs: 100_000_000UL);
        // far-future timestamp
        var o = v.Validate(Req(ts: 5_000_000_000UL), Cred(), FixpState.Idle);
        Assert.Equal(FixpSbe.NegotiationRejectCode.INVALID_TIMESTAMP, o.RejectCode);
    }
}
