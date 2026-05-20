using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using B3.Exchange.Core;
using B3.Exchange.TestSupport;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Issue #322: end-to-end tests for POST /admin/instruments/{id}/halt and
/// /resume. Mirrors <see cref="HttpServerPhaseTests"/>.
/// </summary>
public class HttpServerHaltTests
{
    private sealed class RecordingSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    private static (HostConfig cfg, IUmdfPacketSink sink) BuildConfig()
    {
        var cfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Http = new HttpConfig { Listen = "127.0.0.1:0", LivenessStaleMs = 10_000 },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 92,
                    IncrementalGroup = "239.255.42.92",
                    IncrementalPort = 30192,
                    Ttl = 0,
                    InstrumentsFile = TestPaths.ResolveRepoFile("config/instruments-eqt.json"),
                },
            },
        };
        return (cfg, new RecordingSink());
    }

    private static long FirstSecurityId(HostConfig cfg)
    {
        var path = cfg.Channels[0].InstrumentsFile;
        var instruments = B3.Exchange.Instruments.InstrumentLoader.LoadFromFile(path);
        return instruments[0].SecurityId;
    }

    [Fact]
    public async Task Halt_UnknownSecurityId_Returns404()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        var resp = await client.PostAsJsonAsync("/admin/instruments/9999999999/halt",
            new { reason = "RegulatoryHalt" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Halt_UnknownReason_Returns400()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        var resp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/halt",
            new { reason = "NotARealReason" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Halt_MissingReason_Returns400()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        var resp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/halt", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Halt_FreshInstrument_Returns200WithStateChange()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        var resp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/halt",
            new { reason = "RegulatoryHalt", note = "manual" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"stateChanged\":true", body);
        Assert.Contains("\"isHaltedNow\":true", body);
        Assert.Contains("\"reason\":\"RegulatoryHalt\"", body);
        Assert.Contains("\"note\":\"manual\"", body);
    }

    [Fact]
    public async Task Halt_AlreadyHalted_Returns200WithAlreadyHaltedTrue()
    {
        // Issue #322 review (gpt-5.5): halt is idempotent per the contract
        // ("re-halt mesma reason = no-op + 200"). Re-halting must return
        // 200 with alreadyHalted=true, NOT 409.
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        var first = await client.PostAsJsonAsync($"/admin/instruments/{secId}/halt",
            new { reason = "RegulatoryHalt" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/admin/instruments/{secId}/halt",
            new { reason = "NewsHold" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("\"stateChanged\":false", body);
        Assert.Contains("\"isHaltedNow\":true", body);
        Assert.Contains("\"alreadyHalted\":true", body);
    }

    [Fact]
    public async Task Resume_AfterHalt_Returns200WithStateChange()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        await client.PostAsJsonAsync($"/admin/instruments/{secId}/halt",
            new { reason = "RegulatoryHalt" });

        var resp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/resume", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"stateChanged\":true", body);
        Assert.Contains("\"isHaltedNow\":false", body);
    }

    [Fact]
    public async Task Resume_NotHalted_Returns409NoStateChange()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        var resp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/resume", new { });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"stateChanged\":false", body);
    }

    [Fact]
    public async Task SetPhase_WhileHalted_Returns409()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        await client.PostAsJsonAsync($"/admin/instruments/{secId}/halt",
            new { reason = "RegulatoryHalt" });

        var resp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/phase",
            new { targetPhase = "Pause" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Halt_NumericReasonString_Returns400()
    {
        // Issue #322 review (gpt-5.5): Enum.TryParse accepts numeric
        // strings for any byte value; ensure undefined enum values are
        // rejected at the API boundary so they don't reach _haltById.
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        var resp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/halt",
            new { reason = "200" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Resume_WithChainedPhase_AppliesPhaseAndReports()
    {
        // Issue #322 review (gpt-5.5): /resume body { phase } chains a
        // SetTradingPhase after the resume completes.
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        // Halt then resume with chained phase=Pause.
        var haltResp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/halt",
            new { reason = "RegulatoryHalt" });
        Assert.Equal(HttpStatusCode.OK, haltResp.StatusCode);

        var resumeResp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/resume",
            new { phase = "Pause" });
        Assert.Equal(HttpStatusCode.OK, resumeResp.StatusCode);
        var body = await resumeResp.Content.ReadAsStringAsync();
        Assert.Contains("\"chainedPhase\":\"Pause\"", body);
        Assert.DoesNotContain("\"chainedPhaseError\"", body);
    }

    [Fact]
    public async Task Resume_WithUnknownChainedPhase_Returns400()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        await client.PostAsJsonAsync($"/admin/instruments/{secId}/halt",
            new { reason = "RegulatoryHalt" });

        var resp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/resume",
            new { phase = "Bogus" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
