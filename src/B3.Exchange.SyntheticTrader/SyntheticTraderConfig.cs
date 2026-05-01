using System.Text.Json;
using System.Text.Json.Serialization;

namespace B3.Exchange.SyntheticTrader;

/// <summary>
/// Top-level synthetic-trader configuration. One process can drive any
/// number of instruments; each instrument gets its own (strategy, params)
/// pair. The host endpoint is a single TCP connection per process.
/// </summary>
public sealed class SyntheticTraderConfig
{
    [JsonPropertyName("host")] public HostEndpointConfig Host { get; set; } = new();
    [JsonPropertyName("firm")] public uint Firm { get; set; } = 1;

    /// <summary>RNG seed. 0 means "use a non-deterministic time-based seed".</summary>
    [JsonPropertyName("seed")] public int Seed { get; set; } = 1;

    /// <summary>Tick interval in milliseconds. The runner ticks all strategies on this cadence.</summary>
    [JsonPropertyName("tickIntervalMs")] public int TickIntervalMs { get; set; } = 100;

    [JsonPropertyName("instruments")] public List<InstrumentConfig> Instruments { get; set; } = new();
}

public sealed class HostEndpointConfig
{
    [JsonPropertyName("host")] public string Host { get; set; } = "127.0.0.1";
    [JsonPropertyName("port")] public int Port { get; set; } = 9876;
}

public sealed class InstrumentConfig
{
    [JsonPropertyName("securityId")] public long SecurityId { get; set; }

    /// <summary>Tick size in mantissa units (i.e. /10000). 100 == 0.01 BRL.</summary>
    [JsonPropertyName("tickSize")] public long TickSize { get; set; } = 100;

    [JsonPropertyName("lotSize")] public long LotSize { get; set; } = 100;

    /// <summary>Initial midprice mantissa. The midprice random-walks each tick.</summary>
    [JsonPropertyName("initialMidMantissa")] public long InitialMidMantissa { get; set; }

    /// <summary>Probability per tick that the mid drifts by ±1 tick (random walk).</summary>
    [JsonPropertyName("midDriftProbability")] public double MidDriftProbability { get; set; } = 0.1;

    [JsonPropertyName("strategies")] public List<StrategyConfig> Strategies { get; set; } = new();
}

public sealed class StrategyConfig
{
    /// <summary>One of: <c>marketMaker</c>, <c>noiseTaker</c>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    // MarketMaker
    [JsonPropertyName("levelsPerSide")] public int LevelsPerSide { get; set; } = 3;
    [JsonPropertyName("quoteSpacingTicks")] public int QuoteSpacingTicks { get; set; } = 1;
    [JsonPropertyName("replaceDistanceTicks")] public int ReplaceDistanceTicks { get; set; } = 5;
    [JsonPropertyName("quantity")] public long Quantity { get; set; } = 100;

    // NoiseTaker
    [JsonPropertyName("orderProbability")] public double OrderProbability { get; set; } = 0.2;
    [JsonPropertyName("maxLotMultiple")] public int MaxLotMultiple { get; set; } = 3;
    [JsonPropertyName("crossTicks")] public int CrossTicks { get; set; } = 1;
}

public static class SyntheticTraderConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SyntheticTraderConfig Load(string path)
    {
        using var stream = File.OpenRead(path);
        var cfg = JsonSerializer.Deserialize<SyntheticTraderConfig>(stream, Options)
            ?? throw new InvalidOperationException($"empty synthetic-trader config at {path}");
        if (cfg.Instruments.Count == 0)
            throw new InvalidOperationException("SyntheticTraderConfig.Instruments is empty");
        return cfg;
    }

    public static IStrategy BuildStrategy(StrategyConfig sc) => sc.Kind switch
    {
        "marketMaker" => new MarketMakerStrategy(
            string.IsNullOrEmpty(sc.Name) ? "mm" : sc.Name,
            sc.LevelsPerSide, sc.QuoteSpacingTicks, sc.ReplaceDistanceTicks, sc.Quantity),
        "noiseTaker" => new NoiseTakerStrategy(
            string.IsNullOrEmpty(sc.Name) ? "noise" : sc.Name,
            sc.OrderProbability, sc.MaxLotMultiple, sc.CrossTicks),
        _ => throw new InvalidOperationException($"unknown strategy kind '{sc.Kind}'"),
    };
}
