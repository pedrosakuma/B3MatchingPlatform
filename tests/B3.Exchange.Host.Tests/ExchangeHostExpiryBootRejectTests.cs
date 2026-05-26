using B3.Exchange.Core;
using B3.Exchange.Instruments;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// OPT-03 / ADR 0013 — host-level integration test confirming that an
/// option series whose expirationDate has already passed is rejected
/// at <see cref="ExchangeHost.StartAsync"/> time. The actual rejection
/// lives in <see cref="InstrumentLoader"/> (T-1); this test pins the
/// guarantee that the host bootstrap surfaces it instead of silently
/// loading the stale instrument.
/// </summary>
public class ExchangeHostExpiryBootRejectTests
{
    private sealed class NullSink : IUmdfPacketSink
    {
        public void Publish(byte channelNumber, ReadOnlySpan<byte> packet) { }
    }

    private static string WriteInstrumentsJson(DateOnly expirationDate, string scratchDir)
    {
        Directory.CreateDirectory(scratchDir);
        var path = Path.Combine(scratchDir, "instruments-opt-expired.json");
        var json = $$"""
        [
          {
            "symbol": "PETRC120",
            "securityId": 900000099999,
            "tickSize": "0.01",
            "lotSize": 1,
            "minPx": "0.01",
            "maxPx": "999999.99",
            "currency": "BRL",
            "isin": "BRPETRC1ZZZ0",
            "securityType": "OPT",
            "strikePrice": "12.00",
            "expirationDate": "{{expirationDate:yyyy-MM-dd}}",
            "putOrCall": "Call",
            "exerciseStyle": "American",
            "underlyingSecurityId": 900000000001,
            "underlyingSymbol": "PETR4",
            "contractMultiplier": "100"
          }
        ]
        """;
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task StartAsync_ThrowsWhenOptionExpirationDateIsInThePast()
    {
        // Write the instruments file under the test's bin directory
        // (the repo-rule blocks /tmp). The directory is auto-cleaned
        // by the temp helper below.
        var scratch = Path.Combine(AppContext.BaseDirectory,
            "opt-t3-boot-reject-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instrumentsPath = WriteInstrumentsJson(
                expirationDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1),
                scratchDir: scratch);

            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
                Channels =
                {
                    new ChannelConfig
                    {
                        ChannelNumber = 91,
                        IncrementalGroup = "239.255.42.91",
                        IncrementalPort = 30191,
                        Ttl = 0,
                        InstrumentsFile = instrumentsPath,
                    },
                },
            };

            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            var ex = await Assert.ThrowsAsync<InstrumentConfigException>(() => host.StartAsync());
            Assert.Contains("expirationDate must be today or later", ex.Message);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_AcceptsOptionExpiringToday()
    {
        var scratch = Path.Combine(AppContext.BaseDirectory,
            "opt-t3-boot-accept-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instrumentsPath = WriteInstrumentsJson(
                expirationDate: DateOnly.FromDateTime(DateTime.UtcNow),
                scratchDir: scratch);

            var cfg = new HostConfig
            {
                Auth = new AuthConfig { RequireFixpHandshake = false },
                Tcp = new TcpConfig { Listen = "127.0.0.1:0", EnteringFirm = 7 },
                Channels =
                {
                    new ChannelConfig
                    {
                        ChannelNumber = 92,
                        IncrementalGroup = "239.255.42.92",
                        IncrementalPort = 30192,
                        Ttl = 0,
                        InstrumentsFile = instrumentsPath,
                    },
                },
            };

            await using var host = new ExchangeHost(cfg, packetSinkFactory: _ => new NullSink());
            // Should not throw — boundary case: expiry == today is admitted.
            await host.StartAsync();
            await host.StopAsync();
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }
}
