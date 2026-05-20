using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using B3.Exchange.Core;
using B3.Exchange.TestSupport;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Issue #321: end-to-end tests for POST /admin/instruments/{id}/phase.
/// Reuses the bootstrap pattern from <see cref="HttpServerTests"/>.
/// </summary>
public class HttpServerPhaseTests
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
                    ChannelNumber = 91,
                    IncrementalGroup = "239.255.42.91",
                    IncrementalPort = 30191,
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
    public async Task SetPhase_UnknownSecurityId_Returns404()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        var resp = await client.PostAsJsonAsync("/admin/instruments/9999999999/phase",
            new { targetPhase = "Open" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task SetPhase_UnknownTargetPhase_Returns400()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        var resp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/phase",
            new { targetPhase = "NotAPhase" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SetPhase_ValidSetPhaseTransition_Returns200()
    {
        // The HTTP layer routes plain phase changes through SetPhase; the
        // engine accepts every (current,target) pair so we exercise a
        // representative round-trip and assert the structured outcome.
        // A genuine 409 (engine InvalidOperationException) is only
        // reachable via UncrossAuction with a non-whitelisted pair, which
        // the routing prevents — covered by the dispatcher unit test.
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        var resp = await client.PostAsJsonAsync($"/admin/instruments/{secId}/phase",
            new { targetPhase = "Pause" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"previousPhase\":\"Open\"", body);
        Assert.Contains("\"currentPhase\":\"Pause\"", body);
    }

    [Fact]
    public async Task SetPhase_ReservedToOpen_UnctrossesAuctionAndReturns200()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        // Move to Reserved (plain SetPhase).
        var toReserved = await client.PostAsJsonAsync($"/admin/instruments/{secId}/phase",
            new { targetPhase = "Reserved" });
        Assert.Equal(HttpStatusCode.OK, toReserved.StatusCode);
        var toReservedBody = await toReserved.Content.ReadAsStringAsync();
        Assert.Contains("\"currentPhase\":\"Reserved\"", toReservedBody);

        // Move Reserved → Open: routes through UncrossAuction. No orders on
        // the book means no UncrossPrint is emitted, but the response must
        // still be 200 with currentPhase=Open.
        var toOpen = await client.PostAsJsonAsync($"/admin/instruments/{secId}/phase",
            new { targetPhase = "Open" });
        Assert.Equal(HttpStatusCode.OK, toOpen.StatusCode);
        var toOpenBody = await toOpen.Content.ReadAsStringAsync();
        Assert.Contains("\"previousPhase\":\"Reserved\"", toOpenBody);
        Assert.Contains("\"currentPhase\":\"Open\"", toOpenBody);
    }

    [Fact]
    public async Task SetPhase_MalformedBody_Returns400()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        long secId = FirstSecurityId(cfg);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

        using var content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync($"/admin/instruments/{secId}/phase", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
