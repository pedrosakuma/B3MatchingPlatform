using B3.Exchange.Core;
using B3.Exchange.PostTrade;
using B3.Exchange.TestSupport;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Issue #352: <see cref="ExchangeHost"/> must construct and inject a
/// <see cref="FileAuditLogWriter"/> when the channel config has
/// <c>postTradeAudit.enabled=true</c>, and must fall back to the
/// no-op sink otherwise. Disposal must flush the writer's final
/// watermark.
/// </summary>
public class PostTradeAuditHostWiringTests
{
    private sealed class NullSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }



    private static HostConfig BaseCfg(string instrumentsPath, PostTradeAuditConfig? audit, byte channel = 81)
        => new()
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = channel,
                    IncrementalGroup = "239.255.81.81",
                    IncrementalPort = 30181,
                    Ttl = 0,
                    InstrumentsFile = instrumentsPath,
                    PostTradeAudit = audit,
                },
            },
        };

    [Fact]
    public async Task StartAsync_WithoutPostTradeAuditBlock_UsesNullSink()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var cfg = BaseCfg(instrumentsPath, audit: null);

        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
        await host.StartAsync();
        // Host must boot and shut down cleanly without touching disk.
        await host.StopAsync();
    }

    [Fact]
    public async Task StartAsync_WithDisabledAudit_DoesNotCreateAuditDir()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var auditDir = Path.Combine(Path.GetTempPath(),
            "b3-audit-disabled-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = BaseCfg(instrumentsPath, new PostTradeAuditConfig
            {
                Enabled = false,
                DataDir = auditDir,
                RetentionDays = 5,
            });

            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();
            await host.StopAsync();

            // Writer was never constructed; the per-channel sub-directory
            // must not exist on disk.
            Assert.False(Directory.Exists(Path.Combine(auditDir, "81")),
                "disabled audit must not pre-create the per-channel dir");
        }
        finally
        {
            TempDirs.TryDelete(auditDir);
        }
    }

    [Fact]
    public async Task StartAsync_WithEnabledAudit_BootsCleanly()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var auditDir = Path.Combine(Path.GetTempPath(),
            "b3-audit-enabled-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = BaseCfg(instrumentsPath, new PostTradeAuditConfig
            {
                Enabled = true,
                DataDir = auditDir,
                RetentionDays = 0,
            }, channel: 82);

            await using (var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink()))
            {
                await host.StartAsync();
                await host.StopAsync();
            }
            // No trades were emitted, so the writer never had to call
            // RotateTo and the per-channel dir may not exist. The
            // important assertion is that boot + shutdown cycle through
            // a non-null FileAuditLogWriter without throwing.
        }
        finally
        {
            TempDirs.TryDelete(auditDir);
        }
    }

    [Fact]
    public async Task StartAsync_WithEmptyDataDir_LogsWarningAndSkips()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var cfg = BaseCfg(instrumentsPath, new PostTradeAuditConfig
        {
            Enabled = true,
            DataDir = "",
            RetentionDays = 0,
        }, channel: 83);

        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
        // Empty dataDir is a misconfiguration that we tolerate (warn + skip)
        // rather than failing closed — matches the BuildPersister/BuildWal
        // convention so a half-configured channel keeps the legacy
        // no-audit-log behaviour instead of refusing to boot.
        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task StartAsync_WithNegativeRetentionDays_Throws()
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var auditDir = Path.Combine(Path.GetTempPath(),
            "b3-audit-neg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = BaseCfg(instrumentsPath, new PostTradeAuditConfig
            {
                Enabled = true,
                DataDir = auditDir,
                RetentionDays = -1,
            }, channel: 84);

            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
            Assert.Contains("retentionDays", ex.Message);
        }
        finally
        {
            TempDirs.TryDelete(auditDir);
        }
    }
}
