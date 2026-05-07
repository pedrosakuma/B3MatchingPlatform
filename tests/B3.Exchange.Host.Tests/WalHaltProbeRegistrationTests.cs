using B3.Exchange.Core;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Issue #294 follow-up: the WAL-halt readiness probe must register
/// BEFORE <c>HttpServer</c> snapshots <c>_probes</c>, otherwise
/// <c>RegisterReadinessProbe</c> throws because <c>_http</c> is set.
/// Regression test: a host with both HTTP enabled and at least one
/// channel running <see cref="WalAppendFailurePolicy.Halt"/> must boot
/// cleanly.
/// </summary>
public class WalHaltProbeRegistrationTests
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
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task StartAsync_WithWalHaltAndHttpEnabled_BootsCleanly()
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
        var dataDir = Path.Combine(Path.GetTempPath(),
            "b3-walhalt-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
                Http = new HttpConfig { Listen = "127.0.0.1:0", LivenessStaleMs = 60000 },
                Channels =
                {
                    new ChannelConfig
                    {
                        ChannelNumber = 92,
                        IncrementalGroup = "239.255.42.92",
                        IncrementalPort = 30192,
                        Ttl = 0,
                        InstrumentsFile = instrumentsPath,
                        Persistence = new PersistenceConfig
                        {
                            DataDir = dataDir,
                            Wal = new WalConfig
                            {
                                Enabled = true,
                                FsyncPerWrite = false,
                                OnAppendFailure = "halt",
                            },
                        },
                    },
                },
            };

            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            // Pre-fix: this would throw InvalidOperationException because
            // RegisterReadinessProbe runs after _http is constructed.
            await host.StartAsync();

            // Probe must observe an initially-healthy channel.
            Assert.NotNull(host.HttpEndpoint);
            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint!}") };
            using var resp = await client.GetAsync("/health/ready");
            Assert.True(resp.IsSuccessStatusCode);
        }
        finally { TryDeleteDir(dataDir); }
    }
}
