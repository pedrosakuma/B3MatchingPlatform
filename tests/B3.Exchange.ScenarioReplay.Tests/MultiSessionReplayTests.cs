using B3.Exchange.Core;
using B3.Exchange.Host;
using B3.Exchange.SyntheticTrader;
using B3.Exchange.SyntheticTrader.Fixp;

namespace B3.Exchange.ScenarioReplay.Tests;

/// <summary>
/// End-to-end test for multi-session replay (#115): boots an
/// <see cref="ExchangeHost"/> with two firms / two sessions, opens one
/// FIXP-handshaken <see cref="EntryPointClient"/> per session, drives a
/// crossing script through <see cref="ReplayRunner"/>, and asserts both
/// sides see ER_Trade tagged with their session name.
/// </summary>
public class MultiSessionReplayTests
{
    private const long Petr = 900_000_000_001L;

    private sealed class CountingSink : IUmdfPacketSink
    {
        public int Count;
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) => Interlocked.Increment(ref Count);
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
    public async Task TwoSessions_CrossingScript_LandsErTradesOnBothSides()
    {
        var sink = new CountingSink();
        var hostCfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = true, DevMode = true },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Firms = new()
            {
                new FirmConfig { Id = "firmA", Name = "Firm A", EnteringFirmCode = 7 },
                new FirmConfig { Id = "firmB", Name = "Firm B", EnteringFirmCode = 8 },
            },
            Sessions = new()
            {
                new SessionConfig { SessionId = "100", FirmId = "firmA" },
                new SessionConfig { SessionId = "200", FirmId = "firmB" },
            },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 84,
                    IncrementalGroup = "239.255.42.84",
                    IncrementalPort = 30192,
                    Ttl = 0,
                    InstrumentsFile = ResolveRepoFile("config/instruments-eqt.json"),
                },
            },
        };

        await using var host = new ExchangeHost(hostCfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var clientA = await EntryPointClient.ConnectAsync(
            ep.Address.ToString(), ep.Port,
            new FixpClientOptions { SessionId = "100", EnteringFirm = 7, HandshakeTimeout = TimeSpan.FromSeconds(5) },
            ct: cts.Token);
        await using var clientB = await EntryPointClient.ConnectAsync(
            ep.Address.ToString(), ep.Port,
            new FixpClientOptions { SessionId = "200", EnteringFirm = 8, HandshakeTimeout = TimeSpan.FromSeconds(5) },
            ct: cts.Token);

        var sw = new StringWriter();
        long t = 0;
        using var capture = new CaptureWriter(sw, () => Interlocked.Increment(ref t));

        var sessions = new Dictionary<string, EntryPointClient>
        {
            ["firmA"] = clientA,
            ["firmB"] = clientB,
        };
        var runner = new ReplayRunner(sessions, "firmA", new SystemClock(speed: 10.0), capture);

        // firmA posts a resting bid; firmB hits it with a marketable IOC sell.
        // clOrdId reuse across sessions is allowed (issue note: the gateway
        // namespaces orders by (firm, clOrdId)).
        var script = new[]
        {
            new ScriptEvent(0,  ScriptEventKind.New, 1, Petr, Side.Buy,  OrderType.Limit, Tif.Day, 100, 320000, 0, 1, "firmA"),
            new ScriptEvent(20, ScriptEventKind.New, 1, Petr, Side.Sell, OrderType.Limit, Tif.IOC, 100, 320000, 0, 2, "firmB"),
        };

        await runner.RunAsync(script, cts.Token);

        // Wait for ER_Trade on BOTH sessions. Poll until both sides have
        // observed their own trade ER, not just any trade ER (the previous
        // condition broke as soon as the first side's trade landed, which
        // races the aggressor's recv loop — see #146).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var snapLines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool aTrade = snapLines.Any(l => l.Contains("\"src\":\"er\"") && l.Contains("\"session\":\"firmA\"") && l.Contains("\"execType\":\"trade\""));
            bool bTrade = snapLines.Any(l => l.Contains("\"src\":\"er\"") && l.Contains("\"session\":\"firmB\"") && l.Contains("\"execType\":\"trade\""));
            if (aTrade && bTrade)
                break;
            await Task.Delay(50, cts.Token);
        }

        capture.Dispose();
        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains(lines, l => l.Contains("\"session\":\"firmA\"") && l.Contains("submit_new") && l.Contains("clOrdId=1"));
        Assert.Contains(lines, l => l.Contains("\"session\":\"firmB\"") && l.Contains("submit_new") && l.Contains("clOrdId=1"));

        // Each side must observe its own ER_Trade tagged with the right session.
        Assert.Contains(lines, l => l.Contains("\"src\":\"er\"") && l.Contains("\"session\":\"firmA\"") && l.Contains("\"execType\":\"trade\""));
        Assert.Contains(lines, l => l.Contains("\"src\":\"er\"") && l.Contains("\"session\":\"firmB\"") && l.Contains("\"execType\":\"trade\""));

        // ER_New for the resting bid must be tagged firmA, not firmB.
        Assert.Contains(lines, l => l.Contains("\"src\":\"er\"") && l.Contains("\"session\":\"firmA\"") && l.Contains("\"execType\":\"new\""));
    }

    [Fact]
    public async Task EventWithoutSession_RoutesToDefault_BackCompat()
    {
        var sink = new CountingSink();
        var hostCfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = false },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 84,
                    IncrementalGroup = "239.255.42.84",
                    IncrementalPort = 30193,
                    Ttl = 0,
                    InstrumentsFile = ResolveRepoFile("config/instruments-eqt.json"),
                },
            },
        };

        await using var host = new ExchangeHost(hostCfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = await EntryPointClient.ConnectAsync(ep.Address.ToString(), ep.Port);

        var sw = new StringWriter();
        long t = 0;
        using var capture = new CaptureWriter(sw, () => Interlocked.Increment(ref t));

        var sessions = new Dictionary<string, EntryPointClient> { ["only"] = client };
        var runner = new ReplayRunner(sessions, "only", new SystemClock(speed: 10.0), capture);

        var script = new[]
        {
            // No "session" field — should route to "only".
            new ScriptEvent(0, ScriptEventKind.New, 99, Petr, Side.Buy, OrderType.Limit, Tif.Day, 100, 320000, 0, 1),
        };

        await runner.RunAsync(script, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (sw.ToString().Contains("\"execType\":\"new\""))
                break;
            await Task.Delay(50, cts.Token);
        }

        capture.Dispose();
        var snapshot = sw.ToString();
        Assert.Contains("\"session\":\"only\"", snapshot);
        Assert.Contains("\"execType\":\"new\"", snapshot);
    }
}
