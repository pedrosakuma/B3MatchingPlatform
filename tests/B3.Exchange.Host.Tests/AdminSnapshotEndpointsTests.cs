using System.Net;
using System.Net.Http;
using B3.Exchange.Core;
using B3.Exchange.TestSupport;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Issue #271: tests for the /admin/channels/{ch}/snapshot/* HTTP
/// endpoints. Covers GET (load from disk + JSON), POST force (alias of
/// snapshot-now), POST reset (delete files + WAL, requires force flag),
/// and POST validate (TryValidateSnapshot on the persisted snapshot).
/// </summary>
public class AdminSnapshotEndpointsTests
{
    private static (HostConfig cfg, string dataDir) BuildConfig(string testName)
    {
        var instrumentsPath = TestPaths.ResolveRepoFile("config/instruments-eqt.json");
        var dataDir = Path.Combine(Path.GetTempPath(), "b3-admin-tests-" + testName + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var cfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Http = new HttpConfig { Listen = "127.0.0.1:0", LivenessStaleMs = 60000 },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 91,
                    IncrementalGroup = "239.255.42.91",
                    IncrementalPort = 30191,
                    Ttl = 0,
                    InstrumentsFile = instrumentsPath,
                    Persistence = new PersistenceConfig
                    {
                        DataDir = dataDir,
                        Wal = new WalConfig { Enabled = true, FsyncPerWrite = false },
                    },
                },
            },
        };
        return (cfg, dataDir);
    }

    private sealed class NullSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }


    [Fact]
    public async Task GetSnapshot_WhenNoneExists_Returns204()
    {
        var (cfg, dataDir) = BuildConfig(nameof(GetSnapshot_WhenNoneExists_Returns204));
        try
        {
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint!}") };

            using var resp = await client.GetAsync("/admin/channels/91/snapshot");
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
        finally { TempDirs.TryDelete(dataDir); }
    }

    [Fact]
    public async Task ForceSnapshot_ThenGet_ReturnsJsonAndValidatesOk()
    {
        var (cfg, dataDir) = BuildConfig(nameof(ForceSnapshot_ThenGet_ReturnsJsonAndValidatesOk));
        try
        {
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint!}") };

            using var force = await client.PostAsync("/admin/channels/91/snapshot/force", content: null);
            Assert.Equal(HttpStatusCode.Accepted, force.StatusCode);

            // Snapshot capture is async on the dispatch thread; poll
            // briefly until the file appears.
            string? snapshotJson = null;
            for (int i = 0; i < 50 && snapshotJson is null; i++)
            {
                using var get = await client.GetAsync("/admin/channels/91/snapshot");
                if (get.StatusCode == HttpStatusCode.OK)
                {
                    snapshotJson = await get.Content.ReadAsStringAsync();
                    break;
                }
                await Task.Delay(50);
            }
            Assert.NotNull(snapshotJson);
            Assert.Contains("\"ChannelNumber\":91", snapshotJson);

            using var validate = await client.PostAsync("/admin/channels/91/snapshot/validate", content: null);
            Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
            var body = await validate.Content.ReadAsStringAsync();
            Assert.StartsWith("ok ", body);
        }
        finally { TempDirs.TryDelete(dataDir); }
    }

    [Fact]
    public async Task Reset_WithoutForceFlag_Returns400()
    {
        var (cfg, dataDir) = BuildConfig(nameof(Reset_WithoutForceFlag_Returns400));
        try
        {
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint!}") };

            using var resp = await client.PostAsync("/admin/channels/91/snapshot/reset", content: null);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        finally { TempDirs.TryDelete(dataDir); }
    }

    [Fact]
    public async Task Reset_WithForceFlag_DeletesSnapshotFiles()
    {
        var (cfg, dataDir) = BuildConfig(nameof(Reset_WithForceFlag_DeletesSnapshotFiles));
        try
        {
            await using (var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink()))
            {
                await host.StartAsync();
                using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint!}") };

                using var force = await client.PostAsync("/admin/channels/91/snapshot/force", content: null);
                Assert.Equal(HttpStatusCode.Accepted, force.StatusCode);

                // Wait for snapshot file to appear.
                for (int i = 0; i < 50; i++)
                {
                    if (Directory.GetFiles(dataDir, "channel-91.snapshot*").Length > 0) break;
                    await Task.Delay(50);
                }
                Assert.NotEmpty(Directory.GetFiles(dataDir, "channel-91.snapshot*"));

                using var reset = await client.PostAsync("/admin/channels/91/snapshot/reset?force=true", content: null);
                Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
                var body = await reset.Content.ReadAsStringAsync();
                Assert.Contains("files-removed=", body);

                Assert.Empty(Directory.GetFiles(dataDir, "channel-91.snapshot*"));
            }
        }
        finally { TempDirs.TryDelete(dataDir); }
    }

    [Fact]
    public async Task Validate_WhenNoSnapshot_Returns404()
    {
        var (cfg, dataDir) = BuildConfig(nameof(Validate_WhenNoSnapshot_Returns404));
        try
        {
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint!}") };

            using var resp = await client.PostAsync("/admin/channels/91/snapshot/validate", content: null);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { TempDirs.TryDelete(dataDir); }
    }

    [Fact]
    public async Task UnknownChannel_AllEndpoints_Return404()
    {
        var (cfg, dataDir) = BuildConfig(nameof(UnknownChannel_AllEndpoints_Return404));
        try
        {
            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            await host.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://{host.HttpEndpoint!}") };

            using var get = await client.GetAsync("/admin/channels/200/snapshot");
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

            using var force = await client.PostAsync("/admin/channels/200/snapshot/force", content: null);
            Assert.Equal(HttpStatusCode.NotFound, force.StatusCode);

            using var reset = await client.PostAsync("/admin/channels/200/snapshot/reset?force=true", content: null);
            Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);
        }
        finally { TempDirs.TryDelete(dataDir); }
    }

}
