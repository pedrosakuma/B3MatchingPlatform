using B3.Exchange.Core;
using B3.Exchange.Host;
using B3.Exchange.SyntheticTrader.Fixp;

namespace B3.Exchange.SyntheticTrader.Tests.Fixp;

/// <summary>
/// End-to-end check that <see cref="FixpClient"/> negotiates a session
/// against a real <see cref="ExchangeHost"/> with
/// <c>auth.requireFixpHandshake=true</c>, sends a business order with a
/// proper <c>InboundBusinessHeader.msgSeqNum</c>, receives the
/// corresponding ExecutionReport, and shuts down with a graceful
/// <c>Terminate</c>.
/// </summary>
public class FixpClientHandshakeTests
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
    public async Task FixpClient_HandshakeAndOrderRoundTrip()
    {
        var sink = new CountingSink();
        var hostCfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = true, DevMode = true },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Firms = new()
            {
                new FirmConfig { Id = "firmA", Name = "Firm A", EnteringFirmCode = 7 },
            },
            Sessions = new()
            {
                new SessionConfig { SessionId = "100", FirmId = "firmA" },
            },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 84,
                    IncrementalGroup = "239.255.42.84",
                    IncrementalPort = 30190,
                    Ttl = 0,
                    InstrumentsFile = ResolveRepoFile("config/instruments-eqt.json"),
                },
            },
        };

        await using var host = new ExchangeHost(hostCfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fixpOptions = new FixpClientOptions
        {
            SessionId = "100",
            EnteringFirm = 7,
            KeepAliveIntervalMillis = 2000,
            CancelOnDisconnect = false,
            RetransmitOnGap = false,
            HandshakeTimeout = TimeSpan.FromSeconds(5),
        };
        await using var client = await EntryPointClient.ConnectAsync(
            ep.Address.ToString(), ep.Port, fixpOptions, ct: cts.Token);

        Assert.NotNull(client.Fixp);
        Assert.Equal(FixpClientState.Established, client.Fixp!.State);
        Assert.Equal(100u, client.Fixp.SessionIdNumeric);

        // Send a single NewOrder on PETR4 and wait for ER_New (or ER_Reject).
        var erTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnNew += _ => erTcs.TrySetResult(true);
        client.OnReject += _ => erTcs.TrySetResult(true);

        bool ok = client.SendNewOrder(
            clOrdId: 12345,
            securityId: Petr,
            side: OrderSide.Buy,
            type: OrderTypeIntent.Limit,
            tif: OrderTifIntent.Day,
            qty: 100,
            priceMantissa: 100_000);
        Assert.True(ok, "SendNewOrder should accept the frame");

        var completed = await Task.WhenAny(erTcs.Task, Task.Delay(TimeSpan.FromSeconds(5), cts.Token));
        Assert.Same(erTcs.Task, completed);
        Assert.True(await erTcs.Task, "expected ER_New or ER_Reject from gateway");

        // FIXP outbound seq must have advanced past 1 after one business send.
        Assert.True(client.Fixp.NextOutboundSeqNo > 1u,
            $"expected nextOutboundSeqNo > 1, got {client.Fixp.NextOutboundSeqNo}");
    }

    [Fact]
    public async Task FixpClient_RejectsUnknownSession()
    {
        var sink = new CountingSink();
        var hostCfg = new HostConfig
        {
            Auth = new AuthConfig { RequireFixpHandshake = true, DevMode = true },
            Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
            Firms = new() { new FirmConfig { Id = "firmA", Name = "Firm A", EnteringFirmCode = 7 } },
            Sessions = new() { new SessionConfig { SessionId = "100", FirmId = "firmA" } },
            Channels =
            {
                new ChannelConfig
                {
                    ChannelNumber = 84,
                    IncrementalGroup = "239.255.42.84",
                    IncrementalPort = 30191,
                    Ttl = 0,
                    InstrumentsFile = ResolveRepoFile("config/instruments-eqt.json"),
                },
            },
        };

        await using var host = new ExchangeHost(hostCfg, packetSinkFactory: _ => sink);
        await host.StartAsync();
        var ep = host.TcpEndpoint!;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fixpOptions = new FixpClientOptions
        {
            SessionId = "9999",  // not registered
            EnteringFirm = 7,
            HandshakeTimeout = TimeSpan.FromSeconds(3),
        };

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var _ = await EntryPointClient.ConnectAsync(
                ep.Address.ToString(), ep.Port, fixpOptions, ct: cts.Token);
        });
    }
}
