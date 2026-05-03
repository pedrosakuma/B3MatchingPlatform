using System.Threading;

namespace B3.Exchange.Contracts;

/// <summary>
/// Process-wide counters tracking FIXP session lifecycle transitions
/// (Established / Suspended / Rebound / Reaped / CancelOnDisconnect).
/// Owned by the Core <c>MetricsRegistry</c> and exposed via
/// <c>MetricsRegistry.Sessions</c>. All increments are atomic and
/// safe to call from any thread (state-machine transitions, listener
/// reaper, etc).
///
/// <para>Lives in <c>B3.Exchange.Contracts</c> so the Gateway can consume
/// the type without taking a project reference on Core (issue #154).</para>
/// </summary>
public sealed class SessionLifecycleMetrics
{
    private long _established;
    private long _suspended;
    private long _rebound;
    private long _reaped;
    private long _cancelOnDisconnectFired;

    public long Established => Interlocked.Read(ref _established);
    public long Suspended => Interlocked.Read(ref _suspended);
    public long Rebound => Interlocked.Read(ref _rebound);
    public long Reaped => Interlocked.Read(ref _reaped);
    public long CancelOnDisconnectFired => Interlocked.Read(ref _cancelOnDisconnectFired);

    public void IncEstablished() => Interlocked.Increment(ref _established);
    public void IncSuspended() => Interlocked.Increment(ref _suspended);
    public void IncRebound() => Interlocked.Increment(ref _rebound);
    public void IncReaped() => Interlocked.Increment(ref _reaped);
    public void IncCancelOnDisconnectFired() => Interlocked.Increment(ref _cancelOnDisconnectFired);
}

/// <summary>
/// Process-wide counters for the per-session inbound sliding-window
/// throttle (issue #56 / GAP-20, guidelines §4.9). Increments are atomic
/// so the FIXP recv thread can advance them while a metrics scrape runs
/// concurrently on the HTTP thread.
///
/// <para>Lives in <c>B3.Exchange.Contracts</c> so the Gateway can consume
/// the type without taking a project reference on Core (issue #154).</para>
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
/// Process-wide SRE counter for transport-layer backpressure events
/// (issue #155). Today the gateway's <c>TcpTransport</c> closes a
/// connection on send-queue overflow to avoid unbounded memory growth;
/// this counter records every such event so operators can alert on
/// repeated overflows (typically a stuck or slow peer).
///
/// <para>Lives in <c>B3.Exchange.Contracts</c> so the Gateway can consume
/// the type without taking a project reference on Core.</para>
/// </summary>
public sealed class TransportMetrics
{
    private long _sendQueueFull;

    public long SendQueueFull => Interlocked.Read(ref _sendQueueFull);

    public void IncSendQueueFull() => Interlocked.Increment(ref _sendQueueFull);
}
