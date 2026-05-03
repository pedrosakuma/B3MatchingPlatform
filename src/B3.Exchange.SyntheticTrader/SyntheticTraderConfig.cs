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

    /// <summary>
    /// Optional FIXP block. When present the trader performs a full
    /// Negotiate+Establish handshake on connect and runs the session
    /// layer (sequence numbers, heartbeat, Terminate on dispose). When
    /// omitted the trader stays in legacy "raw business frames" mode for
    /// back-compat with hosts that have <c>auth.requireFixpHandshake=false</c>.
    /// </summary>
    [JsonPropertyName("fixp")] public FixpConfig? Fixp { get; set; }
}

/// <summary>
/// FIXP session layer configuration. The <c>sessionId</c> must match a
/// <c>sessions[].sessionId</c> entry in the host config and obey the
/// gateway's decimal-uint32 rule (no leading zeros, &gt; 0).
/// </summary>
public sealed class FixpConfig
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("accessKey")] public string AccessKey { get; set; } = "";
    [JsonPropertyName("keepAliveIntervalMs")] public uint KeepAliveIntervalMs { get; set; } = 5000;
    [JsonPropertyName("cancelOnDisconnect")] public bool CancelOnDisconnect { get; set; }
    [JsonPropertyName("retransmitOnGap")] public bool RetransmitOnGap { get; set; } = true;
    [JsonPropertyName("clientAppName")] public string ClientAppName { get; set; } = "synth";
    [JsonPropertyName("clientAppVersion")] public string ClientAppVersion { get; set; } = "1.0";
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
    /// <summary>One of: <c>marketMaker</c>, <c>noiseTaker</c>,
    /// <c>meanReverting</c>, <c>momentum</c>, <c>sweeper</c>,
    /// <c>newsShock</c>.</summary>
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

    // MeanReverting / Momentum / Sweeper
    /// <summary>EWMA smoothing factor for <c>meanReverting</c> (0,1].</summary>
    [JsonPropertyName("alpha")] public double Alpha { get; set; } = 0.1;
    /// <summary>Tick-distance from the EWMA at which <c>meanReverting</c> fires.</summary>
    [JsonPropertyName("entryThresholdTicks")] public int EntryThresholdTicks { get; set; } = 3;
    /// <summary>Per-tick mid-step (in ticks) at which <c>momentum</c> fires.</summary>
    [JsonPropertyName("triggerTicks")] public int TriggerTicks { get; set; } = 2;
    /// <summary>Lots per order for <c>meanReverting</c> / <c>momentum</c>.</summary>
    [JsonPropertyName("lotsPerOrder")] public long LotsPerOrder { get; set; } = 1;
    /// <summary>Per-tick fire probability for <c>sweeper</c> [0,1].</summary>
    [JsonPropertyName("triggerProbability")] public double TriggerProbability { get; set; } = 0.05;
    /// <summary>Lots per sweep order; size <c>sweepLots * lotSize</c>.</summary>
    [JsonPropertyName("sweepLots")] public long SweepLots { get; set; } = 10;

    // NewsShock (issue #117)
    /// <summary>Mean ticks-between-shocks (used directly when configured in
    /// ticks); when <see cref="MeanIntervalMs"/> is set, takes precedence.</summary>
    [JsonPropertyName("meanIntervalMs")] public int MeanIntervalMs { get; set; } = 60000;
    [JsonPropertyName("jitterMs")] public int JitterMs { get; set; }
    [JsonPropertyName("shockDurationMs")] public int ShockDurationMs { get; set; } = 1500;
    [JsonPropertyName("fadeDurationMs")] public int FadeDurationMs { get; set; } = 3000;
    [JsonPropertyName("levelsToSweep")] public int LevelsToSweep { get; set; } = 3;
    [JsonPropertyName("burstQtyLots")] public long BurstQtyLots { get; set; } = 5;
    [JsonPropertyName("directionBias")] public double DirectionBias { get; set; } = 0.5;
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

    public static IStrategy BuildStrategy(StrategyConfig sc, int tickIntervalMs) => sc.Kind switch
    {
        "marketMaker" => new MarketMakerStrategy(
            string.IsNullOrEmpty(sc.Name) ? "mm" : sc.Name,
            sc.LevelsPerSide, sc.QuoteSpacingTicks, sc.ReplaceDistanceTicks, sc.Quantity),
        "noiseTaker" => new NoiseTakerStrategy(
            string.IsNullOrEmpty(sc.Name) ? "noise" : sc.Name,
            sc.OrderProbability, sc.MaxLotMultiple, sc.CrossTicks),
        "meanReverting" => new MeanRevertingStrategy(
            string.IsNullOrEmpty(sc.Name) ? "meanrev" : sc.Name,
            sc.Alpha, sc.EntryThresholdTicks, sc.CrossTicks, sc.LotsPerOrder),
        "momentum" => new MomentumStrategy(
            string.IsNullOrEmpty(sc.Name) ? "momo" : sc.Name,
            sc.TriggerTicks, sc.CrossTicks, sc.LotsPerOrder),
        "sweeper" => new SweeperStrategy(
            string.IsNullOrEmpty(sc.Name) ? "sweep" : sc.Name,
            sc.TriggerProbability, sc.SweepLots, sc.CrossTicks),
        "newsShock" => new NewsShockStrategy(
            string.IsNullOrEmpty(sc.Name) ? "shock" : sc.Name,
            tickIntervalMs,
            sc.MeanIntervalMs, sc.JitterMs,
            sc.ShockDurationMs, sc.FadeDurationMs,
            sc.LevelsToSweep, sc.BurstQtyLots, sc.DirectionBias),
        _ => throw new InvalidOperationException($"unknown strategy kind '{sc.Kind}'"),
    };
}
