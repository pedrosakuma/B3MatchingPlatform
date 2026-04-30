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
