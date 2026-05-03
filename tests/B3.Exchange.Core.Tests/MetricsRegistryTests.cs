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

    [Fact]
    public void SreCounters_AreRenderedEvenAtZero()
    {
        // Issue #155: dispatch-queue-full, decode-errors, and
        // transport-send-queue-full counters must be present in the
        // scrape output from t=0 so alerting rules can be written
        // before the first incident happens.
        var reg = new MetricsRegistry();
        reg.RegisterChannel(84);

        var text = reg.RenderProm();

        Assert.Contains("# TYPE exch_dispatch_queue_full_total counter\n", text);
        Assert.Contains("exch_dispatch_queue_full_total{channel=\"84\"} 0\n", text);
        Assert.Contains("# TYPE exch_decode_errors_total counter\n", text);
        Assert.Contains("exch_decode_errors_total{channel=\"84\"} 0\n", text);
        Assert.Contains("# TYPE exch_transport_send_queue_full_total counter\n", text);
        Assert.Contains("exch_transport_send_queue_full_total 0\n", text);
    }

    [Fact]
    public void SreCounters_IncrementsArePropagatedToScrape()
    {
        var reg = new MetricsRegistry();
        var ch = reg.RegisterChannel(84);
        ch.IncDispatchQueueFull();
        ch.IncDispatchQueueFull();
        ch.IncDispatchQueueFull();
        ch.IncDecodeErrors();
        reg.Transport.IncSendQueueFull();
        reg.Transport.IncSendQueueFull();

        var text = reg.RenderProm();

        Assert.Contains("exch_dispatch_queue_full_total{channel=\"84\"} 3\n", text);
        Assert.Contains("exch_decode_errors_total{channel=\"84\"} 1\n", text);
        Assert.Contains("exch_transport_send_queue_full_total 2\n", text);
    }

    [Fact]
    public void Render_EmitsRuntimeMetrics_Issue177()
    {
        var reg = new MetricsRegistry();
        var text = reg.RenderProm();

        // Process metrics — names mirror prometheus-net DotNetStats so that
        // existing dashboards work without modification.
        Assert.Contains("# TYPE process_cpu_seconds_total counter\n", text);
        Assert.Contains("# TYPE process_resident_memory_bytes gauge\n", text);
        Assert.Contains("# TYPE process_virtual_memory_bytes gauge\n", text);
        Assert.Contains("# TYPE process_start_time_seconds gauge\n", text);

        // Managed heap.
        Assert.Contains("# TYPE dotnet_total_memory_bytes gauge\n", text);
        Assert.Contains("# TYPE dotnet_total_allocated_bytes counter\n", text);

        // GC pause + per-generation collection counts.
        Assert.Contains("# TYPE dotnet_gc_collections_total counter\n", text);
        Assert.Contains("dotnet_gc_collections_total{generation=\"0\"} ", text);
        Assert.Contains("dotnet_gc_collections_total{generation=\"1\"} ", text);
        Assert.Contains("dotnet_gc_collections_total{generation=\"2\"} ", text);
        Assert.Contains("# TYPE dotnet_gc_pause_seconds_total counter\n", text);

        // ThreadPool.
        Assert.Contains("# TYPE dotnet_threadpool_threads_count gauge\n", text);
        Assert.Contains("# TYPE dotnet_threadpool_queue_length gauge\n", text);
        Assert.Contains("# TYPE dotnet_threadpool_completed_items_total counter\n", text);
    }

    [Fact]
    public void Render_EmitsLatencyHistograms_Issue173()
    {
        var reg = new MetricsRegistry();
        var ch = reg.RegisterChannel(7);

        // Inject samples directly into each histogram so the test is not
        // coupled to dispatcher timing. Buckets (s):
        //   50us, 100us, 250us, 500us, 1ms, 2.5ms, 5ms, 10ms,
        //   25ms, 50ms, 100ms, 250ms, 1s, +Inf
        ch.InboundDecode.Observe(0.00003);    // → 50us bucket
        ch.DispatchWait.Observe(0.0007);      // → 1ms bucket (le=0.001)
        ch.DispatchWait.Observe(0.003);       // → 5ms bucket (le=0.005)
        ch.EngineProcess.Observe(0.0001);     // → 100us bucket
        ch.OutboundEmit.Observe(2.0);         // → +Inf overflow
        ch.OutboundEmit.Observe(0.04);        // → 50ms bucket (le=0.05)

        var text = reg.RenderProm();

        foreach (var name in new[]
        {
            "exch_inbound_decode_seconds",
            "exch_dispatch_wait_seconds",
            "exch_engine_process_seconds",
            "exch_outbound_emit_seconds",
        })
        {
            Assert.Contains($"# TYPE {name} histogram\n", text);
            Assert.Contains($"{name}_bucket{{channel=\"7\",le=\"0.00005\"}} ", text);
            Assert.Contains($"{name}_bucket{{channel=\"7\",le=\"+Inf\"}} ", text);
            Assert.Contains($"{name}_sum{{channel=\"7\"}} ", text);
            Assert.Contains($"{name}_count{{channel=\"7\"}} ", text);
        }

        // Cumulative bucket invariants (Prometheus histograms are
        // cumulative: each le-bucket count includes all lower buckets).
        Assert.Contains("exch_inbound_decode_seconds_bucket{channel=\"7\",le=\"0.00005\"} 1\n", text);
        Assert.Contains("exch_inbound_decode_seconds_count{channel=\"7\"} 1\n", text);

        // dispatch_wait: 0.0007 → le=0.001 bucket, 0.003 → le=0.005.
        // Cumulative: le=0.001 covers 1; le=0.005 covers both = 2.
        Assert.Contains("exch_dispatch_wait_seconds_bucket{channel=\"7\",le=\"0.001\"} 1\n", text);
        Assert.Contains("exch_dispatch_wait_seconds_bucket{channel=\"7\",le=\"0.005\"} 2\n", text);
        Assert.Contains("exch_dispatch_wait_seconds_bucket{channel=\"7\",le=\"+Inf\"} 2\n", text);
        Assert.Contains("exch_dispatch_wait_seconds_count{channel=\"7\"} 2\n", text);

        // outbound_emit: 0.04 → le=0.05; 2.0 → overflow only counted at +Inf.
        Assert.Contains("exch_outbound_emit_seconds_bucket{channel=\"7\",le=\"0.05\"} 1\n", text);
        Assert.Contains("exch_outbound_emit_seconds_bucket{channel=\"7\",le=\"1\"} 1\n", text);
        Assert.Contains("exch_outbound_emit_seconds_bucket{channel=\"7\",le=\"+Inf\"} 2\n", text);
        Assert.Contains("exch_outbound_emit_seconds_count{channel=\"7\"} 2\n", text);
    }

    [Fact]
    public void LatencyHistogram_Observe_RejectsNaNAndNegative()
    {
        var h = new LatencyHistogram();
        h.Observe(double.NaN);
        h.Observe(-1.0);
        h.ObserveTicks(-100);

        var snap = h.SnapshotCounts();
        Assert.Equal(0, snap.OverflowCount);
        Assert.All(snap.BucketCounts, c => Assert.Equal(0, c));
        Assert.Equal(0.0, snap.SumSeconds);
    }

    private sealed class StubSessionProvider : ISessionMetricsProvider
    {
        private readonly SessionDiagnostics[] _samples;
        public StubSessionProvider(SessionDiagnostics[] samples) { _samples = samples; }
        public IEnumerable<SessionDiagnostics> Sample() => _samples;
    }
}
