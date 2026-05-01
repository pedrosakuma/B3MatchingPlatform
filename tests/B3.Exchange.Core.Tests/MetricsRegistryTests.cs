using B3.Exchange.Core;

namespace B3.Exchange.Core.Tests;

public class MetricsRegistryTests
{
    [Fact]
    public void Render_EmitsExpectedSeries_WithLabelsAndHelpLines()
    {
        var reg = new MetricsRegistry();
        var ch = reg.RegisterChannel(84);
        ch.IncOrdersIn();
        ch.IncOrdersIn();
        ch.IncPacketsOut();
        ch.IncSnapshotsEmitted();
        ch.IncInstrumentDefsEmitted();
        ch.RecordTick(1_700_000_000_000);

        reg.SetSessionProvider(new StubSessionProvider(new[]
        {
            new SessionDiagnostics(
                SessionId: "conn-42", FirmId: "FIRM01", State: 3, SessionVerId: 17,
                OutboundSeq: 4291, InboundExpectedSeq: 1027, RetxBufferDepth: 4291,
                SendQueueDepth: 7, AttachedTransportId: "tx-2a", LastActivityAtMs: 1700000123456),
        }));

        var text = reg.RenderProm();

        // Counters present with channel label.
        Assert.Contains("# TYPE exch_orders_in_total counter\n", text);
        Assert.Contains("exch_orders_in_total{channel=\"84\"} 2\n", text);
        Assert.Contains("exch_packets_out_total{channel=\"84\"} 1\n", text);
        Assert.Contains("exch_snapshots_emitted_total{channel=\"84\"} 1\n", text);
        Assert.Contains("exch_instrument_defs_emitted_total{channel=\"84\"} 1\n", text);

        // Gauges.
        Assert.Contains("# TYPE exch_dispatch_loop_last_tick_unixms gauge\n", text);
        Assert.Contains("exch_dispatch_loop_last_tick_unixms{channel=\"84\"} 1700000000000\n", text);

        // Session gauges (issue #70 + legacy send-queue depth).
        Assert.Contains("# TYPE exch_send_queue_depth gauge\n", text);
        Assert.Contains("exch_send_queue_depth{channel=\"all\",session=\"conn-42\"} 7\n", text);
        Assert.Contains("# TYPE fixp_session_state gauge\n", text);
        Assert.Contains("fixp_session_state{session=\"conn-42\",firm=\"FIRM01\"} 3\n", text);
        Assert.Contains("fixp_session_outbound_seq{session=\"conn-42\"} 4291\n", text);
        Assert.Contains("fixp_session_inbound_expected_seq{session=\"conn-42\"} 1027\n", text);
        Assert.Contains("fixp_session_retx_buffer_depth{session=\"conn-42\"} 4291\n", text);
        Assert.Contains("fixp_session_attached_transports{session=\"conn-42\"} 1\n", text);
        Assert.Contains("fixp_session_last_activity_unixms{session=\"conn-42\"} 1700000123456\n", text);
    }

    [Fact]
    public void RegisterChannel_IsIdempotent()
    {
        var reg = new MetricsRegistry();
        var a = reg.RegisterChannel(7);
        var b = reg.RegisterChannel(7);
        Assert.Same(a, b);
    }

    [Fact]
    public void StartupReadinessProbe_FlipsOnMarkReady()
    {
        var p = new StartupReadinessProbe("startup");
        Assert.False(p.IsReady);
        p.MarkReady();
        Assert.True(p.IsReady);
        Assert.Equal("startup", p.Name);
    }

    [Fact]
    public void SessionLifecycleCounters_AreRendered_AndExposeMonotonicCounts()
    {
        var reg = new MetricsRegistry();
        reg.Sessions.IncEstablished();
        reg.Sessions.IncEstablished();
        reg.Sessions.IncSuspended();
        reg.Sessions.IncRebound();
        reg.Sessions.IncReaped();
        reg.Sessions.IncReaped();
        reg.Sessions.IncReaped();

        Assert.Equal(2, reg.Sessions.Established);
        Assert.Equal(1, reg.Sessions.Suspended);
        Assert.Equal(1, reg.Sessions.Rebound);
        Assert.Equal(3, reg.Sessions.Reaped);

        var text = reg.RenderProm();
        Assert.Contains("# TYPE exch_session_established_total counter\n", text);
        Assert.Contains("exch_session_established_total 2\n", text);
        Assert.Contains("exch_session_suspended_total 1\n", text);
        Assert.Contains("exch_session_rebound_total 1\n", text);
        Assert.Contains("exch_session_reaped_total 3\n", text);
    }

    [Fact]
    public void SessionLifecycleCounters_AreZeroByDefault_AndStillRendered()
    {
        var reg = new MetricsRegistry();
        var text = reg.RenderProm();
        // Counters MUST be rendered even at zero so scrapers don't see them
        // appear/disappear; this matches Prometheus best practices.
        Assert.Contains("exch_session_established_total 0\n", text);
        Assert.Contains("exch_session_suspended_total 0\n", text);
        Assert.Contains("exch_session_rebound_total 0\n", text);
        Assert.Contains("exch_session_reaped_total 0\n", text);
    }

    private sealed class StubSessionProvider : ISessionMetricsProvider
    {
        private readonly SessionDiagnostics[] _samples;
        public StubSessionProvider(SessionDiagnostics[] samples) { _samples = samples; }
        public IEnumerable<SessionDiagnostics> Sample() => _samples;
    }
}
