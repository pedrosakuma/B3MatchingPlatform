using B3.Exchange.Core;
using B3.Exchange.Host;
using B3.Exchange.SyntheticTrader;

namespace B3.Exchange.ScenarioReplay.Tests;

/// <summary>
/// End-to-end test: spin up an <see cref="ExchangeHost"/> on loopback,
/// connect via <see cref="EntryPointClient"/>, drive a small replay script
/// through <see cref="ReplayRunner"/>, and assert that the resulting tape
/// contains the expected ER frames.
/// </summary>
public class EndToEndReplayTests
{
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

    private sealed class CountingSink : IUmdfPacketSink
    {
        public int Count;
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Interlocked.Increment(ref Count);
    }

    [Fact]
    public async Task RunsScript_AgainstHost_AndCapturesExecReports()
    {
        var instrumentsPath = ResolveRepoFile("config/instruments-eqt.json");
        var sink = new CountingSink();
        var cfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
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

        await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        var sw = new StringWriter();
        long t = 0;
        using var capture = new CaptureWriter(sw, () => Interlocked.Increment(ref t));

        await using var client = await EntryPointClient.ConnectAsync(ep.Address.ToString(), ep.Port);

        // securityId from instruments-eqt.json: PETR4 = 900_000_000_001 (lotSize 100).
        const long secId = 900_000_000_001;
        var script = new[]
        {
            new ScriptEvent(0,   ScriptEventKind.New, 1001, secId, Side.Buy,  OrderType.Limit, Tif.Day, 100, 320000, 0, 1),
            new ScriptEvent(10,  ScriptEventKind.New, 1002, secId, Side.Sell, OrderType.Limit, Tif.IOC, 100, 320000, 0, 2),
        };

        // Real-time SystemClock with high speed so the 10ms between events
        // becomes a tiny real-time wait yet still gives the dispatcher time
        // to publish ER_New(1001) before the IOC sell arrives.
        var realClock = new SystemClock(speed: 10.0);
        var runner = new ReplayRunner(client, realClock, capture);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runner.RunAsync(script, cts.Token);

        // Wait for trailing ER frames to arrive.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            if (sw.ToString().Contains("\"execType\":\"trade\"")) break;
        }

        capture.Dispose();
        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, l => l.Contains("\"src\":\"evt\"") && l.Contains("script_start"));
        Assert.Contains(lines, l => l.Contains("\"src\":\"evt\"") && l.Contains("submit_new") && l.Contains("clOrdId=1001"));
        Assert.Contains(lines, l => l.Contains("\"execType\":\"new\"") && l.Contains("\"clOrdId\":1001"));
        Assert.Contains(lines, l => l.Contains("\"execType\":\"trade\""));
    }
}

