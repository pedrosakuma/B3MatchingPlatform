using System.Diagnostics;
using System.Globalization;
using System.Text;
using B3.Exchange.Contracts;

namespace B3.Exchange.Core;

/// <summary>
/// Inbound command kinds counted by <c>exch_inbound_messages_total</c>
/// (issue #174). Bounded set; the label cardinality on the metric is
/// fixed.
/// </summary>
public enum InboundMessageKind
{
    New = 0, Cancel = 1, Replace = 2, Cross = 3, MassCancel = 4, DecodeError = 5,
}

/// <summary>
/// ExecutionReport kinds counted by <c>exch_execution_reports_total</c>
/// (issue #174). Distinguishes active vs passive sides of trades and
/// cancels because that's the breakdown SREs need to spot a runaway
/// passive replay.
/// </summary>
public enum ExecutionReportKind
{
    New = 0, Trade = 1, TradePassive = 2, Cancel = 3, CancelPassive = 4, Replace = 5, Reject = 6,
}

/// <summary>
/// UMDF feed kinds counted by <c>exch_umdf_packets_total</c> /
/// <c>exch_umdf_bytes_total</c> (issue #174). Three values, one per
/// physical multicast group the host publishes on.
/// </summary>
public enum UmdfFeedKind
{
    Incremental = 0, Snapshot = 1, InstrumentDef = 2,
}

/// <summary>
/// Per-channel atomic counters and gauges. All mutating methods are
/// designed to be called from the channel's single dispatch thread (no
/// internal locking; lock-free <see cref="Interlocked"/> primitives are
/// used so a metrics scrape from a separate thread sees consistent values).
/// </summary>
public sealed class ChannelMetrics
{
    public byte ChannelNumber { get; }

    private long _ordersIn;
    private long _packetsOut;
    private long _snapshotsEmitted;
    private long _instrumentDefsEmitted;
    private long _lastTickUnixMs;
    private long _chaosDropped;
    private long _chaosDuplicated;
    private long _chaosReordered;
    private long _dispatchQueueFull;
    private long _decodeErrors;
    private long _dispatcherCrashes;
    private long _publishErrors;
    private long _publishErrorsHostUnreachable;
    private long _publishErrorsMessageTooLarge;

    // Issue #173: latency histograms — Stopwatch.GetTimestamp() based,
    // lock-free per-bucket counters. ~150ns observation overhead.
    public LatencyHistogram DispatchWait { get; } = new();
    public LatencyHistogram EngineProcess { get; } = new();
    public LatencyHistogram OutboundEmit { get; } = new();
    public LatencyHistogram InboundDecode { get; } = new();

    // Issue #174: throughput counters — per-channel, labelled by a small
    // bounded set (msg_type / exec_type / feed). Indexed by the enum's
    // numeric value so increments are a single Interlocked op.
    private readonly long[] _inboundByKind = new long[InboundMessageKindNames.Length];
    private readonly long[] _execReportsByKind = new long[ExecutionReportKindNames.Length];
    private readonly long[] _packetsByFeed = new long[UmdfFeedKindNames.Length];
    private readonly long[] _bytesByFeed = new long[UmdfFeedKindNames.Length];

    internal static readonly string[] InboundMessageKindNames = new[]
    {
        "new", "cancel", "replace", "cross", "mass_cancel", "decode_error",
    };

    internal static readonly string[] ExecutionReportKindNames = new[]
    {
        "new", "trade", "trade_passive", "cancel", "cancel_passive", "replace", "reject",
    };

    internal static readonly string[] UmdfFeedKindNames = new[]
    {
        "incremental", "snapshot", "instrumentdef",
    };

    public void IncInboundMessage(InboundMessageKind kind)
        => Interlocked.Increment(ref _inboundByKind[(int)kind]);
    public void IncExecutionReport(ExecutionReportKind kind)
        => Interlocked.Increment(ref _execReportsByKind[(int)kind]);
    public void IncUmdfPacket(UmdfFeedKind feed, int bytes)
    {
        Interlocked.Increment(ref _packetsByFeed[(int)feed]);
        Interlocked.Add(ref _bytesByFeed[(int)feed], bytes);
    }

    internal long ReadInboundMessages(InboundMessageKind kind)
        => Interlocked.Read(ref _inboundByKind[(int)kind]);
    internal long ReadExecutionReports(ExecutionReportKind kind)
        => Interlocked.Read(ref _execReportsByKind[(int)kind]);
    internal long ReadUmdfPackets(UmdfFeedKind feed)
        => Interlocked.Read(ref _packetsByFeed[(int)feed]);
    internal long ReadUmdfBytes(UmdfFeedKind feed)
        => Interlocked.Read(ref _bytesByFeed[(int)feed]);

    public ChannelMetrics(byte channelNumber)
    {
        ChannelNumber = channelNumber;
    }

    public long OrdersIn => Interlocked.Read(ref _ordersIn);
    public long PacketsOut => Interlocked.Read(ref _packetsOut);
    public long SnapshotsEmitted => Interlocked.Read(ref _snapshotsEmitted);
    public long InstrumentDefsEmitted => Interlocked.Read(ref _instrumentDefsEmitted);
    public long LastTickUnixMs => Interlocked.Read(ref _lastTickUnixMs);
    public long ChaosDropped => Interlocked.Read(ref _chaosDropped);
    public long ChaosDuplicated => Interlocked.Read(ref _chaosDuplicated);
    public long ChaosReordered => Interlocked.Read(ref _chaosReordered);
    public long DispatchQueueFull => Interlocked.Read(ref _dispatchQueueFull);
    public long DecodeErrors => Interlocked.Read(ref _decodeErrors);
    public long DispatcherCrashes => Interlocked.Read(ref _dispatcherCrashes);
    public long PublishErrors => Interlocked.Read(ref _publishErrors);
    public long PublishErrorsHostUnreachable => Interlocked.Read(ref _publishErrorsHostUnreachable);
    public long PublishErrorsMessageTooLarge => Interlocked.Read(ref _publishErrorsMessageTooLarge);

    public void IncOrdersIn() => Interlocked.Increment(ref _ordersIn);
    public void IncPacketsOut() => Interlocked.Increment(ref _packetsOut);
    public void IncSnapshotsEmitted() => Interlocked.Increment(ref _snapshotsEmitted);
    public void IncInstrumentDefsEmitted() => Interlocked.Increment(ref _instrumentDefsEmitted);
    public void IncChaosDropped() => Interlocked.Increment(ref _chaosDropped);
    public void IncChaosDuplicated() => Interlocked.Increment(ref _chaosDuplicated);
    public void IncChaosReordered() => Interlocked.Increment(ref _chaosReordered);
    public void IncDispatchQueueFull() => Interlocked.Increment(ref _dispatchQueueFull);
    public void IncDecodeErrors() => Interlocked.Increment(ref _decodeErrors);
    public void IncDispatcherCrashes() => Interlocked.Increment(ref _dispatcherCrashes);
    public void IncPublishErrorSocketError() => Interlocked.Increment(ref _publishErrors);
    public void IncPublishErrorHostUnreachable() => Interlocked.Increment(ref _publishErrorsHostUnreachable);
    public void IncPublishErrorMessageTooLarge() => Interlocked.Increment(ref _publishErrorsMessageTooLarge);

    /// <summary>
    /// Heartbeat. Called from the dispatch thread on every loop wakeup so
    /// liveness probes can detect a stuck/dead dispatcher.
    /// </summary>
    public void RecordTick(long unixMs) => Interlocked.Exchange(ref _lastTickUnixMs, unixMs);
}

/// <summary>
/// Provider of per-session diagnostics for the operator surface
/// (issue #70). The host wires an implementation that snapshots active
/// <c>FixpSession</c>s at scrape time. Used by both the Prometheus
/// renderer (per-session series) and the <c>GET /sessions</c> JSON
/// endpoint.
/// </summary>
public interface ISessionMetricsProvider
{
    /// <summary>Snapshot every currently-known session as a
    /// <see cref="SessionDiagnostics"/>. Closed sessions are filtered
    /// by the implementation.</summary>
    IEnumerable<SessionDiagnostics> Sample();
}

/// <summary>
/// Diagnostic snapshot of a single FIXP session. The numeric
/// <paramref name="State"/> matches <c>FixpState</c>: 0=Idle,
/// 1=Negotiated, 2=Established, 3=Suspended, 4=Terminated.
/// <paramref name="AttachedTransportId"/> is <c>null</c> when the
/// session is Suspended (no attached TCP transport).
/// <paramref name="LastActivityAtMs"/> is Unix milliseconds; <c>0</c>
/// before the first inbound frame.
/// </summary>
public readonly record struct SessionDiagnostics(
    string SessionId,
    string FirmId,
    int State,
    ulong SessionVerId,
    uint OutboundSeq,
    uint InboundExpectedSeq,
    int RetxBufferDepth,
    long SendQueueDepth,
    string? AttachedTransportId,
    long LastActivityAtMs);

/// <summary>
/// Static identity for a participant (corretora). Mirror of the
/// <c>Firm</c> record in <c>B3.Exchange.Gateway</c>, exposed in
/// <c>B3.Exchange.Core</c> so the operator HTTP surface (issue #70) can
/// list firms without a Core→Gateway dependency.
/// </summary>
public readonly record struct FirmInfo(string Id, string Name, uint EnteringFirmCode);

/// <summary>
/// Central registry of channel metrics + a Prometheus text-format
/// renderer. Hand-rolled to avoid taking a dependency on
/// <c>prometheus-net</c> (see issue #5 / project conventions).
/// </summary>
public sealed class MetricsRegistry
{
    private readonly Dictionary<byte, ChannelMetrics> _channels = new();
    private readonly object _lock = new();
    private ISessionMetricsProvider? _sessions;
    private readonly SessionLifecycleMetrics _sessionLifecycle = new();
    private readonly ThrottleMetrics _throttle = new();
    private readonly TransportMetrics _transport = new();
    private readonly BoundedSessionFirmCounters _sessionFirmMessages = new();

    public SessionLifecycleMetrics Sessions => _sessionLifecycle;
    public ThrottleMetrics Throttle => _throttle;
    public TransportMetrics Transport => _transport;

    /// <summary>
    /// Bounded-cardinality per-firm/per-session inbound message counters
    /// (issue #176). Increment from the dispatcher; rendered as
    /// <c>exch_session_messages_total{firm,session_id}</c> with an
    /// <c>"_other"</c> overflow series when the cap is exceeded.
    /// </summary>
    public BoundedSessionFirmCounters SessionFirmMessages => _sessionFirmMessages;

    public ChannelMetrics RegisterChannel(byte channelNumber)
    {
        lock (_lock)
        {
            if (_channels.TryGetValue(channelNumber, out var existing)) return existing;
            var m = new ChannelMetrics(channelNumber);
            _channels.Add(channelNumber, m);
            return m;
        }
    }

    public void SetSessionProvider(ISessionMetricsProvider provider)
    {
        lock (_lock) _sessions = provider;
    }

    public IReadOnlyCollection<ChannelMetrics> Channels
    {
        get { lock (_lock) return _channels.Values.ToArray(); }
    }

    /// <summary>
    /// Render the Prometheus 0.0.4 text exposition format for all registered
    /// metrics. Output is ASCII; numeric values use invariant culture.
    /// </summary>
    public string RenderProm()
    {
        var sb = new StringBuilder(1024);
        ChannelMetrics[] channels;
        ISessionMetricsProvider? sessions;
        lock (_lock)
        {
            channels = _channels.Values.OrderBy(c => c.ChannelNumber).ToArray();
            sessions = _sessions;
        }

        EmitCounter(sb, "exch_orders_in_total",
            "Total inbound order commands (New/Cancel/Replace) accepted by the dispatcher.",
            channels, c => c.OrdersIn);
        EmitCounter(sb, "exch_packets_out_total",
            "Total UMDF packets emitted to the multicast sink.",
            channels, c => c.PacketsOut);
        EmitCounter(sb, "exch_snapshots_emitted_total",
            "Total UMDF snapshot frames emitted by the snapshot rotator (issue #1).",
            channels, c => c.SnapshotsEmitted);
        EmitCounter(sb, "exch_instrument_defs_emitted_total",
            "Total UMDF SecurityList/InstrumentDef messages emitted (issue #2).",
            channels, c => c.InstrumentDefsEmitted);
        EmitGauge(sb, "exch_dispatch_loop_last_tick_unixms",
            "Unix time (milliseconds) of the dispatcher loop's last heartbeat.",
            channels, c => c.LastTickUnixMs);
        EmitCounter(sb, "umdf_chaos_dropped_total",
            "Total UMDF packets dropped by the chaos decorator (issue #119). 0 unless chaos is enabled in HostConfig.",
            channels, c => c.ChaosDropped);
        EmitCounter(sb, "umdf_chaos_duplicated_total",
            "Total UMDF packets duplicated by the chaos decorator (issue #119).",
            channels, c => c.ChaosDuplicated);
        EmitCounter(sb, "umdf_chaos_reordered_total",
            "Total UMDF packets held back for reorder by the chaos decorator (issue #119).",
            channels, c => c.ChaosReordered);
        EmitCounter(sb, "exch_dispatch_queue_full_total",
            "Total inbound work items dropped because the per-channel dispatcher's bounded queue was full (issue #155). A non-zero value usually means a producer is outpacing the engine — alert / investigate.",
            channels, c => c.DispatchQueueFull);
        EmitCounter(sb, "exch_decode_errors_total",
            "Total inbound frames that failed gateway decoding for this channel (issue #155). A non-zero value indicates a malformed/incompatible producer; the gateway emits a SessionReject and may close the session.",
            channels, c => c.DecodeErrors);
        EmitCounter(sb, "exch_dispatcher_crash_total",
            "Total work-items whose ProcessOne raised an unhandled exception (issue #170). The dispatcher loop catches and logs these, then continues draining; a non-zero value is a hard bug-report signal — every increment is a wedge that would have killed the channel before the containment fix.",
            channels, c => c.DispatcherCrashes);

        // Issue #172: per-channel UDP publish errors broken down by kind.
        // Multiple series (one per kind) under a single metric name with a
        // 'kind' label so operators can alert on socket_error specifically
        // (likely transient route loss) vs message_too_large (a hard bug —
        // engine is producing an oversized UMDF packet).
        sb.Append("# HELP exch_umdf_publish_errors_total ")
          .Append("Total UMDF packet publish failures per channel and error kind (issue #172). The decorator catches the SocketException, increments this counter, and continues — the dispatcher loop is never poisoned by a publish failure. kind=host_unreachable usually means a transient route/multicast issue; kind=message_too_large is a packetizer bug; kind=socket_error covers everything else.\n");
        sb.Append("# TYPE exch_umdf_publish_errors_total counter\n");
        EmitLabeledCounter(sb, "exch_umdf_publish_errors_total", "kind", "socket_error",
            channels, c => c.PublishErrors);
        EmitLabeledCounter(sb, "exch_umdf_publish_errors_total", "kind", "host_unreachable",
            channels, c => c.PublishErrorsHostUnreachable);
        EmitLabeledCounter(sb, "exch_umdf_publish_errors_total", "kind", "message_too_large",
            channels, c => c.PublishErrorsMessageTooLarge);

        sb.Append("# HELP exch_send_queue_depth Per-session ExecutionReport send-queue depth (channel=\"all\" because the session queue is shared).\n");
        sb.Append("# TYPE exch_send_queue_depth gauge\n");
        SessionDiagnostics[] sessionSnap = sessions != null
            ? sessions.Sample().ToArray()
            : Array.Empty<SessionDiagnostics>();
        if (sessionSnap.Length > 0)
        {
            foreach (var s in sessionSnap)
            {
                sb.Append("exch_send_queue_depth{channel=\"all\",session=\"")
                  .Append(EscapeLabel(s.SessionId))
                  .Append("\"} ")
                  .Append(s.SendQueueDepth.ToString(CultureInfo.InvariantCulture))
                  .Append('\n');
            }
        }

        // Per-session diagnostics series (issue #70).
        EmitSessionGauge(sb, sessionSnap, "fixp_session_state",
            "Current FIXP state (0=Idle, 1=Negotiated, 2=Established, 3=Suspended, 4=Terminated).",
            withFirmLabel: true, s => s.State);
        EmitSessionGauge(sb, sessionSnap, "fixp_session_outbound_seq",
            "Last allocated outbound MsgSeqNum on this session (peer's next-expected is this+1).",
            withFirmLabel: false, s => (long)s.OutboundSeq);
        EmitSessionGauge(sb, sessionSnap, "fixp_session_inbound_expected_seq",
            "Highest inbound MsgSeqNum accepted on this session (next-expected is this+1).",
            withFirmLabel: false, s => (long)s.InboundExpectedSeq);
        EmitSessionGauge(sb, sessionSnap, "fixp_session_retx_buffer_depth",
            "Number of business frames buffered for replay on this session.",
            withFirmLabel: false, s => s.RetxBufferDepth);
        EmitSessionGauge(sb, sessionSnap, "fixp_session_attached_transports",
            "1 if a TCP transport is currently attached, 0 if Suspended.",
            withFirmLabel: false, s => s.AttachedTransportId is null ? 0 : 1);
        EmitSessionGauge(sb, sessionSnap, "fixp_session_last_activity_unixms",
            "Unix time (ms) of the most recent inbound frame on this session; 0 if nothing yet.",
            withFirmLabel: false, s => s.LastActivityAtMs);

        EmitProcessCounter(sb, "exch_session_established_total",
            "Total FIXP sessions that have transitioned into Established (initial Establish + rebind via #69b).",
            _sessionLifecycle.Established);
        EmitProcessCounter(sb, "exch_session_suspended_total",
            "Total FIXP sessions that have transitioned into Suspended (transport drop while Established, issue #69a).",
            _sessionLifecycle.Suspended);
        EmitProcessCounter(sb, "exch_session_rebound_total",
            "Total successful re-attaches of a Suspended session via Establish on a new TCP connection (issue #69b).",
            _sessionLifecycle.Rebound);
        EmitProcessCounter(sb, "exch_session_reaped_total",
            "Total Suspended FIXP sessions closed by the listener's CoD/suspended reaper after exceeding SuspendedTimeoutMs.",
            _sessionLifecycle.Reaped);
        EmitProcessCounter(sb, "exch_session_cancel_on_disconnect_fired_total",
            "Total times the cancel-on-disconnect timer fired for a Suspended FIXP session (issue #54 / GAP-18) and the gateway issued a session-scoped mass cancel.",
            _sessionLifecycle.CancelOnDisconnectFired);
        EmitProcessCounter(sb, "exch_throttle_accepted_total",
            "Total inbound application messages accepted by the per-session sliding-window throttle (issue #56 / GAP-20).",
            _throttle.Accepted);
        EmitProcessCounter(sb, "exch_throttle_rejected_total",
            "Total inbound application messages rejected with BusinessMessageReject(\"Throttle limit exceeded\") by the per-session sliding-window throttle (issue #56 / GAP-20).",
            _throttle.Rejected);
        EmitProcessCounter(sb, "exch_transport_send_queue_full_total",
            "Total times the gateway's TcpTransport closed a session because its bounded outbound send queue overflowed (issue #155). A non-zero value means a stuck/slow consumer is causing teardowns — alert.",
            _transport.SendQueueFull);

        // Issue #173: latency histograms. Per-channel buckets in seconds;
        // observed via Stopwatch.GetTimestamp() at the dispatcher
        // boundaries (decode→enqueue, enqueue→pickup, engine entry→exit,
        // engine exit→FlushPacket complete).
        EmitHistogram(sb, "exch_inbound_decode_seconds",
            "Latency from decode start (gateway frame ready) to enqueue completion on the dispatcher's inbound queue, in seconds.",
            channels, c => c.InboundDecode);
        EmitHistogram(sb, "exch_dispatch_wait_seconds",
            "Latency from work-item enqueue to dispatch-loop pickup, in seconds. A growing tail indicates queue saturation or threadpool starvation.",
            channels, c => c.DispatchWait);
        EmitHistogram(sb, "exch_engine_process_seconds",
            "Latency spent inside the matching engine for a single command (Submit/Cancel/Replace/MassCancel/Cross), in seconds.",
            channels, c => c.EngineProcess);
        EmitHistogram(sb, "exch_outbound_emit_seconds",
            "Latency to flush the per-command UMDF packet to the outbound sink (engine exit → packet sink return), in seconds.",
            channels, c => c.OutboundEmit);

        // Issue #174: throughput counters. Bounded labels (msg_type ≤ 6,
        // exec_type ≤ 7, feed = 3) — safe to ship per-channel.
        EmitLabeledChannelCounter(sb, "exch_inbound_messages_total",
            "Total inbound application commands received by the dispatcher, broken down by message kind.",
            "msg_type", channels,
            ChannelMetrics.InboundMessageKindNames,
            (c, i) => c.ReadInboundMessages((InboundMessageKind)i));
        EmitLabeledChannelCounter(sb, "exch_execution_reports_total",
            "Total ExecutionReports emitted to client sessions, broken down by ER kind. Passive variants are reports for resting orders touched by another session's aggressor.",
            "exec_type", channels,
            ChannelMetrics.ExecutionReportKindNames,
            (c, i) => c.ReadExecutionReports((ExecutionReportKind)i));
        EmitLabeledChannelCounter(sb, "exch_umdf_packets_total",
            "Total UMDF packets published per multicast feed.",
            "feed", channels,
            ChannelMetrics.UmdfFeedKindNames,
            (c, i) => c.ReadUmdfPackets((UmdfFeedKind)i));
        EmitLabeledChannelCounter(sb, "exch_umdf_bytes_total",
            "Total bytes (UDP payload, including UMDF packet header) published per multicast feed.",
            "feed", channels,
            ChannelMetrics.UmdfFeedKindNames,
            (c, i) => c.ReadUmdfBytes((UmdfFeedKind)i));

        EmitSessionFirmCounters(sb);

        EmitRuntimeMetrics(sb);

        return sb.ToString();
    }

    private void EmitSessionFirmCounters(StringBuilder sb)
    {
        // Issue #176 (B4): bounded-cardinality per-firm and per-session
        // inbound message counters. Two metric names with disjoint label
        // sets so dashboards can group on either dimension cheaply
        // without exploding the time-series count.
        //
        // Cardinality budget: ≤ MaxFirms+1 firm-series and
        // ≤ MaxSessions+1 session-series (the +1 is the "_other"
        // overflow). Beyond the cap, every newly-seen firm/session
        // contributes to the overflow series only — its identity is
        // lost on the metric (still visible via /sessions and audit
        // logs). Document the cap in the operability runbook.
        sb.Append("# HELP exch_session_messages_by_firm_total ")
          .Append("Total inbound application messages received from a given firm (issue #176). Bounded to ")
          .Append(_sessionFirmMessages.MaxFirms.ToString(CultureInfo.InvariantCulture))
          .Append(" firms; overflow funnels into firm=\"")
          .Append(BoundedSessionFirmCounters.OverflowLabel)
          .Append("\".\n");
        sb.Append("# TYPE exch_session_messages_by_firm_total counter\n");
        var firms = _sessionFirmMessages.FirmsSnapshot();
        Array.Sort(firms, (a, b) => a.Key.CompareTo(b.Key));
        foreach (var kv in firms)
        {
            sb.Append("exch_session_messages_by_firm_total{firm=\"")
              .Append(kv.Key.ToString(CultureInfo.InvariantCulture))
              .Append("\"} ")
              .Append(kv.Value.ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }
        // Always emit the overflow series so dashboards can alert on
        // rate(...{firm="_other"}[5m]) > 0 without having to special-case
        // an absent label.
        sb.Append("exch_session_messages_by_firm_total{firm=\"")
          .Append(BoundedSessionFirmCounters.OverflowLabel)
          .Append("\"} ")
          .Append(_sessionFirmMessages.FirmOverflowCount.ToString(CultureInfo.InvariantCulture))
          .Append('\n');

        sb.Append("# HELP exch_session_messages_by_session_total ")
          .Append("Total inbound application messages received on a given FIXP session (issue #176). Bounded to ")
          .Append(_sessionFirmMessages.MaxSessions.ToString(CultureInfo.InvariantCulture))
          .Append(" sessions; overflow funnels into session_id=\"")
          .Append(BoundedSessionFirmCounters.OverflowLabel)
          .Append("\".\n");
        sb.Append("# TYPE exch_session_messages_by_session_total counter\n");
        var sessionsArr = _sessionFirmMessages.SessionsSnapshot();
        Array.Sort(sessionsArr, (a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
        foreach (var kv in sessionsArr)
        {
            sb.Append("exch_session_messages_by_session_total{session_id=\"")
              .Append(EscapeLabel(kv.Key))
              .Append("\"} ")
              .Append(kv.Value.ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }
        sb.Append("exch_session_messages_by_session_total{session_id=\"")
          .Append(BoundedSessionFirmCounters.OverflowLabel)
          .Append("\"} ")
          .Append(_sessionFirmMessages.SessionOverflowCount.ToString(CultureInfo.InvariantCulture))
          .Append('\n');
    }

    private static void EmitRuntimeMetrics(StringBuilder sb)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();

        // process_cpu_seconds_total is the conventional name; we pick the
        // user+kernel sum (= TotalProcessorTime) which is what `node_exporter`
        // and prometheus-net both use.
        EmitProcessCounterFloat(sb, "process_cpu_seconds_total",
            "Total user and kernel CPU time spent in seconds.",
            proc.TotalProcessorTime.TotalSeconds);
        EmitProcessGaugeFloat(sb, "process_resident_memory_bytes",
            "Resident set size of the process in bytes.",
            proc.WorkingSet64);
        EmitProcessGaugeFloat(sb, "process_virtual_memory_bytes",
            "Virtual memory size of the process in bytes.",
            proc.VirtualMemorySize64);
        EmitProcessGaugeFloat(sb, "process_start_time_seconds",
            "Start time of the process since unix epoch in seconds.",
            new DateTimeOffset(proc.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0);
        // process_open_fds is Linux-only — best-effort by counting /proc/self/fd
        // entries; on Windows we silently skip it to keep the exposition clean.
        if (System.IO.Directory.Exists("/proc/self/fd"))
        {
            try
            {
                int fds = System.IO.Directory.EnumerateFileSystemEntries("/proc/self/fd").Count();
                EmitProcessGaugeFloat(sb, "process_open_fds",
                    "Number of open file descriptors (Linux only).", fds);
            }
            catch { /* /proc may not be readable in some sandboxes; just skip */ }
        }

        // dotnet_total_memory_bytes — managed heap in use.
        EmitProcessGaugeFloat(sb, "dotnet_total_memory_bytes",
            "Total bytes currently allocated on the managed heap.",
            GC.GetTotalMemory(forceFullCollection: false));
        EmitProcessCounterFloat(sb, "dotnet_total_allocated_bytes",
            "Total bytes ever allocated by the managed runtime.",
            GC.GetTotalAllocatedBytes());

        // GC collection counts per generation. .NET 10 has 3 generations
        // plus a virtual 'LOH'/'PinnedObjectHeap' bucket — we expose the
        // three real generations explicitly.
        sb.Append("# HELP dotnet_gc_collections_total Total GC collections per generation.\n");
        sb.Append("# TYPE dotnet_gc_collections_total counter\n");
        for (int gen = 0; gen <= 2; gen++)
        {
            sb.Append("dotnet_gc_collections_total{generation=\"")
              .Append(gen.ToString(CultureInfo.InvariantCulture))
              .Append("\"} ")
              .Append(GC.CollectionCount(gen).ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }
        // .NET 7+: cumulative GC pause time across the process lifetime.
        EmitProcessCounterFloat(sb, "dotnet_gc_pause_seconds_total",
            "Total time the runtime spent paused for GC, in seconds (cumulative across all generations).",
            GC.GetTotalPauseDuration().TotalSeconds);

        // ThreadPool — current worker thread count (busy + idle) and the
        // depth of the global work-item queue. A growing queue is the
        // classic signal of threadpool starvation under sync-over-async or
        // CPU saturation.
        EmitProcessGaugeFloat(sb, "dotnet_threadpool_threads_count",
            "Number of currently active threadpool worker threads.",
            ThreadPool.ThreadCount);
        EmitProcessGaugeFloat(sb, "dotnet_threadpool_queue_length",
            "Number of work items currently queued to the threadpool but not yet started.",
            ThreadPool.PendingWorkItemCount);
        EmitProcessCounterFloat(sb, "dotnet_threadpool_completed_items_total",
            "Total work items that have completed execution on the threadpool.",
            ThreadPool.CompletedWorkItemCount);
    }

    private static void EmitProcessCounterFloat(StringBuilder sb, string name, string help, double value)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" counter\n");
        sb.Append(name).Append(' ').Append(value.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void EmitProcessGaugeFloat(StringBuilder sb, string name, string help, double value)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        sb.Append(name).Append(' ').Append(value.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void EmitProcessCounter(StringBuilder sb, string name, string help, long value)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" counter\n");
        sb.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void EmitCounter(StringBuilder sb, string name, string help,
        ChannelMetrics[] channels, Func<ChannelMetrics, long> selector)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" counter\n");
        foreach (var c in channels)
        {
            sb.Append(name).Append("{channel=\"")
              .Append(c.ChannelNumber.ToString(CultureInfo.InvariantCulture))
              .Append("\"} ")
              .Append(selector(c).ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }
    }

    private static void EmitLabeledCounter(StringBuilder sb, string name, string label, string labelValue,
        ChannelMetrics[] channels, Func<ChannelMetrics, long> selector)
    {
        // Emits {channel,label} pairs reusing an already-emitted HELP/TYPE
        // header. Caller is responsible for emitting the header exactly once
        // before the first call for a given metric name.
        foreach (var c in channels)
        {
            sb.Append(name).Append("{channel=\"")
              .Append(c.ChannelNumber.ToString(CultureInfo.InvariantCulture))
              .Append("\",").Append(label).Append("=\"").Append(labelValue).Append("\"} ")
              .Append(selector(c).ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }
    }

    private static void EmitLabeledChannelCounter(StringBuilder sb, string name, string help,
        string label, ChannelMetrics[] channels, string[] labelValues, Func<ChannelMetrics, int, long> selector)
    {
        // Emits {channel,<label>} pairs with a single HELP/TYPE header
        // covering all label values. Cardinality = channels × labelValues.
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" counter\n");
        foreach (var c in channels)
        {
            for (int i = 0; i < labelValues.Length; i++)
            {
                sb.Append(name).Append("{channel=\"")
                  .Append(c.ChannelNumber.ToString(CultureInfo.InvariantCulture))
                  .Append("\",").Append(label).Append("=\"").Append(labelValues[i]).Append("\"} ")
                  .Append(selector(c, i).ToString(CultureInfo.InvariantCulture))
                  .Append('\n');
            }
        }
    }

    private static void EmitGauge(StringBuilder sb, string name, string help,
        ChannelMetrics[] channels, Func<ChannelMetrics, long> selector)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        foreach (var c in channels)
        {
            sb.Append(name).Append("{channel=\"")
              .Append(c.ChannelNumber.ToString(CultureInfo.InvariantCulture))
              .Append("\"} ")
              .Append(selector(c).ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }
    }

    private static void EmitSessionGauge(StringBuilder sb, SessionDiagnostics[] sessions,
        string name, string help, bool withFirmLabel, Func<SessionDiagnostics, long> selector)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        foreach (var s in sessions)
        {
            sb.Append(name).Append("{session=\"").Append(EscapeLabel(s.SessionId)).Append('"');
            if (withFirmLabel)
                sb.Append(",firm=\"").Append(EscapeLabel(s.FirmId)).Append('"');
            sb.Append("} ").Append(selector(s).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    private static void EmitHistogram(StringBuilder sb, string name, string help,
        ChannelMetrics[] channels, Func<ChannelMetrics, LatencyHistogram> selector)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" histogram\n");
        var bounds = LatencyHistogram.Buckets;
        foreach (var c in channels)
        {
            var h = selector(c);
            var snap = h.SnapshotCounts();
            long cumulative = 0;
            for (int i = 0; i < bounds.Length; i++)
            {
                cumulative += snap.BucketCounts[i];
                sb.Append(name).Append("_bucket{channel=\"")
                  .Append(c.ChannelNumber.ToString(CultureInfo.InvariantCulture))
                  .Append("\",le=\"")
                  .Append(bounds[i].ToString("0.######", CultureInfo.InvariantCulture))
                  .Append("\"} ")
                  .Append(cumulative.ToString(CultureInfo.InvariantCulture))
                  .Append('\n');
            }
            cumulative += snap.OverflowCount;
            sb.Append(name).Append("_bucket{channel=\"")
              .Append(c.ChannelNumber.ToString(CultureInfo.InvariantCulture))
              .Append("\",le=\"+Inf\"} ")
              .Append(cumulative.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(name).Append("_sum{channel=\"")
              .Append(c.ChannelNumber.ToString(CultureInfo.InvariantCulture))
              .Append("\"} ")
              .Append(snap.SumSeconds.ToString("0.#########", CultureInfo.InvariantCulture))
              .Append('\n');
            sb.Append(name).Append("_count{channel=\"")
              .Append(c.ChannelNumber.ToString(CultureInfo.InvariantCulture))
              .Append("\"} ")
              .Append(cumulative.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    private static string EscapeLabel(string s)
    {
        if (s.IndexOfAny(new[] { '\\', '"', '\n' }) < 0) return s;
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Lock-free fixed-bucket histogram for sub-millisecond latency
/// observations (issue #173). Per-bucket counters use
/// <see cref="Interlocked"/>; sum-of-seconds uses a CAS loop on a 64-bit
/// double bit pattern. Observation overhead is dominated by
/// <see cref="Stopwatch.GetTimestamp"/> + a binary-bucket search and
/// measures &lt; 200 ns on a typical x64 host.
/// </summary>
public sealed class LatencyHistogram
{
    /// <summary>
    /// Bucket upper bounds in seconds (exclusive of +Inf), shared across
    /// every histogram in the process. Tuned for matching-engine latencies
    /// (50 us → 1 s) per the issue spec.
    /// </summary>
    public static readonly double[] Buckets = new[]
    {
        0.00005, 0.0001, 0.00025, 0.0005,
        0.001, 0.0025, 0.005, 0.01,
        0.025, 0.05, 0.1, 0.25, 1.0,
    };

    private readonly long[] _counts = new long[Buckets.Length];
    private long _overflow;
    private long _sumBits; // double bit-pattern; mutated via CAS

    /// <summary>
    /// Observe one latency sample, in seconds. Negative or NaN values are
    /// silently dropped (defensive — Stopwatch should never produce them).
    /// </summary>
    public void Observe(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) return;
        int idx = FindBucket(seconds);
        if (idx >= 0) Interlocked.Increment(ref _counts[idx]);
        else Interlocked.Increment(ref _overflow);

        // CAS-add into the double sum.
        long current, updated;
        do
        {
            current = Volatile.Read(ref _sumBits);
            double sum = BitConverter.Int64BitsToDouble(current) + seconds;
            updated = BitConverter.DoubleToInt64Bits(sum);
        } while (Interlocked.CompareExchange(ref _sumBits, updated, current) != current);
    }

    /// <summary>
    /// Convenience wrapper: observe a sample expressed in
    /// <see cref="Stopwatch"/> ticks (caller passes
    /// <c>Stopwatch.GetTimestamp() - start</c>).
    /// </summary>
    public void ObserveTicks(long elapsedTicks)
    {
        if (elapsedTicks < 0) return;
        Observe(elapsedTicks / (double)Stopwatch.Frequency);
    }

    /// <summary>
    /// Atomic-ish snapshot of the bucket counts + sum + overflow for the
    /// Prometheus renderer. Buckets are reported in their stored order
    /// (lower-bound first); the renderer accumulates them into the
    /// cumulative <c>le=</c> series Prometheus expects.
    /// </summary>
    public Snapshot SnapshotCounts()
    {
        var bucketCounts = new long[_counts.Length];
        for (int i = 0; i < _counts.Length; i++)
            bucketCounts[i] = Interlocked.Read(ref _counts[i]);
        long overflow = Interlocked.Read(ref _overflow);
        double sum = BitConverter.Int64BitsToDouble(Volatile.Read(ref _sumBits));
        return new Snapshot(bucketCounts, overflow, sum);
    }

    private static int FindBucket(double seconds)
    {
        // Linear scan — Buckets.Length is 13, so a binary search costs more
        // in branch overhead than it saves in iterations.
        for (int i = 0; i < Buckets.Length; i++)
            if (seconds <= Buckets[i]) return i;
        return -1;
    }

    public readonly record struct Snapshot(long[] BucketCounts, long OverflowCount, double SumSeconds);
}
