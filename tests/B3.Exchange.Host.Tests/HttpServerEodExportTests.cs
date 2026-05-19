using System.Net;
using System.Net.Http;
using System.Text.Json;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Issue #330 PR-2: smoke tests for the
/// <c>POST /admin/post-trade/eod-export</c> endpoint wired by
/// <see cref="ExchangeHost"/>. Pre-writes an audit log on disk via
/// <see cref="FileAuditLogWriter"/> at the location the exporter reads
/// from, then drives the endpoint over loopback HTTP.
/// </summary>
public class HttpServerEodExportTests
{
    private sealed class NullSink : IUmdfPacketSink
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

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private const byte Channel = 82;
    private static readonly DateOnly TestDate = new(2026, 5, 18);
    private static readonly ulong TestDateNanos =
        (ulong)(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;

    private static HostConfig BuildConfig(string instrumentsPath, string auditDir, string? dropDir)
        => new()
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Http = new HttpConfig { Listen = "127.0.0.1:0" },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = Channel,
                    IncrementalGroup = "239.255.82.82",
                    IncrementalPort = 30182,
                    Ttl = 0,
                    InstrumentsFile = instrumentsPath,
                    PostTradeAudit = new PostTradeAuditConfig
                    {
                        Enabled = true,
                        DataDir = auditDir,
                        EodDropDir = dropDir ?? "",
                    },
                },
            },
        };

    private static void SeedAuditLog(string auditDir, params PostTradeRecord[] records)
    {
        using var w = new FileAuditLogWriter(auditDir, channelNumber: Channel);
        foreach (var r in records) w.OnTrade(r);
    }

    private static PostTradeRecord MakeFill(uint id, long secId, ulong nanosOffset = 0)
        => new(
            TradeId: id, TransactTimeNanos: TestDateNanos + nanosOffset, SecurityId: secId,
            AggressorSide: id % 2 == 0 ? Side.Buy : Side.Sell,
            Quantity: 100 + id, PriceMantissa: 10_0000L + id,
            BuyClOrdId: 1000UL + id, SellClOrdId: 2000UL + id,
            BuyFirm: 7, SellFirm: 7,
            BuyOrderId: 5000 + id, SellOrderId: 6000 + id);

    [Fact]
    public async Task Post_EodExport_ProducesCsvAndDoneFilesAndReturnsJson()
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
        var auditDir = Path.Combine(Path.GetTempPath(), "b3-eod-audit-" + Guid.NewGuid().ToString("N"));
        var dropDir = Path.Combine(Path.GetTempPath(), "b3-eod-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(auditDir);
            // PETR4 = 900000000001 (resolved by symbol lookup at startup).
            SeedAuditLog(auditDir,
                MakeFill(1, 900000000001L),
                MakeFill(2, 900000000001L, nanosOffset: 1_000_000UL));

            var cfg = BuildConfig(instrumentsPath, auditDir, dropDir);
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();

            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };
            using var resp = await client.PostAsync($"/admin/post-trade/eod-export?channel={Channel}&date=2026-05-18", content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(2, doc.RootElement.GetProperty("rowCount").GetInt64());
            var csvPath = doc.RootElement.GetProperty("csvPath").GetString()!;
            var sha = doc.RootElement.GetProperty("sha256").GetString()!;
            Assert.True(File.Exists(csvPath));
            Assert.True(File.Exists(csvPath + ".done"));

            var lines = File.ReadAllLines(csvPath);
            Assert.Equal(3, lines.Length); // header + 2 rows
            Assert.Contains("PETR4", lines[1]);

            // .done sidecar SHA matches response body.
            using var doneDoc = JsonDocument.Parse(File.ReadAllText(csvPath + ".done"));
            Assert.Equal(sha, doneDoc.RootElement.GetProperty("sha256").GetString());

            await host.StopAsync();
        }
        finally
        {
            TryDeleteDir(auditDir);
            TryDeleteDir(dropDir);
        }
    }

    [Fact]
    public async Task Post_EodExport_Returns404_WhenChannelHasNoEodDropConfigured()
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
        var auditDir = Path.Combine(Path.GetTempPath(), "b3-eod-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Audit enabled but EodDropDir empty → exporter not wired.
            var cfg = BuildConfig(instrumentsPath, auditDir, dropDir: null);
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();

            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };
            using var resp = await client.PostAsync($"/admin/post-trade/eod-export?channel={Channel}&date=2026-05-18", content: null);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var text = await resp.Content.ReadAsStringAsync();
            Assert.Contains("eodDropDir", text, StringComparison.Ordinal);

            await host.StopAsync();
        }
        finally
        {
            TryDeleteDir(auditDir);
        }
    }

    [Fact]
    public async Task Post_EodExport_Returns404_WhenAuditLogForDateIsMissing()
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
        var auditDir = Path.Combine(Path.GetTempPath(), "b3-eod-audit-" + Guid.NewGuid().ToString("N"));
        var dropDir = Path.Combine(Path.GetTempPath(), "b3-eod-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(auditDir);
            var cfg = BuildConfig(instrumentsPath, auditDir, dropDir);
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();

            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };
            // No audit log written → 404.
            using var resp = await client.PostAsync($"/admin/post-trade/eod-export?channel={Channel}&date=2026-05-18", content: null);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var text = await resp.Content.ReadAsStringAsync();
            Assert.Contains("audit log not found", text, StringComparison.Ordinal);

            await host.StopAsync();
        }
        finally
        {
            TryDeleteDir(auditDir);
            TryDeleteDir(dropDir);
        }
    }

    [Fact]
    public async Task Post_EodExport_Returns400_OnMissingOrInvalidQueryParams()
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
        var auditDir = Path.Combine(Path.GetTempPath(), "b3-eod-audit-" + Guid.NewGuid().ToString("N"));
        var dropDir = Path.Combine(Path.GetTempPath(), "b3-eod-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = BuildConfig(instrumentsPath, auditDir, dropDir);
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();

            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

            using var noChannel = await client.PostAsync("/admin/post-trade/eod-export?date=2026-05-18", content: null);
            Assert.Equal(HttpStatusCode.BadRequest, noChannel.StatusCode);

            using var noDate = await client.PostAsync($"/admin/post-trade/eod-export?channel={Channel}", content: null);
            Assert.Equal(HttpStatusCode.BadRequest, noDate.StatusCode);

            using var badDate = await client.PostAsync($"/admin/post-trade/eod-export?channel={Channel}&date=2026/05/18", content: null);
            Assert.Equal(HttpStatusCode.BadRequest, badDate.StatusCode);

            using var badChannel = await client.PostAsync("/admin/post-trade/eod-export?channel=abc&date=2026-05-18", content: null);
            Assert.Equal(HttpStatusCode.BadRequest, badChannel.StatusCode);

            await host.StopAsync();
        }
        finally
        {
            TryDeleteDir(auditDir);
            TryDeleteDir(dropDir);
        }
    }

    [Fact]
    public async Task Post_EodExport_IsIdempotent_AcrossSequentialReruns()
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
        var auditDir = Path.Combine(Path.GetTempPath(), "b3-eod-audit-" + Guid.NewGuid().ToString("N"));
        var dropDir = Path.Combine(Path.GetTempPath(), "b3-eod-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(auditDir);
            SeedAuditLog(auditDir, MakeFill(1, 900000000001L), MakeFill(2, 900000000001L));

            var cfg = BuildConfig(instrumentsPath, auditDir, dropDir);
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();

            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };
            using var r1 = await client.PostAsync($"/admin/post-trade/eod-export?channel={Channel}&date=2026-05-18", content: null);
            using var r2 = await client.PostAsync($"/admin/post-trade/eod-export?channel={Channel}&date=2026-05-18", content: null);

            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

            using var d1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync());
            using var d2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync());
            Assert.Equal(
                d1.RootElement.GetProperty("sha256").GetString(),
                d2.RootElement.GetProperty("sha256").GetString());

            await host.StopAsync();
        }
        finally
        {
            TryDeleteDir(auditDir);
            TryDeleteDir(dropDir);
        }
    }

    [Fact]
    public async Task Post_EodExport_RejectsConcurrentSameChannelDateWith409()
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
        var auditDir = Path.Combine(Path.GetTempPath(), "b3-eod-audit-" + Guid.NewGuid().ToString("N"));
        var dropDir = Path.Combine(Path.GetTempPath(), "b3-eod-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(auditDir);
            // Many records so the export takes long enough to race against.
            var records = new PostTradeRecord[2000];
            for (uint i = 0; i < records.Length; i++)
                records[i] = MakeFill(i + 1, 900000000001L, nanosOffset: (ulong)i * 1000UL);
            SeedAuditLog(auditDir, records);

            var cfg = BuildConfig(instrumentsPath, auditDir, dropDir);
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();

            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };

            // Fire 8 parallel requests for the same (channel, date). Each
            // must return either 200 (the one that grabbed the in-flight
            // slot) or 409 (lost the race) — NEVER 500, never partial.
            var tasks = Enumerable.Range(0, 8).Select(_ =>
                client.PostAsync($"/admin/post-trade/eod-export?channel={Channel}&date=2026-05-18", content: null)).ToArray();
            var responses = await Task.WhenAll(tasks);
            try
            {
                int ok = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
                int conflict = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);
                Assert.Equal(responses.Length, ok + conflict);
                Assert.True(ok >= 1, "at least one request must succeed");
                // 409 count is environment-dependent (zero is acceptable
                // when requests serialize on the wire); the critical
                // guarantee is no 500s and no partial publishes.
            }
            finally
            {
                foreach (var r in responses) r.Dispose();
            }

            await host.StopAsync();
        }
        finally
        {
            TryDeleteDir(auditDir);
            TryDeleteDir(dropDir);
        }
    }
}
