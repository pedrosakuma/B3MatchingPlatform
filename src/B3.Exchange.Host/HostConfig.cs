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
    /// Optional snapshot publisher configuration. When omitted, the channel
    /// publishes only the incremental feed; consumers connecting mid-session
    /// have no way to bootstrap the order book.
    /// </summary>
    [JsonPropertyName("snapshot")] public SnapshotChannelConfig? Snapshot { get; set; }
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
