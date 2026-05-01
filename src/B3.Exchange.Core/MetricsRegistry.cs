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
/// Provider of per-session send-queue depth gauges. The host wires an
/// implementation that snapshots the active <c>EntryPointSession</c>
/// queues at scrape time. The "channel" label is currently fixed to
/// <c>"all"</c> because a session's send queue is shared across every
/// channel that may route ExecutionReports back to it; per-channel
/// partitioning of session queues is not implemented today.
/// </summary>
public interface ISessionMetricsProvider
{
    IEnumerable<SessionQueueSample> Sample();
}

public readonly record struct SessionQueueSample(string SessionId, long QueueDepth);

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
        if (sessions != null)
        {
            foreach (var s in sessions.Sample())
            {
                sb.Append("exch_send_queue_depth{channel=\"all\",session=\"")
                  .Append(EscapeLabel(s.SessionId))
                  .Append("\"} ")
                  .Append(s.QueueDepth.ToString(CultureInfo.InvariantCulture))
                  .Append('\n');
            }
        }

        return sb.ToString();
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
