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
    [JsonPropertyName("auth")] public AuthConfig Auth { get; set; } = new();
    [JsonPropertyName("firms")] public List<FirmConfig> Firms { get; set; } = new();
    [JsonPropertyName("sessions")] public List<SessionConfig> Sessions { get; set; } = new();
    [JsonPropertyName("http")] public HttpConfig? Http { get; set; }
    [JsonPropertyName("channels")] public List<ChannelConfig> Channels { get; set; } = new();
}

public sealed class TcpConfig
{
    [JsonPropertyName("listen")] public string Listen { get; set; } = "0.0.0.0:9876";
    /// <summary>
    /// DEPRECATED — pre-#67 single-tenant fallback. Used only when
    /// <c>firms[]</c> / <c>sessions[]</c> are empty so existing configs
    /// keep working. New configs should declare firms and sessions
    /// explicitly per <c>docs/B3-ENTRYPOINT-ARCHITECTURE.md</c> §8.
    /// </summary>
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

/// <summary>
/// Authentication mode. <c>devMode=true</c> bypasses access-key validation
/// during Negotiate (#42) — useful for local development / tests. Mixing
/// devMode with non-empty <c>sessions[].accessKey</c> values produces a
/// startup warning.
/// </summary>
public sealed class AuthConfig
{
    [JsonPropertyName("devMode")] public bool DevMode { get; set; }

    /// <summary>
    /// When true (default), the host wires the FIXP <c>Negotiate</c> and
    /// <c>Establish</c> validators so application messages received on a
    /// session before the handshake completes are rejected per spec
    /// §4.5.3.1 (<c>Terminate(Unnegotiated)</c> /
    /// <c>Terminate(NotEstablished)</c>). Set to <c>false</c> to run in
    /// legacy permissive mode used by integration tests / synthetic
    /// trader flows that do not yet implement the handshake.
    /// </summary>
    [JsonPropertyName("requireFixpHandshake")] public bool RequireFixpHandshake { get; set; } = true;
}

/// <summary>One firm (corretora). See architecture §4.1.</summary>
public sealed class FirmConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("enteringFirmCode")] public uint EnteringFirmCode { get; set; }
}

/// <summary>One session credential. See architecture §4.2.</summary>
public sealed class SessionConfig
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("firmId")] public string FirmId { get; set; } = "";
    [JsonPropertyName("accessKey")] public string AccessKey { get; set; } = "";
    [JsonPropertyName("allowedSourceCidrs")] public List<string>? AllowedSourceCidrs { get; set; }
    [JsonPropertyName("policy")] public SessionPolicyConfig? Policy { get; set; }
}

public sealed class SessionPolicyConfig
{
    [JsonPropertyName("throttleMessagesPerSecond")] public int ThrottleMessagesPerSecond { get; set; }
    [JsonPropertyName("keepAliveIntervalMs")] public int KeepAliveIntervalMs { get; set; } = 30_000;
    [JsonPropertyName("idleTimeoutMs")] public int IdleTimeoutMs { get; set; } = 30_000;
    [JsonPropertyName("testRequestGraceMs")] public int TestRequestGraceMs { get; set; } = 5_000;
    [JsonPropertyName("retransmitBufferSize")] public int RetransmitBufferSize { get; set; } = 10_000;
}

/// <summary>
/// Optional Kestrel-hosted HTTP endpoint exposing /health/live,
/// /health/ready, and /metrics. Omit the <c>http</c> object from the
/// config to disable HTTP entirely.
/// </summary>
public sealed class HttpConfig
{
    [JsonPropertyName("listen")] public string Listen { get; set; } = "0.0.0.0:8080";

    /// <summary>
    /// Liveness staleness threshold in milliseconds. /health/live returns
    /// 503 if any registered dispatcher has not ticked within this many
    /// milliseconds. Default 5000 (5s).
    /// </summary>
    [JsonPropertyName("livenessStaleMs")] public int LivenessStaleMs { get; set; } = 5000;
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
    /// UDP transport mode for the incremental, snapshot, and instrumentDef
    /// publishers. <c>multicast</c> (default) preserves legacy behaviour and
    /// expects <c>IncrementalGroup</c>/<c>Snapshot.Group</c>/
    /// <c>InstrumentDefinition.Group</c> to be IPv4 multicast addresses.
    /// <c>unicast</c> is the bridge-network-friendly mode introduced for
    /// docker-compose setups where multicast is not routable: the
    /// <c>*.Group</c> fields are then treated as DNS hostnames (e.g.
    /// <c>"marketdata"</c>) or unicast IPs and resolved at startup.
    /// </summary>
    [JsonPropertyName("transport")]
    [JsonConverter(typeof(UmdfTransportJsonConverter))]
    public UmdfTransport Transport { get; set; } = UmdfTransport.Multicast;

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

public enum UmdfTransport
{
    /// <summary>UDP multicast (legacy default; matches B3 production wire).</summary>
    Multicast,
    /// <summary>UDP unicast — destination is a DNS hostname or unicast IP.
    /// Used for docker-compose bridge-network setups.</summary>
    Unicast,
}

public sealed class UmdfTransportJsonConverter : JsonConverter<UmdfTransport>
{
    public override UmdfTransport Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return s?.ToLowerInvariant() switch
        {
            null or "" or "multicast" => UmdfTransport.Multicast,
            "unicast" => UmdfTransport.Unicast,
            _ => throw new JsonException($"unknown transport value '{s}' (expected: multicast|unicast)"),
        };
    }

    public override void Write(Utf8JsonWriter writer, UmdfTransport value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            UmdfTransport.Multicast => "multicast",
            UmdfTransport.Unicast => "unicast",
            _ => throw new JsonException($"unknown UmdfTransport value: {value}"),
        });
    }
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
