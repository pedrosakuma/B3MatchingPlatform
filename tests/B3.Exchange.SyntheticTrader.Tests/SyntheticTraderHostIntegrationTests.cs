using B3.Exchange.Host;
using B3.Exchange.Core;

namespace B3.Exchange.SyntheticTrader.Tests;

/// <summary>
/// Boots the in-proc <see cref="ExchangeHost"/> on loopback, connects a
/// real <see cref="EntryPointClient"/> (via <see cref="SyntheticTraderRunner"/>),
/// runs the trader for a few seconds, and asserts that trades happened.
///
/// This is the closest thing to a smoke test of the full synth-trader stack:
/// it exercises the TCP wire encoder, the host's inbound decoder, the
/// matching engine, the ER encoder, and the synth trader's ER decoder.
/// </summary>
public class SyntheticTraderHostIntegrationTests
{
    private const long Petr = 900_000_000_001L;

    private sealed class RecordingPacketSink : IUmdfPacketSink
    {
        private int _packetCount;

        public int PacketCount => Volatile.Read(ref _packetCount);

        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet)
            => Interlocked.Increment(ref _packetCount);
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
    public async Task SynthTrader_AgainstInProcHost_GeneratesTrades()
    {
        var sink = new RecordingPacketSink();
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
                    IncrementalPort = 30185,
                    Ttl = 0,
                    InstrumentsFile = ResolveRepoFile("config/instruments-eqt.json"),
                },
            },
        };

        await using var host = new ExchangeHost(hostCfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        // Synth trader: one MM + one noise-taker on PETR4. crossTicks must be
        // >= MM quoteSpacingTicks so noise orders are reliably marketable.
        var instCfg = new InstrumentConfig
        {
            SecurityId = Petr,
            TickSize = 100,
            LotSize = 100,
            InitialMidMantissa = 123_400,
            MidDriftProbability = 0.0,  // deterministic mid for the assertion
        };
        IReadOnlyList<IStrategy> strategies = new IStrategy[]
        {
            new MarketMakerStrategy("mm", levelsPerSide: 3, quoteSpacingTicks: 1, replaceDistanceTicks: 5, quantity: 100),
            new NoiseTakerStrategy("noise", orderProbability: 0.5, maxLotMultiple: 2, crossTicks: 2),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var client = await EntryPointClient.ConnectAsync(ep.Address.ToString(), ep.Port, ct: cts.Token);
        await using var runner = new SyntheticTraderRunner(
            client,
            new[] { (instCfg, strategies) },
            new Random(1234),
            TimeSpan.FromMilliseconds(20),
            ct: cts.Token);
        runner.Start();

        // Run until we see a handful of trades (or timeout).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline && runner.TradesObserved < 5)
            await Task.Delay(50);

        Assert.True(runner.OrdersSent > 0, "expected the synth trader to send orders");
        Assert.True(runner.TradesObserved >= 1,
            $"expected at least one trade ER, got {runner.TradesObserved} (orders sent={runner.OrdersSent})");
        Assert.True(sink.PacketCount > 0, "expected multicast packets to be published by the host");

        await client.DisposeAsync();
    }
}
