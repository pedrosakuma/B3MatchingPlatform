using System.Text.Json;
using System.Text.Json.Serialization;

namespace B3.Exchange.Host;

/// <summary>
/// Top-level host configuration loaded from <c>exchange-simulator.json</c>.
/// One channel per UMDF channel; instruments are partitioned across channels
/// (an instrument belongs to exactly one channel).
/// </summary>
public sealed class HostConfig
{
    [JsonPropertyName("tcp")] public TcpConfig Tcp { get; set; } = new();
    [JsonPropertyName("channels")] public List<ChannelConfig> Channels { get; set; } = new();
}

public sealed class TcpConfig
{
    [JsonPropertyName("listen")] public string Listen { get; set; } = "0.0.0.0:9876";
    [JsonPropertyName("enteringFirm")] public uint EnteringFirm { get; set; } = 1;
    /// <summary>Server-side heartbeat (Sequence) interval in milliseconds.
    /// A heartbeat is only emitted when no other outbound traffic has been
    /// sent within this window. Default: 30 s.</summary>
    [JsonPropertyName("heartbeatIntervalMs")] public int HeartbeatIntervalMs { get; set; } = 30_000;
    /// <summary>Inbound silence in milliseconds before the server emits a
    /// Sequence probe (FIXP TestRequest equivalent). Default: 30 s.</summary>
    [JsonPropertyName("idleTimeoutMs")] public int IdleTimeoutMs { get; set; } = 30_000;
    /// <summary>Additional grace window after the probe before the session is
    /// closed for inactivity. Default: 5 s.</summary>
    [JsonPropertyName("testRequestGraceMs")] public int TestRequestGraceMs { get; set; } = 5_000;
}

public sealed class ChannelConfig
{
    [JsonPropertyName("channelNumber")] public byte ChannelNumber { get; set; }
    [JsonPropertyName("incrementalGroup")] public string IncrementalGroup { get; set; } = "";
    [JsonPropertyName("incrementalPort")] public int IncrementalPort { get; set; }
    [JsonPropertyName("localInterface")] public string? LocalInterface { get; set; }
    [JsonPropertyName("ttl")] public byte Ttl { get; set; } = 1;
    [JsonPropertyName("instruments")] public string InstrumentsFile { get; set; } = "";

    /// <summary>
    /// Self-trade prevention policy applied by this channel's matching engine.
    /// Defaults to <c>none</c> (preserves legacy behaviour). Accepted values
    /// (case-insensitive): <c>none</c>, <c>cancel-aggressor</c>,
    /// <c>cancel-resting</c>, <c>cancel-both</c>.
    /// </summary>
    [JsonPropertyName("selfTradePrevention")]
    [JsonConverter(typeof(SelfTradePreventionJsonConverter))]
    public B3.Exchange.Matching.SelfTradePrevention SelfTradePrevention { get; set; }
        = B3.Exchange.Matching.SelfTradePrevention.None;

    /// <summary>
    /// Optional snapshot publisher configuration. When omitted, the channel
    /// publishes only the incremental feed; consumers connecting mid-session
    /// have no way to bootstrap the order book.
    /// </summary>
    [JsonPropertyName("snapshot")] public SnapshotChannelConfig? Snapshot { get; set; }

    /// <summary>
    /// Optional: enables the periodic <c>SecurityDefinition_12</c> publisher
    /// on a dedicated InstrumentDef multicast group. When omitted, no
    /// instrument definitions are published for this channel.
    /// </summary>
    [JsonPropertyName("instrumentDefinition")] public InstrumentDefinitionConfig? InstrumentDefinition { get; set; }
}

public sealed class SelfTradePreventionJsonConverter : JsonConverter<B3.Exchange.Matching.SelfTradePrevention>
{
    public override B3.Exchange.Matching.SelfTradePrevention Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return s?.ToLowerInvariant() switch
        {
            null or "" or "none" => B3.Exchange.Matching.SelfTradePrevention.None,
            "cancel-aggressor" => B3.Exchange.Matching.SelfTradePrevention.CancelAggressor,
            "cancel-resting" => B3.Exchange.Matching.SelfTradePrevention.CancelResting,
            "cancel-both" => B3.Exchange.Matching.SelfTradePrevention.CancelBoth,
            _ => throw new JsonException($"unknown selfTradePrevention value '{s}' (expected: none|cancel-aggressor|cancel-resting|cancel-both)"),
        };
    }

    public override void Write(Utf8JsonWriter writer, B3.Exchange.Matching.SelfTradePrevention value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            B3.Exchange.Matching.SelfTradePrevention.None => "none",
            B3.Exchange.Matching.SelfTradePrevention.CancelAggressor => "cancel-aggressor",
            B3.Exchange.Matching.SelfTradePrevention.CancelResting => "cancel-resting",
            B3.Exchange.Matching.SelfTradePrevention.CancelBoth => "cancel-both",
            _ => throw new JsonException($"unknown SelfTradePrevention value: {value}"),
        });
    }
}

/// <summary>
/// Per-channel snapshot publisher config. Multicast group/port MUST be
/// distinct from the incremental channel — consumers subscribe to both
/// independently.
/// </summary>
public sealed class SnapshotChannelConfig
{
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("ttl")] public byte? Ttl { get; set; }
    [JsonPropertyName("cadenceMs")] public int CadenceMs { get; set; } = 1000;
    [JsonPropertyName("maxEntriesPerChunk")] public int? MaxEntriesPerChunk { get; set; }
}

public sealed class InstrumentDefinitionConfig
{
    /// <summary>
    /// UMDF channel number stamped on the InstrumentDef PacketHeader.
    /// Defaults to the parent channel's <c>ChannelNumber</c> when 0.
    /// </summary>
    [JsonPropertyName("channelNumber")] public byte ChannelNumber { get; set; }

    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("localInterface")] public string? LocalInterface { get; set; }
    [JsonPropertyName("ttl")] public byte Ttl { get; set; } = 1;

    /// <summary>Cycle period in milliseconds. Defaults to 5000 (5 s).</summary>
    [JsonPropertyName("cadenceMs")] public int CadenceMs { get; set; } = 5_000;
}

public static class HostConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static HostConfig Load(string path)
    {
        using var stream = File.OpenRead(path);
        var cfg = JsonSerializer.Deserialize<HostConfig>(stream, Options)
            ?? throw new InvalidOperationException($"empty host config at {path}");
        if (cfg.Channels.Count == 0)
            throw new InvalidOperationException("HostConfig.Channels is empty");
        return cfg;
    }
}
