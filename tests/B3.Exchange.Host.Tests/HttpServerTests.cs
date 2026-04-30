using System.Net.Http;
using B3.Exchange.Integration;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Smoke tests for the Kestrel-hosted /health/live, /health/ready, and
/// /metrics endpoints. Covers issue #5 acceptance:
///   * 200 on all three endpoints under nominal conditions.
///   * /metrics exposes the documented counters/gauges.
///   * Killing a dispatcher flips /health/live to 503 within <10 s.
/// </summary>
public class HttpServerTests
{
    private static (HostConfig cfg, IUmdfPacketSink sink) BuildConfig(int livenessStaleMs = 5000)
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
        var cfg = new HostConfig
        {
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Http = new HttpConfig { Listen = "127.0.0.1:0", LivenessStaleMs = livenessStaleMs },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 84,
                    IncrementalGroup = "239.255.42.84",
                    IncrementalPort = 30184,
                    Ttl = 0,
                    InstrumentsFile = instrumentsPath,
                },
            },
        };
        return (cfg, new RecordingSink());
    }

    private sealed class RecordingSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    private static string ResolveRepoFile(string relPath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, relPath);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"could not locate {relPath} from {AppContext.BaseDirectory}");
    }

    [Fact]
    public async Task LiveAndReadyAndMetrics_AllReturn200_AfterStartup()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var http = host.HttpEndpoint!;

        using var client = new HttpClient { BaseAddress = new Uri($"http://{http}") };

        var live = await client.GetAsync("/health/live");
        Assert.Equal(System.Net.HttpStatusCode.OK, live.StatusCode);

        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);

        var metrics = await client.GetAsync("/metrics");
        Assert.Equal(System.Net.HttpStatusCode.OK, metrics.StatusCode);
        var body = await metrics.Content.ReadAsStringAsync();
        Assert.Contains("exch_orders_in_total", body);
        Assert.Contains("exch_packets_out_total", body);
        Assert.Contains("exch_send_queue_depth", body);
        Assert.Contains("exch_snapshots_emitted_total", body);
        Assert.Contains("exch_instrument_defs_emitted_total", body);
        Assert.Contains("exch_dispatch_loop_last_tick_unixms", body);
        Assert.Contains("channel=\"84\"", body);
    }

    [Fact]
    public async Task ReadyReturns503_WhenAnAdditionalProbeIsNotReady()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        var rotatorProbe = new StartupReadinessProbe("snapshot-rotator");
        host.RegisterReadinessProbe(rotatorProbe);
        await host.StartAsync();
        var http = host.HttpEndpoint!;

        using var client = new HttpClient { BaseAddress = new Uri($"http://{http}") };
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        Assert.Contains("snapshot-rotator", body);

        rotatorProbe.MarkReady();
        var ready2 = await client.GetAsync("/health/ready");
        Assert.Equal(System.Net.HttpStatusCode.OK, ready2.StatusCode);
    }

    [Fact]
    public async Task LiveFlipsTo503_WithinThreshold_AfterDispatcherIsKilled()
    {
        // Short threshold so the test stays well under 10 s.
        var (cfg, sink) = BuildConfig(livenessStaleMs: 500);
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var http = host.HttpEndpoint!;

        using var client = new HttpClient { BaseAddress = new Uri($"http://{http}") };

        // Healthy first.
        var live = await client.GetAsync("/health/live");
        Assert.Equal(System.Net.HttpStatusCode.OK, live.StatusCode);

        // Wedge the dispatcher loop without disposing.
        foreach (var d in host.Dispatchers) d.KillForTesting();

        // Poll until /health/live flips to 503. Bound wait at 8 s (issue
        // acceptance: <10 s).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        System.Net.HttpStatusCode last = System.Net.HttpStatusCode.OK;
        while (DateTime.UtcNow < deadline)
        {
            var resp = await client.GetAsync("/health/live");
            last = resp.StatusCode;
            if (last == System.Net.HttpStatusCode.ServiceUnavailable) break;
            await Task.Delay(100);
        }
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, last);
    }
}
