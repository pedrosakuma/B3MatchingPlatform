using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<uint, long[]> _rateLimitedBySession = new();
    private readonly ConcurrentDictionary<string, long[]> _rateLimitedBySessionLabels = new();
    private long _accepted;
    private long _rejected;

    public long Accepted => Interlocked.Read(ref _accepted);
    public long Rejected => Interlocked.Read(ref _rejected);

    public void IncAccepted() => Interlocked.Increment(ref _accepted);
    public void IncRejected() => IncRejected(sessionId: null);
    public void IncRejected(string? sessionId)
    {
        if (uint.TryParse(sessionId, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var numericSessionId))
        {
            IncRejected(numericSessionId);
            return;
        }

        Interlocked.Increment(ref _rejected);
        if (string.IsNullOrEmpty(sessionId))
            return;
        var box = _rateLimitedBySessionLabels.GetOrAdd(sessionId, static _ => new long[1]);
        Interlocked.Increment(ref box[0]);
    }

    public void IncRejected(uint sessionId)
    {
        Interlocked.Increment(ref _rejected);
        if (sessionId == 0)
            return;
        var box = _rateLimitedBySession.GetOrAdd(sessionId, static _ => new long[1]);
        Interlocked.Increment(ref box[0]);
    }

    public KeyValuePair<string, long>[] RateLimitedBySessionSnapshot()
        => _rateLimitedBySession.ToArray()
            .Select(kv => new KeyValuePair<string, long>(
                kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Volatile.Read(ref kv.Value[0])))
            .Concat(_rateLimitedBySessionLabels.ToArray()
                .Select(kv => new KeyValuePair<string, long>(kv.Key, Volatile.Read(ref kv.Value[0]))))
            .ToArray();
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

/// <summary>
/// Process-wide counters for the per-session FIXP RetransmitBuffer
/// dimensioning (issue #288). The May 2026 disconnect/reattach review
/// identified <c>(buffer capacity, SuspendedTimeoutMs, fill rate while
/// disconnected)</c> as the dimensioning tuple for a successful reattach;
/// without these counters, an undersized ring is only discovered when a
/// peer fails to reattach — too late.
///
/// <list type="bullet">
///   <item><see cref="BufferEvictions"/>: every tick is a sequence number
///         that is no longer replayable. Each tick is a potential reattach
///         failure if the peer requests it.</item>
///   <item><see cref="PassiveErBuffered"/>: outbound frames the encoder
///         appended to the ring while the session was Suspended (the
///         issue #217 path). Quantifies "how much reattach traffic is
///         post-disconnect catch-up?".</item>
/// </list>
///
/// <para>Lives in <c>B3.Exchange.Contracts</c> so the Gateway can consume
/// the type without taking a project reference on Core.</para>
/// </summary>
public sealed class RetransmitMetrics
{
    private long _bufferEvictions;
    private long _passiveErBuffered;

    public long BufferEvictions => Interlocked.Read(ref _bufferEvictions);
    public long PassiveErBuffered => Interlocked.Read(ref _passiveErBuffered);

    public void IncBufferEvictions() => Interlocked.Increment(ref _bufferEvictions);
    public void IncPassiveErBuffered() => Interlocked.Increment(ref _passiveErBuffered);
}

/// <summary>
/// Process-wide gauges/counters for the durable FIXP outbound retransmit
/// journal. Lives in Contracts so Gateway can update it without referencing
/// Core.
/// </summary>
public sealed class FixpJournalMetrics
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionJournalMetrics> _sessions = new();

    public void Observe(uint sessionId, long bytes, long oldestAgeSeconds)
    {
        var s = Get(sessionId);
        Interlocked.Exchange(ref s.Bytes, bytes);
        Interlocked.Exchange(ref s.OldestAgeSeconds, oldestAgeSeconds);
    }

    public void IncRotation(uint sessionId, string reason)
    {
        var s = Get(sessionId);
        if (string.Equals(reason, "bytes", StringComparison.Ordinal))
            Interlocked.Increment(ref s.RotationsBytes);
        else if (string.Equals(reason, "age", StringComparison.Ordinal))
            Interlocked.Increment(ref s.RotationsAge);
    }

    public void Reset(uint sessionId)
        => _sessions.TryRemove(SessionKey(sessionId), out _);

    public IReadOnlyList<FixpJournalMetricsSnapshot> Snapshot()
        => _sessions
            .Select(kv => new FixpJournalMetricsSnapshot(
                kv.Key,
                Interlocked.Read(ref kv.Value.Bytes),
                Interlocked.Read(ref kv.Value.OldestAgeSeconds),
                Interlocked.Read(ref kv.Value.RotationsBytes),
                Interlocked.Read(ref kv.Value.RotationsAge)))
            .OrderBy(s => s.Session, StringComparer.Ordinal)
            .ToArray();

    private SessionJournalMetrics Get(uint sessionId)
        => _sessions.GetOrAdd(SessionKey(sessionId),
            static _ => new SessionJournalMetrics());

    private static string SessionKey(uint sessionId)
        => "0x" + sessionId.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class SessionJournalMetrics
    {
        public long Bytes;
        public long OldestAgeSeconds;
        public long RotationsBytes;
        public long RotationsAge;
    }
}

public readonly record struct FixpJournalMetricsSnapshot(
    string Session,
    long Bytes,
    long OldestAgeSeconds,
    long RotationsBytes,
    long RotationsAge);
