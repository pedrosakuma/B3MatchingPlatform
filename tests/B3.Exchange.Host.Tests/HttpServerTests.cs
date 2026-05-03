using System.Net.Http;
using B3.Exchange.Core;

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
            Auth = new AuthConfig { RequireFixpHandshake = false },
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

        using var live = await client.GetAsync("/health/live");
        Assert.Equal(System.Net.HttpStatusCode.OK, live.StatusCode);

        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);

        using var metrics = await client.GetAsync("/metrics");
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
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        Assert.Contains("snapshot-rotator", body);

        rotatorProbe.MarkReady();
        using var ready2 = await client.GetAsync("/health/ready");
        Assert.Equal(System.Net.HttpStatusCode.OK, ready2.StatusCode);
    }

    [Fact]
    public async Task LiveFlipsTo503_WithinThreshold_AfterDispatcherIsKilled()
    {
        // Threshold must be > the dispatcher's internal HeartbeatInterval
        // (1 s) so a healthy dispatcher's normal "tick every 1 s" cadence
        // doesn't itself trip the 503. 2500 ms keeps total test time well
        // under the 10 s acceptance window while leaving headroom for CI.
        var (cfg, sink) = BuildConfig(livenessStaleMs: 2500);
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var http = host.HttpEndpoint!;

        using var client = new HttpClient { BaseAddress = new Uri($"http://{http}") };

        // Healthy first.
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(System.Net.HttpStatusCode.OK, live.StatusCode);

        // Wedge the dispatcher loop without disposing. KillForTesting is an
        // internal test seam; reach it via reflection so this test does not
        // require Core/Gateway IVT to Host.Tests (issue #141).
        var killForTesting = typeof(B3.Exchange.Core.ChannelDispatcher).GetMethod(
            "KillForTesting",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var d in host.Dispatchers) killForTesting.Invoke(d, null);

        // Poll until /health/live flips to 503. Bound wait at 8 s (issue
        // acceptance: <10 s).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        System.Net.HttpStatusCode last = System.Net.HttpStatusCode.OK;
        while (DateTime.UtcNow < deadline)
        {
            using var resp = await client.GetAsync("/health/live");
            last = resp.StatusCode;
            if (last == System.Net.HttpStatusCode.ServiceUnavailable) break;
            await Task.Delay(100);
        }
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, last);
    }

    [Fact]
    public async Task OperatorTradeBust_Returns202_AndEmitsTradeBustOnIncrementalSink()
    {
        var (cfg, _) = BuildConfig();
        var incSink = new RecordingPacketSink();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => incSink);
        await host.StartAsync();
        var http = host.HttpEndpoint!;

        using var client = new HttpClient { BaseAddress = new Uri($"http://{http}") };

        // securityId 900_000_000_001 = PETR4 (matches config/instruments-eqt.json).
        var url = "/channel/84/trade-bust/4242?securityId=900000000001&priceMantissa=2505000&size=100&tradeDate=19500";
        var resp = await client.PostAsync(url, content: null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("trade-bust", body);
        Assert.Contains("4242", body);

        // Wait for dispatcher to drain.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (incSink.Packets.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(incSink.Packets.Count >= 1, "expected TradeBust packet on incremental sink");

        var packet = incSink.Packets[0];
        // SBE TemplateId @ packet[16+4+2..+4] == 57.
        ushort templateId = System.Runtime.InteropServices.MemoryMarshal.Read<ushort>(packet.AsSpan(16 + 4 + 2, 2));
        Assert.Equal((ushort)57, templateId);

        // Bad channel.
        var unknown = await client.PostAsync("/channel/200/trade-bust/4242?securityId=1", content: null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);

        // Missing securityId.
        var missing = await client.PostAsync("/channel/84/trade-bust/4242", content: null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, missing.StatusCode);

        // Invalid securityId (zero).
        var invalid = await client.PostAsync("/channel/84/trade-bust/4242?securityId=0", content: null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);

        // Invalid tradeDate (out of ushort range).
        var badDate = await client.PostAsync("/channel/84/trade-bust/4242?securityId=900000000001&tradeDate=70000", content: null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badDate.StatusCode);
    }

    [Fact]
    public async Task OperatorBumpVersion_Returns202_AndEmitsChannelResetOnIncrementalSink()
    {
        var (cfg, _) = BuildConfig();
        var incSink = new RecordingPacketSink();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => incSink);
        await host.StartAsync();
        var http = host.HttpEndpoint!;

        using var client = new HttpClient { BaseAddress = new Uri($"http://{http}") };

        var resp = await client.PostAsync("/channel/84/bump-version", content: null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);

        // Wait for the dispatcher to drain the bump-version work item.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (incSink.Packets.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(incSink.Packets.Count >= 1, "expected ChannelReset packet on incremental sink");
        var packet = incSink.Packets[0];

        // SBE TemplateId == 11 (ChannelReset_11). PacketHeader=16, Framing=4,
        // SBE MessageHeader: BlockLength(2) + TemplateId(2) ...
        int sbeHdrStart = 16 + 4;
        ushort templateId = System.Runtime.InteropServices.MemoryMarshal.Read<ushort>(
            packet.AsSpan(sbeHdrStart + 2, 2));
        Assert.Equal((ushort)11, templateId);

        // SequenceVersion bumped from the default 1 to 2.
        ushort version = System.Runtime.InteropServices.MemoryMarshal.Read<ushort>(packet.AsSpan(2, 2));
        Assert.Equal((ushort)2, version);
    }

    [Fact]
    public async Task OperatorSnapshotNow_Returns202_AndUnknownChannelReturns404()
    {
        var (cfg, sink) = BuildConfig();
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var http = host.HttpEndpoint!;

        using var client = new HttpClient { BaseAddress = new Uri($"http://{http}") };

        var ok = await client.PostAsync("/channel/84/snapshot-now", content: null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, ok.StatusCode);

        var unknown = await client.PostAsync("/channel/200/snapshot-now", content: null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);

        var unknownBump = await client.PostAsync("/channel/200/bump-version", content: null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknownBump.StatusCode);
    }

    [Fact]
    public async Task SessionsAndFirms_ExposeOperatorJson_AndUnknownSessionReturns404()
    {
        // Issue #70 acceptance: GET /sessions, /sessions/{id}, /firms.
        var (cfg, sink) = BuildConfig();
        cfg.Firms.Add(new FirmConfig { Id = "FIRM01", Name = "Acme", EnteringFirmCode = 7 });
        cfg.Sessions.Add(new SessionConfig { SessionId = "1", FirmId = "FIRM01" });
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var http = host.HttpEndpoint!;

        using var client = new HttpClient { BaseAddress = new Uri($"http://{http}") };

        // /firms — returns the configured firms as JSON.
        using var firmsResp = await client.GetAsync("/firms");
        Assert.Equal(System.Net.HttpStatusCode.OK, firmsResp.StatusCode);
        var firmsBody = await firmsResp.Content.ReadAsStringAsync();
        Assert.Contains("\"id\":\"FIRM01\"", firmsBody);
        Assert.Contains("\"name\":\"Acme\"", firmsBody);
        Assert.Contains("\"enteringFirmCode\":7", firmsBody);

        // /sessions — returns an empty array when no clients are connected.
        using var sessionsResp = await client.GetAsync("/sessions");
        Assert.Equal(System.Net.HttpStatusCode.OK, sessionsResp.StatusCode);
        var sessionsBody = await sessionsResp.Content.ReadAsStringAsync();
        Assert.Equal("[]", sessionsBody.Trim());

        // /sessions/{id} — 404 on unknown id.
        using var unknown = await client.GetAsync("/sessions/conn-99999");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task SessionsEndpoint_RevealsConnectedTcpClient()
    {
        // Issue #70: validate /sessions exposes a real connected client.
        // RequireFixpHandshake=false so a plain TCP connect is enough.
        var (cfg, sink) = BuildConfig();
        cfg.Firms.Add(new FirmConfig { Id = "FIRM01", Name = "Acme", EnteringFirmCode = 7 });
        cfg.Sessions.Add(new SessionConfig { SessionId = "1", FirmId = "FIRM01" });
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var http = host.HttpEndpoint!;
        var tcp = host.TcpEndpoint!;

        using var tcpClient = new System.Net.Sockets.TcpClient();
        await tcpClient.ConnectAsync(tcp.Address, tcp.Port);

        using var http_client = new HttpClient { BaseAddress = new Uri($"http://{http}") };
        // Poll because session registration races the TCP accept.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        string body = "[]";
        while (DateTime.UtcNow < deadline)
        {
            using var resp = await http_client.GetAsync("/sessions");
            body = await resp.Content.ReadAsStringAsync();
            if (body.Contains("\"sessionId\"", StringComparison.Ordinal)) break;
            await Task.Delay(50);
        }

        // Parse the JSON to validate shape and pick out the session id.
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() >= 1, "expected at least one session in /sessions");
        var first = doc.RootElement[0];
        var sid = first.GetProperty("sessionId").GetString()!;
        Assert.StartsWith("conn-", sid);
        Assert.Equal("FIRM01", first.GetProperty("firmId").GetString());
        // State 0..4 are valid (Idle..Terminated). For an unauthenticated
        // TCP connect with handshake disabled the session sits in Idle (0).
        var state = first.GetProperty("state").GetInt32();
        Assert.InRange(state, 0, 4);
        Assert.StartsWith("tx-", first.GetProperty("attachedTransportId").GetString());
        Assert.True(first.GetProperty("lastActivityAtMs").GetInt64() > 0);

        // /sessions/{id} returns the same single session object.
        using var oneResp = await http_client.GetAsync($"/sessions/{sid}");
        Assert.Equal(System.Net.HttpStatusCode.OK, oneResp.StatusCode);
        var oneBody = await oneResp.Content.ReadAsStringAsync();
        using var oneDoc = System.Text.Json.JsonDocument.Parse(oneBody);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, oneDoc.RootElement.ValueKind);
        Assert.Equal(sid, oneDoc.RootElement.GetProperty("sessionId").GetString());
    }

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        public List<byte[]> Packets { get; } = new();
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
        {
            lock (Packets) Packets.Add(packet.ToArray());
        }
    }
}
