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
    [JsonPropertyName("dailyReset")] public DailyResetConfig? DailyReset { get; set; }
    [JsonPropertyName("shutdown")] public ShutdownConfig Shutdown { get; set; } = new();
    [JsonPropertyName("channels")] public List<ChannelConfig> Channels { get; set; } = new();
}

/// <summary>
/// Graceful-shutdown tunables (issue #171 / A7). On SIGTERM the host
/// flips its readiness probe NOT_READY, stops accepting connections,
/// then waits up to <see cref="DrainGraceMs"/> for the per-channel
/// dispatcher inbound queues to drain (polled every
/// <see cref="DrainPollMs"/>) before broadcasting <c>Terminate(Finished)</c>
/// to every live FIXP session and tearing the listener down.
/// </summary>
public sealed class ShutdownConfig
{
    /// <summary>Maximum wall-clock time to wait for inbound queues to
    /// drain before forcing the Terminate broadcast. Default 5 s.</summary>
    [JsonPropertyName("drainGraceMs")] public int DrainGraceMs { get; set; } = 5_000;

    /// <summary>How often the drain loop polls per-channel queue depth.
    /// Default 50 ms. Smaller = more responsive shutdown, more CPU.</summary>
    [JsonPropertyName("drainPollMs")] public int DrainPollMs { get; set; } = 50;
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

    /// <summary>
    /// Per-session inbound sliding-window throttle (issue #56 / GAP-20,
    /// guidelines §4.9). Omit (or set both fields to 0) to disable
    /// throttling. Defaults to 100 application messages per 1000 ms when
    /// the JSON object is present without explicit overrides.
    /// </summary>
    [JsonPropertyName("throttle")] public ThrottleConfig? Throttle { get; set; }
}

/// <summary>
/// Configuration block for the per-session inbound sliding-window
/// throttle (issue #56 / GAP-20, guidelines §4.9). On violation the
/// session emits <c>BusinessMessageReject("Throttle limit exceeded")</c>
/// and stays open. FIXP session-layer messages bypass the throttle.
/// </summary>
public sealed class ThrottleConfig
{
    /// <summary>Sliding window length in milliseconds. Default 1000 ms.</summary>
    [JsonPropertyName("timeWindowMs")] public int TimeWindowMs { get; set; } = 1_000;

    /// <summary>Maximum application messages accepted per window. Default 100.</summary>
    [JsonPropertyName("maxMessages")] public int MaxMessages { get; set; } = 100;
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

/// <summary>
/// Daily trading-day rollover (#GAP-09 / issue #47, spec §4.5.1). When
/// <see cref="Enabled"/> is true the host schedules a once-per-day timer
/// that fires at <see cref="Schedule"/> in <see cref="Timezone"/> and
/// terminates every live session, forcing each client to reconnect with
/// a fresh <c>Negotiate</c>+<c>Establish(nextSeqNo=1)</c>. Operators can
/// also trigger the rollover on demand via <c>POST /admin/daily-reset</c>
/// regardless of this flag.
/// </summary>
public sealed class DailyResetConfig
{
    /// <summary>When false (default) the scheduled timer is not armed —
    /// the operator endpoint still works.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    /// <summary>Local-time-of-day "HH:mm" (24h) at which the rollover
    /// fires. Defaults to <c>"18:00"</c>, the B3 cash-equity post-trading
    /// boundary.</summary>
    [JsonPropertyName("schedule")] public string Schedule { get; set; } = "18:00";

    /// <summary>IANA time zone identifier in which <see cref="Schedule"/>
    /// is interpreted. Defaults to <c>"America/Sao_Paulo"</c>; the
    /// platform's <c>TimeZoneInfo</c> store must recognize the value
    /// (Linux containers ship with the IANA database).</summary>
    [JsonPropertyName("timezone")] public string Timezone { get; set; } = "America/Sao_Paulo";
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

    /// <summary>
    /// Optional UMDF retransmit ring sizing (issue #216 / Onda L · L3a).
    /// When omitted, the default ring size is used. Set
    /// <c>bufferSize=0</c> to disable retransmit buffering entirely (the
    /// dispatcher will not retain published packets and any future
    /// retransmit responder will report an empty window).
    /// </summary>
    [JsonPropertyName("umdfRetransmit")] public UmdfRetransmitConfig? UmdfRetransmit { get; set; }

    /// <summary>
    /// Optional chaos injection on the incremental UDP sink (issue #119).
    /// When omitted or all probabilities are 0, the sink is unmodified.
    /// Used to exercise the consumer's recovery paths (snapshot bootstrap,
    /// gap detection) without external network-shaping tools.
    /// </summary>
    [JsonPropertyName("chaos")] public ChaosConfigJson? Chaos { get; set; }
}

/// <summary>
/// JSON projection of <see cref="B3.Exchange.Core.ChaosConfig"/>. Lives here
/// (rather than re-using the Core type directly) so the JSON shape is owned
/// by the host config layer and the Core decorator stays POCO-free.
/// </summary>
public sealed class ChaosConfigJson
{
    [JsonPropertyName("dropProbability")] public double DropProbability { get; set; }
    [JsonPropertyName("duplicateProbability")] public double DuplicateProbability { get; set; }
    [JsonPropertyName("reorderProbability")] public double ReorderProbability { get; set; }
    [JsonPropertyName("reorderMaxLagPackets")] public int ReorderMaxLagPackets { get; set; } = 3;
    [JsonPropertyName("seed")] public int Seed { get; set; }

    public B3.Exchange.Core.ChaosConfig ToCore() => new()
    {
        DropProbability = DropProbability,
        DuplicateProbability = DuplicateProbability,
        ReorderProbability = ReorderProbability,
        ReorderMaxLagPackets = ReorderMaxLagPackets,
        Seed = Seed,
    };
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

/// <summary>
/// Per-channel UMDF retransmit ring sizing (issue #216 / Onda L · L3a).
/// </summary>
public sealed class UmdfRetransmitConfig
{
    /// <summary>
    /// Ring capacity in packets. <c>null</c> uses the default
    /// (<see cref="B3.Exchange.Core.RetransmitBufferDefaults.UmdfRingCapacity"/>);
    /// <c>0</c> disables the ring entirely (no buffer is allocated and
    /// the dispatcher publishes without retaining packets).
    /// </summary>
    [JsonPropertyName("bufferSize")] public int? BufferSize { get; set; }
}
