using System.Text.Json;
using System.Text.Json.Serialization;
using B3.Exchange.Gateway;

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
    [JsonPropertyName("phaseScheduler")] public PhaseSchedulerConfig? PhaseScheduler { get; set; }
    [JsonPropertyName("shutdown")] public ShutdownConfig Shutdown { get; set; } = new();
    [JsonPropertyName("maxOpenOrdersPerFirm")] public int MaxOpenOrdersPerFirm { get; set; } = 100_000;
    [JsonPropertyName("channels")] public List<ChannelConfig> Channels { get; set; } = new();

    /// <summary>
    /// Issue #288: optional metrics-rendering tunables (per-session label
    /// gates, etc). Omit to keep all defaults. Today only controls
    /// <see cref="MetricsConfig.FixpSessionLabelsEnabled"/>.
    /// </summary>
    [JsonPropertyName("metrics")] public MetricsConfig? Metrics { get; set; }
}

/// <summary>
/// Issue #288: gates for metrics that carry per-session labels. Per-session
/// cardinality can blow up scrape size on deployments where firms cycle
/// through many short-lived FIXP sessions; opt in only when the operator
/// explicitly accepts that cost.
/// </summary>
public sealed class MetricsConfig
{
    /// <summary>
    /// When <c>true</c>, the Prometheus renderer emits the
    /// <c>exch_fixp_retransmit_buffer_utilization</c> and
    /// <c>exch_fixp_retransmit_buffer_full_percent</c> gauges with
    /// per-session labels. Default <c>false</c>; the aggregate counters
    /// (<c>exch_fixp_retransmit_buffer_evictions_total</c>,
    /// <c>exch_fixp_passive_er_buffered_total</c>) are always emitted.
    /// </summary>
    [JsonPropertyName("fixpSessionLabelsEnabled")] public bool FixpSessionLabelsEnabled { get; set; }
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
    /// Maximum absolute skew tolerated for inbound application
    /// InboundBusinessHeader.sendingTime. Default: 5 s. Set to 0 to disable.
    /// </summary>
    [JsonPropertyName("sendingTimeSkewToleranceMs")] public int SendingTimeSkewToleranceMs { get; set; } = 5_000;

    /// <summary>Decode-time maximum accepted OrderQty/NewQuantity. Larger
    /// values are rejected with ER_Reject OrdRejReason=99 (Other).</summary>
    [JsonPropertyName("maxOrderQty")] public long MaxOrderQty { get; set; } = InboundFatFingerOptions.DefaultMaxOrderQty;

    /// <summary>Decode-time maximum accepted limit price mantissa (/10000).
    /// Default 1e15 = 100,000,000,000.0000, intentionally far above normal B3
    /// cash-equity and listed-derivative price ranges while still catching
    /// corrupted 64-bit payloads.</summary>
    [JsonPropertyName("maxPrice")] public long MaxPrice { get; set; } = InboundFatFingerOptions.DefaultMaxPriceMantissa;

    /// <summary>Optional dynamic price band in percent, relative to the last
    /// trade price for the instrument. Null disables the band; with no last
    /// trade reference the decoder accepts and leaves downstream validation
    /// unchanged.</summary>
    [JsonPropertyName("priceBandPercent")] public decimal? PriceBandPercent { get; set; }

    /// <summary>
    /// Per-session inbound sliding-window throttle (issue #56 / GAP-20,
    /// guidelines §4.9). Omit (or set both fields to 0) to disable
    /// throttling. Defaults to 100 application messages per 1000 ms when
    /// the JSON object is present without explicit overrides.
    /// </summary>
    [JsonPropertyName("throttle")] public ThrottleConfig? Throttle { get; set; }

    /// <summary>
    /// Issue #405: per-FIXP-session resync persistence directory.
    /// When non-empty, two artifacts are written under this directory:
    /// (a) an append-only journal of every outbound business
    /// frame at <c>{dir}/journal/session-{sessionId:x8}/segment-*.log</c>,
    /// and (b) a periodically-rewritten envelope-state snapshot
    /// (SessionVerId, LastIncomingSeqNo, outbound MsgSeqNum, CoD
    /// parameters) at <c>{dir}/state/session-{sessionId:x8}.state</c>.
    /// On boot the host rehydrates <c>SessionClaimRegistry</c> from the
    /// snapshots so peers reconnecting with their original SessionVerId
    /// are accepted (no <c>UNNEGOTIATED</c> reject), and subsequent
    /// <c>RetransmitRequest</c>s for seqs beyond the in-memory ring are
    /// served from the journal — satisfying the
    /// <c>EstablishmentAck.serverFlow=RECOVERABLE</c> contract across
    /// host restarts. The directory is created on demand. Empty (the
    /// default) disables resync persistence and the simulator falls
    /// back to ephemeral FIXP state.
    /// Property name retained from the #289 prototype for config
    /// back-compat; semantics widened by #405.
    /// </summary>
    [JsonPropertyName("retransmitPersistenceDir")] public string? RetransmitPersistenceDir { get; set; }

    /// <summary>
    /// Maximum on-disk bytes retained per FIXP retransmit journal session.
    /// Default 256 MiB. When exceeded, only segments at or below the last
    /// peer-confirmed ACK watermark may be deleted; otherwise the journal is
    /// allowed to grow and metrics/logs alert the operator.
    /// </summary>
    [JsonPropertyName("maxJournalBytes")] public long MaxJournalBytes { get; set; } = 256L * 1024L * 1024L;

    /// <summary>
    /// Maximum age, in hours, of the oldest retained FIXP retransmit journal
    /// entry. Default 24h. Rotation is conservative and never deletes entries
    /// the peer may need on reconnect.
    /// </summary>
    [JsonPropertyName("maxJournalRetentionHours")] public double MaxJournalRetentionHours { get; set; } = 24.0;
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
    private int _maxOrderRatePerSecond = 200;

    [JsonPropertyName("maxOrderRatePerSecond")]
    public int MaxOrderRatePerSecond
    {
        get => _maxOrderRatePerSecond;
        set => _maxOrderRatePerSecond = value;
    }

    [JsonPropertyName("throttleMessagesPerSecond")]
    public int ThrottleMessagesPerSecond
    {
        get => _maxOrderRatePerSecond;
        set => _maxOrderRatePerSecond = value;
    }

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

/// <summary>
/// Issue #321: scheduled trading-phase transitions. When
/// <see cref="Enabled"/> is <c>true</c>, each <see cref="PhaseSchedulerGroup"/>
/// runs its <see cref="PhaseSchedulerGroup.Schedule"/> entries against the
/// listed instruments at the configured local time.
/// </summary>
public sealed record PhaseSchedulerConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("groups")] public IReadOnlyList<PhaseSchedulerGroup> Groups { get; init; } = Array.Empty<PhaseSchedulerGroup>();
}

/// <summary>
/// Issue #321: a named bundle of instruments + a list of scheduled
/// transitions (e.g. all equities, B3 cash-equity day plan).
/// </summary>
public sealed record PhaseSchedulerGroup
{
    [JsonPropertyName("name")] public string Name { get; init; } = "default";
    [JsonPropertyName("timezone")] public string Timezone { get; init; } = "America/Sao_Paulo";
    /// <summary>SecurityIds the schedule applies to. Empty list ⇒ all
    /// instruments routed by the host.</summary>
    [JsonPropertyName("instruments")] public IReadOnlyList<long> Instruments { get; init; } = Array.Empty<long>();
    [JsonPropertyName("schedule")] public IReadOnlyList<PhaseSchedulerEntry> Schedule { get; init; } = Array.Empty<PhaseSchedulerEntry>();
}

/// <summary>
/// Issue #321: one scheduled transition. <see cref="At"/> is HH:mm in the
/// owning <see cref="PhaseSchedulerGroup.Timezone"/>.
/// <see cref="Action"/> is either <c>"EnterPhase"</c> (plain SetPhase) or
/// <c>"UncrossAuction"</c> (Reserved→Open / FinalClosingCall→Close).
/// <see cref="Target"/> is a <c>TradingPhase</c> enum name
/// (e.g. <c>"Open"</c>).
/// </summary>
public sealed record PhaseSchedulerEntry
{
    [JsonPropertyName("at")] public string At { get; init; } = "";
    [JsonPropertyName("action")] public string Action { get; init; } = "EnterPhase";
    [JsonPropertyName("target")] public string Target { get; init; } = "";
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
    /// Optional periodic <c>PriceBand_22</c> cadence for the incremental feed.
    /// <c>0</c> (default) disables the publisher; a positive value schedules a
    /// dispatch-thread tick that emits one static price-band frame per
    /// configured instrument carrying <c>lowerPriceBand</c>/<c>upperPriceBand</c>.
    /// </summary>
    [JsonPropertyName("priceBandPublishIntervalMs")] public int PriceBandPublishIntervalMs { get; set; }

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

    /// <summary>
    /// Optional per-channel persistence (issue #260). When present, the
    /// dispatcher loads any existing snapshot from
    /// <see cref="PersistenceConfig.DataDir"/>/<c>channel-{N}.snapshot</c>
    /// at boot and persists a fresh snapshot after every command flush so
    /// a restart resumes with the working order book + RptSeq + counters
    /// intact. Omit to keep the legacy stateless boot.
    /// </summary>
    [JsonPropertyName("persistence")] public PersistenceConfig? Persistence { get; set; }

    /// <summary>
    /// Optional per-channel post-trade audit log (issue #329 / wiring
    /// follow-up #352). When present and <see cref="PostTradeAuditConfig.Enabled"/>
    /// is <c>true</c>, the dispatcher writes a per-day append-only
    /// audit record per <c>OnTrade</c> under
    /// <c>{DataDir}/{channel}/fills-YYYY-MM-DD.log</c> (plus a sparse
    /// <c>.idx</c> firm index) and persists a watermark sidecar so
    /// post-crash WAL replay can suppress duplicate audit emissions.
    /// Omit (or set <c>enabled=false</c>) to keep the legacy
    /// no-audit-log behaviour (<c>NullPostTradeSink</c>).
    /// </summary>
    [JsonPropertyName("postTradeAudit")] public PostTradeAuditConfig? PostTradeAudit { get; set; }
}

/// <summary>
/// Per-channel persistence config (issue #260). The host creates
/// <see cref="DataDir"/> if missing and writes one snapshot file per
/// channel. Operators must mount a durable volume at this path
/// (e.g. <c>/var/lib/b3matching</c>) for restart-safety to be meaningful.
/// </summary>
public sealed class PersistenceConfig
{
    [JsonPropertyName("dataDir")] public string DataDir { get; set; } = "";

    /// <summary>
    /// Optional snapshot throttling (issue #267). Absent or both fields
    /// zero means "persist after every command flush" — the PR #261
    /// default. Provide either <c>everyN</c> (commands), <c>minIntervalMs</c>
    /// (wall-clock), or both (whichever fires first triggers the persist).
    /// Operator commands always force-persist regardless of throttle.
    /// </summary>
    [JsonPropertyName("throttle")] public SnapshotThrottleConfig? Throttle { get; set; }

    /// <summary>
    /// Issue #268: opt-in async snapshot writer. When <c>true</c> the
    /// dispatcher captures the snapshot POCO on the loop thread (cheap)
    /// and hands it off to a dedicated writer thread for serialization
    /// and atomic write. Off by default so existing deployments keep
    /// the synchronous (zero-RPO) behaviour. Backpressure is
    /// last-write-wins — see exch_snapshot_dropped_by_backpressure_total.
    /// </summary>
    [JsonPropertyName("asyncWriter")] public bool AsyncWriter { get; set; }

    /// <summary>
    /// Issue #264: number of rolling snapshot generations kept on disk
    /// per channel. The persister round-robins across slots
    /// <c>0..generations-1</c> and picks the newest valid one on load,
    /// transparently falling back to older slots if the newest is
    /// corrupted. Defaults to
    /// <see cref="B3.Exchange.Persistence.FileChannelStatePersister.DefaultGenerations"/>
    /// when absent or zero.
    /// </summary>
    [JsonPropertyName("generations")] public int Generations { get; set; }

    /// <summary>
    /// Issue #269: optional Write-Ahead Log between snapshots. When
    /// <see cref="WalConfig.Enabled"/> is <c>true</c> the dispatcher
    /// appends one record per state-mutating command before the engine
    /// observes it and replays the surviving records on cold start —
    /// closing the gap between the most-recent snapshot and an
    /// unclean shutdown. Off by default so existing deployments keep
    /// the snapshot-only behaviour (and pay no per-command file IO).
    /// </summary>
    [JsonPropertyName("wal")] public WalConfig? Wal { get; set; }

    /// <summary>
    /// Issue #270: policy applied to <see cref="OrderOwnerSnapshot"/>
    /// entries whose <c>SessionValue</c> is not present in the host's
    /// firm/session registry at restore time. Accepted values:
    /// <c>"drop"</c> (default — log + metric, skip the owner; engine
    /// state still loads) or <c>"reject"</c> (treat as fatal restore
    /// error → channel fails closed). Case-insensitive. Absent ⇒
    /// <c>"drop"</c>.
    /// </summary>
    [JsonPropertyName("orphanSessionPolicy")] public string? OrphanSessionPolicy { get; set; }

    /// <summary>
    /// Issue #266: on-disk encoding the persister uses when WRITING
    /// snapshots. Accepted values: <c>"json"</c> (default — historical
    /// PR #261 behaviour) or <c>"binary"</c>. Case-insensitive. Both
    /// formats are always recognised on LOAD via magic-byte sniffing,
    /// so flipping this value (and restarting) gracefully migrates
    /// the channel forward — old slots in the previous format remain
    /// loadable until the rolling generations evict them. Picking
    /// <c>"binary"</c> targets the 5–10× size and serialize-CPU
    /// reduction documented in issue #266.
    /// </summary>
    [JsonPropertyName("format")] public string? Format { get; set; }
}

/// <summary>
/// Per-channel post-trade audit log config (issue #329 / wiring
/// follow-up #352). The audit log is an independent durability
/// domain from snapshots/WAL: it appends one record per
/// <c>OnTrade</c>, rolls daily by UTC business date, and is
/// intended to be retained for the compliance horizon (typically
/// 5 years for B3 fills). See RUNBOOK §7.8 for the on-disk layout
/// and watermark contract.
/// </summary>
public sealed class PostTradeAuditConfig
{
    /// <summary>Master switch. <c>false</c> (default) ⇒ no audit
    /// log files are written; the dispatcher uses
    /// <c>NullPostTradeSink</c>.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    /// <summary>Root directory for audit files. The writer creates
    /// <c>{DataDir}/{channelNumber}/</c> if missing. Operators must
    /// mount a durable volume separate from the WAL/snapshot volume
    /// when the compliance horizon exceeds the WAL retention.</summary>
    [JsonPropertyName("dataDir")] public string DataDir { get; set; } = "";

    /// <summary>Number of UTC days of audit log files to keep on
    /// disk. <c>0</c> (default) disables automatic retention — the
    /// host writes audit files but never deletes them, leaving
    /// retention to an external operator job. When set to a
    /// positive value, the host runs a daily timer that calls
    /// <c>FileAuditLogWriter.PruneOldDays(todayUtc, retentionDays)</c>;
    /// files for the currently-open day and the watermark sidecar
    /// are never pruned.</summary>
    [JsonPropertyName("retentionDays")] public int RetentionDays { get; set; }

    /// <summary>Root directory for EOD CSV drop files produced by
    /// <c>EodFillsExporter</c> (issue #330). Layout:
    /// <c>{EodDropDir}/{channelNumber}/{YYYY-MM-DD}/fills.csv</c> + sibling
    /// <c>fills.csv.done</c>. Empty (default) ⇒ the
    /// <c>POST /admin/post-trade/eod-export</c> endpoint returns 404 for
    /// this channel and the daily-reset auto-trigger (PR-3) skips it.
    /// Operators typically point this at a different mount than
    /// <see cref="DataDir"/> so downstream reconciliation jobs can drain
    /// the drop directory without racing the audit writer.</summary>
    [JsonPropertyName("eodDropDir")] public string EodDropDir { get; set; } = "";
}

/// <summary>
/// JSON projection of the per-channel Write-Ahead Log settings
/// (issue #269). The WAL file lives in the same
/// <see cref="PersistenceConfig.DataDir"/> as the snapshot files
/// (named <c>channel-{N}.wal</c>); a successful snapshot persist
/// truncates it.
/// </summary>
public sealed class WalConfig
{
    /// <summary>Master switch. <c>false</c> ⇒ no WAL file is opened
    /// or replayed — the channel behaves exactly as it did before
    /// issue #269.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    /// <summary>When <c>true</c> (default) every <c>Append</c> calls
    /// <c>fsync</c> before returning, giving zero-RPO at the cost of
    /// per-command disk latency. Set to <c>false</c> for higher
    /// throughput on workloads that tolerate losing the last few
    /// commands on a host crash.</summary>
    [JsonPropertyName("fsyncPerWrite")] public bool FsyncPerWrite { get; set; } = true;

    /// <summary>Issue #286: behaviour when <c>Append</c> throws.
    /// Default <c>continue</c> preserves the pre-#286 contract — the
    /// failure is logged + counted via
    /// <c>exch_wal_append_failures_total</c>, and the command runs
    /// against the engine even though it is not durable. Set to
    /// <c>halt</c> to refuse the command (no engine mutation, no
    /// UMDF emission, no ExecutionReport) and flip the host's
    /// readiness probe to NOT_READY on the first failure; the halt
    /// is sticky and clears only on host restart.</summary>
    [JsonPropertyName("onAppendFailure")] public string OnAppendFailure { get; set; } = "continue";

    /// <summary>
    /// Parses <see cref="OnAppendFailure"/> case-insensitively to
    /// the typed enum. Throws on unknown values so misconfiguration
    /// fails the boot rather than silently degrading to the wrong
    /// policy.
    /// </summary>
    public B3.Exchange.Core.WalAppendFailurePolicy ResolveOnAppendFailure() =>
        OnAppendFailure?.Trim().ToLowerInvariant() switch
        {
            null or "" or "continue" => B3.Exchange.Core.WalAppendFailurePolicy.Continue,
            "halt" => B3.Exchange.Core.WalAppendFailurePolicy.Halt,
            _ => throw new InvalidOperationException(
                $"persistence.wal.onAppendFailure: unknown value '{OnAppendFailure}' (expected 'continue' or 'halt')"),
        };

    /// <summary>
    /// Issue #291: optional cap (in bytes) on the on-disk WAL
    /// size. <c>0</c> (default) disables the cap, restoring the
    /// pre-#291 unbounded behaviour. When set, the dispatcher
    /// applies <see cref="OnFull"/> when the next append would
    /// exceed the cap; until a successful snapshot persist
    /// truncates the WAL the cap stays in force.
    /// </summary>
    [JsonPropertyName("maxBytes")] public long MaxBytes { get; set; }

    /// <summary>
    /// Issue #291: behaviour when <see cref="MaxBytes"/> would be
    /// exceeded. Accepted values: <c>"halt"</c> (default — refuse
    /// the command, mark the channel WAL-halted regardless of
    /// <see cref="OnAppendFailure"/>) or <c>"drop"</c> (silently
    /// skip the WAL write, log + bump
    /// <c>exch_wal_drops_on_full_total</c>, let the command run).
    /// Case-insensitive. Ignored when
    /// <see cref="MaxBytes"/> &lt;= 0.
    /// </summary>
    [JsonPropertyName("onFull")] public string OnFull { get; set; } = "halt";

    /// <summary>
    /// Issue #312 (Tier-2 perf): selects the WAL fsync strategy.
    /// <list type="bullet">
    ///   <item><c>"perWrite"</c> (default when <see cref="FsyncPerWrite"/>
    ///   is <c>true</c>) — every <c>Append</c> calls <c>fsync</c>
    ///   before returning. Zero RPO, per-command disk latency.</item>
    ///   <item><c>"groupCommit"</c> — appends are written + fsynced on
    ///   a background thread no more often than every
    ///   <c>groupCommitIntervalMs</c> milliseconds. The outbound
    ///   stack uses <c>WaitForDurable</c> to gate ER frames so
    ///   peers never observe state that is not yet on disk; this
    ///   amortises fsync cost across bursty workloads while keeping
    ///   the durable-before-observable contract intact.</item>
    /// </list>
    /// When omitted, the resolved mode comes from
    /// <see cref="FsyncPerWrite"/>: <c>true</c> ⇒ <c>perWrite</c>,
    /// <c>false</c> ⇒ <c>groupCommit</c>. Setting both
    /// <see cref="FsyncMode"/>=<c>perWrite</c> AND
    /// <see cref="FsyncPerWrite"/>=<c>false</c> (or the inverse) is
    /// flagged as a misconfiguration at boot.
    /// </summary>
    [JsonPropertyName("fsyncMode")] public string? FsyncMode { get; set; }

    /// <summary>
    /// Group-commit batching window in milliseconds. Ignored unless
    /// the resolved fsync mode is <c>groupCommit</c>. Defaults to
    /// <c>1</c> ms — small enough to bound RPO under steady load,
    /// large enough to amortise fsync syscall cost across the burst.
    /// </summary>
    [JsonPropertyName("groupCommitIntervalMs")] public int GroupCommitIntervalMs { get; set; } = 1;

    /// <summary>
    /// Parses <see cref="OnFull"/> case-insensitively to the typed
    /// enum. Throws on unknown values so misconfiguration fails
    /// the boot rather than silently degrading to the wrong
    /// policy.
    /// </summary>
    public B3.Exchange.Core.WalSizeCapPolicy ResolveOnFull() =>
        OnFull?.Trim().ToLowerInvariant() switch
        {
            null or "" or "halt" => B3.Exchange.Core.WalSizeCapPolicy.Halt,
            "drop" => B3.Exchange.Core.WalSizeCapPolicy.Drop,
            _ => throw new InvalidOperationException(
                $"persistence.wal.onFull: unknown value '{OnFull}' (expected 'halt' or 'drop')"),
        };

    /// <summary>
    /// Resolves <see cref="FsyncMode"/> + <see cref="FsyncPerWrite"/>
    /// into the typed <see cref="B3.Exchange.Core.WalFsyncMode"/>
    /// the WAL ctor expects. When <see cref="FsyncMode"/> is unset,
    /// derives from the legacy <see cref="FsyncPerWrite"/> flag so
    /// existing configs keep their pre-#312 behaviour. Throws on
    /// unknown values or contradictory combinations
    /// (<c>perWrite</c> + <see cref="FsyncPerWrite"/>=<c>false</c>,
    /// or <c>groupCommit</c> + <see cref="FsyncPerWrite"/>=<c>true</c>).
    /// </summary>
    public B3.Exchange.Core.WalFsyncMode ResolveFsyncMode()
    {
        var raw = FsyncMode?.Trim().ToLowerInvariant();
        switch (raw)
        {
            case null or "":
                return FsyncPerWrite
                    ? B3.Exchange.Core.WalFsyncMode.PerWrite
                    : B3.Exchange.Core.WalFsyncMode.GroupCommit;
            case "perwrite":
                if (!FsyncPerWrite)
                {
                    throw new InvalidOperationException(
                        "persistence.wal.fsyncMode='perWrite' contradicts persistence.wal.fsyncPerWrite=false; remove one of the two settings.");
                }
                return B3.Exchange.Core.WalFsyncMode.PerWrite;
            case "groupcommit":
                if (FsyncPerWrite && FsyncMode is not null)
                {
                    // FsyncPerWrite default is true; treat the explicit
                    // groupCommit override as authoritative and override
                    // the legacy flag transparently. Operators upgrading
                    // an old config only need to set fsyncMode.
                    return B3.Exchange.Core.WalFsyncMode.GroupCommit;
                }
                return B3.Exchange.Core.WalFsyncMode.GroupCommit;
            default:
                throw new InvalidOperationException(
                    $"persistence.wal.fsyncMode: unknown value '{FsyncMode}' (expected 'perWrite' or 'groupCommit').");
        }
    }
}

/// <summary>
/// JSON projection of <see cref="B3.Exchange.Core.SnapshotThrottlePolicy"/>.
/// </summary>
public sealed class SnapshotThrottleConfig
{
    [JsonPropertyName("everyN")] public int EveryNCommands { get; set; } = 1;
    [JsonPropertyName("minIntervalMs")] public int MinIntervalMs { get; set; }

    public B3.Exchange.Core.SnapshotThrottlePolicy ToPolicy() => new()
    {
        EveryNCommands = EveryNCommands,
        MinIntervalMs = MinIntervalMs,
    };
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
