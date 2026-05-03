using B3.EntryPoint.Wire;
using B3.Exchange.Gateway;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.Gateway.Tests;

public class EstablishValidatorTests
{
    private static EstablishRequest Req(uint sid = 100, ulong ver = 1, ulong ts = 0,
        ulong keep = 5_000UL, uint nextSeq = 1)
        => new(sid, ver, ts, keep, nextSeq, CancelOnDisconnectType: 0, CodTimeoutWindowMillis: 0);

    [Fact]
    public void Accepts_valid_request_in_negotiated_state()
    {
        var v = new EstablishValidator(timestampSkewToleranceNs: 0);
        var o = v.Validate(Req(sid: 100, ver: 1), FixpState.Negotiated, 100, 1, 0);
        Assert.True(o.IsAccepted);
    }

    [Fact]
    public void Rejects_when_idle_with_unnegotiated()
    {
        var v = new EstablishValidator(timestampSkewToleranceNs: 0);
        var o = v.Validate(Req(), FixpState.Idle, 100, 1, 0);
        Assert.False(o.IsAccepted);
        Assert.Equal(FixpSbe.EstablishRejectCode.UNNEGOTIATED, o.RejectCode);
    }

    [Fact]
    public void Rejects_when_already_established()
    {
        var v = new EstablishValidator(timestampSkewToleranceNs: 0);
        var o = v.Validate(Req(), FixpState.Established, 100, 1, 0);
        Assert.False(o.IsAccepted);
        Assert.Equal(FixpSbe.EstablishRejectCode.ALREADY_ESTABLISHED, o.RejectCode);
    }

    [Fact]
    public void Rejects_session_id_mismatch()
    {
        var v = new EstablishValidator(timestampSkewToleranceNs: 0);
        var o = v.Validate(Req(sid: 999), FixpState.Negotiated, 100, 1, 0);
        Assert.Equal(FixpSbe.EstablishRejectCode.INVALID_SESSIONID, o.RejectCode);
    }

    [Fact]
    public void Rejects_session_ver_mismatch()
    {
        var v = new EstablishValidator(timestampSkewToleranceNs: 0);
        var o = v.Validate(Req(sid: 100, ver: 2), FixpState.Negotiated, 100, 1, 0);
        Assert.Equal(FixpSbe.EstablishRejectCode.INVALID_SESSIONVERID, o.RejectCode);
    }

    [Fact]
    public void Rejects_timestamp_skew_beyond_tolerance()
    {
        ulong now = 1_000_000_000UL;
        var v = new EstablishValidator(nowNanos: () => now,
            timestampSkewToleranceNs: 1_000UL);
        var req = Req(sid: 100, ver: 1, ts: now + 10_000UL);
        var o = v.Validate(req, FixpState.Negotiated, 100, 1, 0);
        Assert.Equal(FixpSbe.EstablishRejectCode.INVALID_TIMESTAMP, o.RejectCode);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(60_001UL)]
    public void Rejects_keepalive_out_of_range(ulong keep)
    {
        var v = new EstablishValidator(timestampSkewToleranceNs: 0);
        var req = Req(sid: 100, ver: 1, keep: keep);
        var o = v.Validate(req, FixpState.Negotiated, 100, 1, 0);
        Assert.Equal(FixpSbe.EstablishRejectCode.INVALID_KEEPALIVE_INTERVAL, o.RejectCode);
    }

    [Fact]
    public void Rejects_nextseqno_zero_with_last_incoming_echoed()
    {
        var v = new EstablishValidator(timestampSkewToleranceNs: 0);
        var req = Req(sid: 100, ver: 1, nextSeq: 0);
        var o = v.Validate(req, FixpState.Negotiated, 100, 1, lastIncomingSeqNo: 7);
        Assert.Equal(FixpSbe.EstablishRejectCode.INVALID_NEXTSEQNO, o.RejectCode);
        Assert.Equal(7u, o.LastIncomingSeqNo);
    }

    [Fact]
    public void State_check_takes_precedence_over_field_validation()
    {
        // Bad keepAlive AND idle state: state-machine reason wins.
        var v = new EstablishValidator(timestampSkewToleranceNs: 0);
        var req = Req(sid: 100, ver: 1, keep: 99_999UL);
        var o = v.Validate(req, FixpState.Idle, 100, 1, 0);
        Assert.Equal(FixpSbe.EstablishRejectCode.UNNEGOTIATED, o.RejectCode);
    }
}
