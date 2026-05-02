using System.Globalization;
using System.Text;

namespace B3.Exchange.Core;

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

    public ChannelMetrics(byte channelNumber)
    {
        ChannelNumber = channelNumber;
    }

    public long OrdersIn => Interlocked.Read(ref _ordersIn);
    public long PacketsOut => Interlocked.Read(ref _packetsOut);
    public long SnapshotsEmitted => Interlocked.Read(ref _snapshotsEmitted);
    public long InstrumentDefsEmitted => Interlocked.Read(ref _instrumentDefsEmitted);
    public long LastTickUnixMs => Interlocked.Read(ref _lastTickUnixMs);

    public void IncOrdersIn() => Interlocked.Increment(ref _ordersIn);
    public void IncPacketsOut() => Interlocked.Increment(ref _packetsOut);
    public void IncSnapshotsEmitted() => Interlocked.Increment(ref _snapshotsEmitted);
    public void IncInstrumentDefsEmitted() => Interlocked.Increment(ref _instrumentDefsEmitted);

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
/// Process-wide counters for FIXP session lifecycle events. Exposed via
/// <see cref="MetricsRegistry.Sessions"/>. All increments are atomic and
/// safe to call from any thread (state-machine transitions, listener
/// reaper, etc).
/// </summary>
public sealed class SessionLifecycleMetrics
{
    private long _established;
    private long _suspended;
    private long _rebound;
    private long _reaped;

    public long Established => Interlocked.Read(ref _established);
    public long Suspended => Interlocked.Read(ref _suspended);
    public long Rebound => Interlocked.Read(ref _rebound);
    public long Reaped => Interlocked.Read(ref _reaped);

    public void IncEstablished() => Interlocked.Increment(ref _established);
    public void IncSuspended() => Interlocked.Increment(ref _suspended);
    public void IncRebound() => Interlocked.Increment(ref _rebound);
    public void IncReaped() => Interlocked.Increment(ref _reaped);
}

/// <summary>
/// Process-wide counters for the per-session inbound sliding-window
/// throttle (issue #56 / GAP-20, guidelines §4.9). Increments are atomic
/// so the FIXP recv thread can advance them while a metrics scrape runs
/// concurrently on the HTTP thread.
/// </summary>
public sealed class ThrottleMetrics
{
    private long _accepted;
    private long _rejected;

    public long Accepted => Interlocked.Read(ref _accepted);
    public long Rejected => Interlocked.Read(ref _rejected);

    public void IncAccepted() => Interlocked.Increment(ref _accepted);
    public void IncRejected() => Interlocked.Increment(ref _rejected);
}

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

    public SessionLifecycleMetrics Sessions => _sessionLifecycle;
    public ThrottleMetrics Throttle => _throttle;

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
        EmitProcessCounter(sb, "exch_throttle_accepted_total",
            "Total inbound application messages accepted by the per-session sliding-window throttle (issue #56 / GAP-20).",
            _throttle.Accepted);
        EmitProcessCounter(sb, "exch_throttle_rejected_total",
            "Total inbound application messages rejected with BusinessMessageReject(\"Throttle limit exceeded\") by the per-session sliding-window throttle (issue #56 / GAP-20).",
            _throttle.Rejected);

        return sb.ToString();
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
