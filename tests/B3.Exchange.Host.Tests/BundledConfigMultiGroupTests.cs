using B3.Exchange.Host;

namespace B3.Exchange.Host.Tests;

/// <summary>
/// Validates the bundled <c>config/exchange-simulator*.json</c> files
/// declare the EQT (channel 84) + DRV (channel 72) groups required by
/// the multi-group production topology (#225). Pure JSON-load smoke test
/// — does not start the listener.
/// </summary>
public class BundledConfigMultiGroupTests
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

    [Theory]
    [InlineData("config/exchange-simulator.json")]
    [InlineData("config/exchange-simulator.bridge.json")]
    [InlineData("config/exchange-simulator.soak.json")]
    public void BundledConfig_DeclaresBothEqtAndDrvChannels(string relPath)
    {
        var cfg = HostConfigLoader.Load(ResolveRepoFile(relPath));

        var channelNumbers = cfg.Channels.Select(c => c.ChannelNumber).ToHashSet();
        Assert.Contains((byte)84, channelNumbers);
        Assert.Contains((byte)72, channelNumbers);

        var instrumentFiles = cfg.Channels
            .Select(c => c.InstrumentsFile ?? string.Empty)
            .ToList();
        Assert.Contains(instrumentFiles, f => f.EndsWith("instruments-eqt.json", StringComparison.Ordinal));
        Assert.Contains(instrumentFiles, f => f.EndsWith("instruments-drv.json", StringComparison.Ordinal));

        // Per-channel UMDF L3 destinations must be disjoint to avoid
        // collapsing two groups onto one socket.
        var endpoints = cfg.Channels
            .Select(c => $"{c.IncrementalGroup}:{c.IncrementalPort}")
            .ToList();
        Assert.Equal(endpoints.Count, endpoints.Distinct().Count());
    }
}
