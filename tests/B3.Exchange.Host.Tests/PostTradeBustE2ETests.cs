using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using B3.Exchange.PostTrade;
using B3.Exchange.TestSupport;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// ADR 0008 PR-5 — end-to-end coverage of the full operator bust flow
/// across the HTTP surface, exercising both the pre-EOD path (bust
/// folded out of <c>fills.csv</c> by the exporter) and the post-EOD
/// path (bust routed to <c>amendments.csv</c> after the day's
/// <c>fills.csv.done</c> has been published).
///
/// Pre-seeds the per-trade audit log with three fills, starts a real
/// <see cref="ExchangeHost"/> (which rebuilds its
/// <see cref="BustDedupIndex"/> from disk on boot), then drives the
/// full sequence over loopback HTTP:
///
///   1. POST /admin/post-trade/bust       (trade 1, pre-EOD)
///   2. POST /admin/post-trade/eod-export → fills.csv excludes trade 1
///   3. POST /admin/post-trade/bust       (trade 2, post-EOD)
///   4. amendments.csv + .done present, sha256OfOriginalFillRow matches
///      the on-disk row in fills.csv for trade 2
///
/// This is the only test that wires the full publisher chain end to
/// end — unit-level coverage for the components lives in
/// <c>AmendmentsPublisherTests</c>, <c>EodFillsExporterTests</c>, and
/// the validator-matrix tests in the PostTrade test project.
/// </summary>
public sealed class PostTradeBustE2ETests : IDisposable
{
    private const byte Channel = 91;
    private const long Petr = 900_000_000_001L;
    private static readonly DateOnly TradeDate = new(2026, 5, 18);
    private static readonly ulong TradeDateNanos =
        (ulong)(TradeDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;

    private readonly string _auditRoot;
    private readonly string _dropRoot;

    public PostTradeBustE2ETests()
    {
        var root = Path.Combine(Path.GetTempPath(), "B3BustE2E_" + Guid.NewGuid().ToString("N"));
        _auditRoot = Path.Combine(root, "audit");
        _dropRoot = Path.Combine(root, "drop");
        Directory.CreateDirectory(_auditRoot);
        Directory.CreateDirectory(_dropRoot);
    }

    public void Dispose()
    {
        try
        {
            var root = Path.GetDirectoryName(_auditRoot);
            if (root != null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private sealed class NullSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }


    private static PostTradeRecord MakeFill(uint id) => new(
        TradeId: id, TransactTimeNanos: TradeDateNanos + id * 1_000UL, SecurityId: Petr,
        AggressorSide: id % 2 == 0 ? Side.Buy : Side.Sell,
        Quantity: 100 + id, PriceMantissa: 10_0000L + id,
        BuyClOrdId: 1000UL + id, SellClOrdId: 2000UL + id,
        BuyFirm: 7, SellFirm: 7,
        BuyOrderId: 5000 + id, SellOrderId: 6000 + id);

    private static HostConfig BuildConfig(string instrumentsPath, string auditDir, string dropDir)
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
                    IncrementalGroup = "239.255.91.91",
                    IncrementalPort = 30191,
                    Ttl = 0,
                    InstrumentsFile = instrumentsPath,
                    PostTradeAudit = new PostTradeAuditConfig
                    {
                        Enabled = true,
                        DataDir = auditDir,
                        EodDropDir = dropDir,
                    },
                },
            },
        };

    [Fact]
    public async Task FullFlow_PreEodBust_FoldedOut_PostEodBust_PublishedToAmendmentsCsv()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");

        // 1) Pre-seed three fills before host boot so the dedup index
        //    picks them up at startup and the validator can locate the
        //    target trades. EnqueueOperatorBustV2 itself relies on
        //    BustDedupIndex + BustValidator wired by ExchangeHost.
        using (var w = new FileAuditLogWriter(_auditRoot, channelNumber: Channel))
        {
            w.OnTrade(MakeFill(1));
            w.OnTrade(MakeFill(2));
            w.OnTrade(MakeFill(3));
        }

        var cfg = BuildConfig(instrumentsPath, _auditRoot, _dropRoot);
        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
        await host.StartAsync();

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://{host.HttpEndpoint}"),
            Timeout = TimeSpan.FromSeconds(15),
        };

        var tradeDateStr = TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // 2) Pre-EOD bust on trade 1.
        var preBustUrl = $"/admin/post-trade/bust?channel={Channel}&tradeId=1&tradeDate={tradeDateStr}&correlationId=1001&busterFirm=99&reason=5&securityId={Petr}";
        using (var resp = await client.PostAsync(preBustUrl, content: null))
        {
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == HttpStatusCode.OK && body.Contains("\"status\":\"busted\"", StringComparison.Ordinal),
                $"expected 200 busted (pre-EOD); got {(int)resp.StatusCode}: '{body}'");
        }

        // Sanity: audit log must contain the bust record before we
        // trigger the EOD export (the exporter folds busts out of
        // fills.csv only if it can read the bust record back).
        var auditLogPath = Path.Combine(_auditRoot, Channel.ToString(CultureInfo.InvariantCulture),
            $"fills-{tradeDateStr}.log");
        var entries = AuditLogReader.ReadAllEntries(auditLogPath).ToList();
        var fills = entries.Where(e => e.Kind == AuditRecordKind.Fill).Select(e => e.Fill.TradeId).ToList();
        var busts = entries.Where(e => e.Kind == AuditRecordKind.Bust).Select(e => e.Bust.CancelledTradeId).ToList();
        Assert.True(busts.Count == 1 && busts[0] == 1,
            $"expected 1 bust cancelling tradeId=1; got fills=[{string.Join(",", fills)}] busts=[{string.Join(",", busts)}] at {auditLogPath} size={new FileInfo(auditLogPath).Length}");

        // 3) Trigger EOD export. fills.csv must NOT contain the pre-EOD
        //    bust target (trade 1) but must contain trades 2 and 3.
        var dropDir = Path.Combine(_dropRoot, Channel.ToString(CultureInfo.InvariantCulture), tradeDateStr);
        string csvPath;
        using (var resp = await client.PostAsync($"/admin/post-trade/eod-export?channel={Channel}&date={tradeDateStr}", content: null))
        {
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == HttpStatusCode.OK,
                $"expected 200 eod-export; got {(int)resp.StatusCode}: {body}");
            using var doc = JsonDocument.Parse(body);
            // 3 fills - 1 bust folded = 2 rows
            Assert.Equal(2, doc.RootElement.GetProperty("rowCount").GetInt64());
            csvPath = doc.RootElement.GetProperty("csvPath").GetString()!;
        }
        Assert.True(File.Exists(csvPath), $"expected {csvPath}");
        Assert.True(File.Exists(csvPath + ".done"), $"expected {csvPath}.done");
        var csvLines = File.ReadAllLines(csvPath);
        Assert.Equal(3, csvLines.Length); // header + 2 rows
        Assert.DoesNotContain(csvLines, l => l.StartsWith("1,", StringComparison.Ordinal));
        Assert.Contains(csvLines, l => l.StartsWith("2,", StringComparison.Ordinal));
        Assert.Contains(csvLines, l => l.StartsWith("3,", StringComparison.Ordinal));

        // 4) Post-EOD bust on trade 2 — fills.csv.done now exists, so
        //    the dispatcher routes through AmendmentsPublisher.
        var postBustUrl = $"/admin/post-trade/bust?channel={Channel}&tradeId=2&tradeDate={tradeDateStr}&correlationId=2002&busterFirm=99&reason=7&securityId={Petr}";
        using (var resp = await client.PostAsync(postBustUrl, content: null))
        {
            var body = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == HttpStatusCode.OK,
                $"expected 200 busted (post-EOD); got {(int)resp.StatusCode}: {body}");
            Assert.Contains("\"status\":\"busted\"", body, StringComparison.Ordinal);
        }

        // 5) amendments.csv + .done published next to fills.csv.
        var amendCsv = Path.Combine(dropDir, "amendments.csv");
        var amendDone = Path.Combine(dropDir, "amendments.csv.done");

        // ChannelDispatcher invokes AmendmentsPublisher synchronously on
        // its dispatch thread before completing the TCS, so by the time
        // the HTTP response returns the files MUST be visible. Tiny
        // poll guards against directory-cache lag on slow CI runners.
        for (int i = 0; i < 50 && !(File.Exists(amendCsv) && File.Exists(amendDone)); i++)
            await Task.Delay(20);

        Assert.True(File.Exists(amendCsv), $"expected {amendCsv}");
        Assert.True(File.Exists(amendDone), $"expected {amendDone}");

        var amendLines = File.ReadAllLines(amendCsv);
        Assert.Equal(2, amendLines.Length); // header + 1 row
        Assert.Equal("cancelTradeId,bustTransactTime,reasonCode,correlationId,sha256OfOriginalFillRow", amendLines[0]);
        var cols = amendLines[1].Split(',');
        Assert.Equal("2", cols[0]);
        Assert.Equal("7", cols[2]);
        Assert.Equal("2002", cols[3]);

        // 6) sha256OfOriginalFillRow must equal SHA256 of the bytes for
        //    trade 2's row in fills.csv (from the first byte of the row
        //    through and including the trailing LF). This is the
        //    consumer-visible contract from ADR 0008 §4.
        var fillsBytes = File.ReadAllBytes(csvPath);
        int rowStart = -1, rowEnd = -1;
        // Skip header row.
        int cursor = Array.IndexOf(fillsBytes, (byte)'\n') + 1;
        while (cursor < fillsBytes.Length)
        {
            int lf = Array.IndexOf(fillsBytes, (byte)'\n', cursor);
            if (lf < 0) break;
            // tradeId is leading column up to first comma.
            int comma = Array.IndexOf(fillsBytes, (byte)',', cursor);
            if (comma > 0 && comma < lf)
            {
                var tradeIdSpan = System.Text.Encoding.ASCII.GetString(fillsBytes, cursor, comma - cursor);
                if (tradeIdSpan == "2") { rowStart = cursor; rowEnd = lf; break; }
            }
            cursor = lf + 1;
        }
        Assert.True(rowStart >= 0 && rowEnd > rowStart, "trade 2 row not found in fills.csv");
        var expectedHash = Convert.ToHexString(SHA256.HashData(
            fillsBytes.AsSpan(rowStart, rowEnd - rowStart + 1))).ToLowerInvariant();
        Assert.Equal(expectedHash, cols[4]);

        // 7) .done sidecar SHA256 must match a fresh hash of amendments.csv.
        var amendBodyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(amendCsv)))
            .ToLowerInvariant();
        using (var doneDoc = JsonDocument.Parse(File.ReadAllText(amendDone)))
        {
            Assert.Equal(amendBodyHash, doneDoc.RootElement.GetProperty("sha256").GetString());
            Assert.Equal(1, doneDoc.RootElement.GetProperty("rowCount").GetInt64());
        }

        // 8) Replaying the post-EOD bust with the same correlation id
        //    is an idempotent 200 (X-Idempotent: true). The amendments
        //    file must still be present and unchanged.
        var amendBytesBefore = File.ReadAllBytes(amendCsv);
        using (var resp = await client.PostAsync(postBustUrl, content: null))
        {
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("\"status\":\"idempotent-replay\"", body, StringComparison.Ordinal);
            Assert.Equal("true", resp.Headers.GetValues("X-Idempotent").Single());
        }
        Assert.Equal(amendBytesBefore, File.ReadAllBytes(amendCsv));

        await host.StopAsync();
    }
}
