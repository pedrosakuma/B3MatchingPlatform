using System.Net;
using System.Net.Http;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using B3.Exchange.PostTrade;
using B3.Exchange.TestSupport;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Issue #330 PR-3: triggering the daily reset (scheduled or via
/// <c>/admin/daily-reset</c>) must also chain the EOD fills CSV export
/// for every channel whose <c>postTradeAudit.eodDropDir</c> is set,
/// projecting yesterday UTC's audit log. Per-channel failures must
/// not block other channels.
/// </summary>
public class DailyResetEodExportChainingTests
{
    private sealed class NullSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }



    private static PostTradeRecord MakeFill(uint id, ulong nanos, long secId = 900000000001L)
        => new(
            TradeId: id, TransactTimeNanos: nanos, SecurityId: secId,
            AggressorSide: Side.Buy,
            Quantity: 100 + id, PriceMantissa: 10_0000L + id,
            BuyClOrdId: 1000UL + id, SellClOrdId: 2000UL + id,
            BuyFirm: 7, SellFirm: 7,
            BuyOrderId: 5000 + id, SellOrderId: 6000 + id);

    private static ulong NanosForDate(DateOnly d)
        => (ulong)(d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;

    [Fact]
    public async Task TriggerDailyReset_ProjectsYesterdayAuditLogForAllConfiguredChannels()
    {
        var eqtPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var drvPath = TestPaths.ResolveRepoFile("config/instruments-drv.json");
        var auditDirA = Path.Combine(Path.GetTempPath(), "b3-dr-audit-a-" + Guid.NewGuid().ToString("N"));
        var dropDirA = Path.Combine(Path.GetTempPath(), "b3-dr-drop-a-" + Guid.NewGuid().ToString("N"));
        var auditDirB = Path.Combine(Path.GetTempPath(), "b3-dr-audit-b-" + Guid.NewGuid().ToString("N"));
        var dropDirB = Path.Combine(Path.GetTempPath(), "b3-dr-drop-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(auditDirA);
            Directory.CreateDirectory(auditDirB);
            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
            var yesterdayNanos = NanosForDate(yesterday);

            using (var w = new FileAuditLogWriter(auditDirA, channelNumber: 91))
                w.OnTrade(MakeFill(1, yesterdayNanos));
            using (var w = new FileAuditLogWriter(auditDirB, channelNumber: 92))
            {
                w.OnTrade(MakeFill(1, yesterdayNanos));
                w.OnTrade(MakeFill(2, yesterdayNanos + 1_000UL));
            }

            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
                Channels =
                {
                    new ChannelConfig
                    {
                        ChannelNumber = 91,
                        IncrementalGroup = "239.255.91.91",
                        IncrementalPort = 30191,
                        Ttl = 0,
                        InstrumentsFile = eqtPath,
                        PostTradeAudit = new PostTradeAuditConfig
                        {
                            Enabled = true,
                            DataDir = auditDirA,
                            EodDropDir = dropDirA,
                        },
                    },
                    new ChannelConfig
                    {
                        ChannelNumber = 92,
                        IncrementalGroup = "239.255.92.92",
                        IncrementalPort = 30192,
                        Ttl = 0,
                        InstrumentsFile = drvPath,
                        PostTradeAudit = new PostTradeAuditConfig
                        {
                            Enabled = true,
                            DataDir = auditDirB,
                            EodDropDir = dropDirB,
                        },
                    },
                },
            };

            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();

            // Drives both listener termination AND the per-channel EOD
            // CSV export chain (issue #330 PR-3).
            host.TriggerDailyReset("test");

            var date = yesterday.ToString("yyyy-MM-dd");
            var csvA = Path.Combine(dropDirA, "91", date, "fills.csv");
            var csvB = Path.Combine(dropDirB, "92", date, "fills.csv");
            Assert.True(File.Exists(csvA), $"expected {csvA}");
            Assert.True(File.Exists(csvA + ".done"));
            Assert.True(File.Exists(csvB), $"expected {csvB}");
            Assert.True(File.Exists(csvB + ".done"));

            // 1 row + header / 2 rows + header.
            Assert.Equal(2, File.ReadAllLines(csvA).Length);
            Assert.Equal(3, File.ReadAllLines(csvB).Length);

            await host.StopAsync();
        }
        finally
        {
            TempDirs.TryDelete(auditDirA);
            TempDirs.TryDelete(dropDirA);
            TempDirs.TryDelete(auditDirB);
            TempDirs.TryDelete(dropDirB);
        }
    }

    [Fact]
    public async Task TriggerDailyReset_SkipsChannelsWithoutEodDropConfigured()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var auditDir = Path.Combine(Path.GetTempPath(), "b3-dr-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(auditDir);
            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
            using (var w = new FileAuditLogWriter(auditDir, channelNumber: 93))
                w.OnTrade(MakeFill(1, NanosForDate(yesterday)));

            // Audit enabled, eodDropDir empty → no export trigger registered.
            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
                Channels =
                {
                    new ChannelConfig
                    {
                        ChannelNumber = 93,
                        IncrementalGroup = "239.255.93.93",
                        IncrementalPort = 30193,
                        Ttl = 0,
                        InstrumentsFile = instrumentsPath,
                        PostTradeAudit = new PostTradeAuditConfig
                        {
                            Enabled = true,
                            DataDir = auditDir,
                            EodDropDir = "",
                        },
                    },
                },
            };

            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();

            host.TriggerDailyReset("test"); // must not throw

            await host.StopAsync();
        }
        finally
        {
            TempDirs.TryDelete(auditDir);
        }
    }

    [Fact]
    public async Task TriggerDailyReset_SwallowsMissingAuditLog_AndStillProcessesOtherChannels()
    {
        var eqtPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var drvPath = TestPaths.ResolveRepoFile("config/instruments-drv.json");
        // Channel A has NO audit log written → export should swallow.
        // Channel B has yesterday's audit log → export must still succeed.
        var auditDirA = Path.Combine(Path.GetTempPath(), "b3-dr-audit-a-" + Guid.NewGuid().ToString("N"));
        var dropDirA = Path.Combine(Path.GetTempPath(), "b3-dr-drop-a-" + Guid.NewGuid().ToString("N"));
        var auditDirB = Path.Combine(Path.GetTempPath(), "b3-dr-audit-b-" + Guid.NewGuid().ToString("N"));
        var dropDirB = Path.Combine(Path.GetTempPath(), "b3-dr-drop-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(auditDirA);
            Directory.CreateDirectory(auditDirB);
            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
            using (var w = new FileAuditLogWriter(auditDirB, channelNumber: 95))
                w.OnTrade(MakeFill(1, NanosForDate(yesterday)));

            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
                Channels =
                {
                    new ChannelConfig
                    {
                        ChannelNumber = 94,
                        IncrementalGroup = "239.255.94.94",
                        IncrementalPort = 30194,
                        Ttl = 0,
                        InstrumentsFile = eqtPath,
                        PostTradeAudit = new PostTradeAuditConfig
                        {
                            Enabled = true,
                            DataDir = auditDirA,
                            EodDropDir = dropDirA,
                        },
                    },
                    new ChannelConfig
                    {
                        ChannelNumber = 95,
                        IncrementalGroup = "239.255.95.95",
                        IncrementalPort = 30195,
                        Ttl = 0,
                        InstrumentsFile = drvPath,
                        PostTradeAudit = new PostTradeAuditConfig
                        {
                            Enabled = true,
                            DataDir = auditDirB,
                            EodDropDir = dropDirB,
                        },
                    },
                },
            };

            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();

            host.TriggerDailyReset("test");

            var date = yesterday.ToString("yyyy-MM-dd");
            // Channel 94: no audit log → no CSV produced.
            Assert.False(File.Exists(Path.Combine(dropDirA, "94", date, "fills.csv")));
            // Channel 95: succeeded despite the sibling failure.
            Assert.True(File.Exists(Path.Combine(dropDirB, "95", date, "fills.csv")));

            await host.StopAsync();
        }
        finally
        {
            TempDirs.TryDelete(auditDirA);
            TempDirs.TryDelete(dropDirA);
            TempDirs.TryDelete(auditDirB);
            TempDirs.TryDelete(dropDirB);
        }
    }

    [Fact]
    public async Task AdminDailyReset_HttpEndpoint_AlsoTriggersEodExport()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var auditDir = Path.Combine(Path.GetTempPath(), "b3-dr-audit-" + Guid.NewGuid().ToString("N"));
        var dropDir = Path.Combine(Path.GetTempPath(), "b3-dr-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(auditDir);
            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
            using (var w = new FileAuditLogWriter(auditDir, channelNumber: 96))
                w.OnTrade(MakeFill(1, NanosForDate(yesterday)));

            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
                Http = new HttpConfig { Listen = "127.0.0.1:0" },
                Channels =
                {
                    new ChannelConfig
                    {
                        ChannelNumber = 96,
                        IncrementalGroup = "239.255.96.96",
                        IncrementalPort = 30196,
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

            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();

            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint}") };
            using var resp = await client.PostAsync("/admin/daily-reset", content: null);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            var date = yesterday.ToString("yyyy-MM-dd");
            Assert.True(File.Exists(Path.Combine(dropDir, "96", date, "fills.csv")));

            await host.StopAsync();
        }
        finally
        {
            TempDirs.TryDelete(auditDir);
            TempDirs.TryDelete(dropDir);
        }
    }
}
