using System.Collections.Concurrent;

namespace B3.Exchange.Core;

/// <summary>
/// Bounded-cardinality per-firm and per-session message counters
/// (issue #176 / B4). Operators need to spot "which client is causing
/// the storm", but Prometheus label cardinality cannot grow without
/// bound — a single misbehaving fleet of test clients could otherwise
/// blow up the TSDB. We cap each dimension; once the cap is reached,
/// every additional first-seen firm/session funnels into the
/// <c>"_other"</c> overflow series.
///
/// <para>Thread-safety: <see cref="Inc"/> is lock-free and safe to call
/// from any thread (including the gateway's accept loop and per-channel
/// dispatcher threads concurrently). The per-key counter is held in a
/// <c>long[1]</c> so <see cref="Interlocked.Increment(ref long)"/> can
/// mutate it atomically without re-keying the dictionary on every hit.
/// Caps are advisory: under contention the dictionary can briefly grow a
/// handful of entries past the limit before the size check rejects new
/// keys; the slack is bounded by the number of concurrent first-time
/// inserters and is acceptable because the goal is bounded long-term
/// growth, not strict real-time enforcement.</para>
///
/// <para>Snapshot is taken via <see cref="ConcurrentDictionary{TKey,TValue}.ToArray"/>
/// for stable iteration during a Prometheus scrape. The overflow
/// counter is read with <see cref="Volatile.Read(ref long)"/>.</para>
/// </summary>
public sealed class BoundedSessionFirmCounters
{
    public const int DefaultMaxFirms = 100;
    public const int DefaultMaxSessions = 500;
    public const string OverflowLabel = "_other";

    private readonly ConcurrentDictionary<uint, long[]> _firms = new();
    private readonly ConcurrentDictionary<string, long[]> _sessions = new();
    private long _firmOverflow;
    private long _sessionOverflow;

    public int MaxFirms { get; }
    public int MaxSessions { get; }

    public BoundedSessionFirmCounters(int maxFirms = DefaultMaxFirms, int maxSessions = DefaultMaxSessions)
    {
        if (maxFirms <= 0) throw new ArgumentOutOfRangeException(nameof(maxFirms));
        if (maxSessions <= 0) throw new ArgumentOutOfRangeException(nameof(maxSessions));
        MaxFirms = maxFirms;
        MaxSessions = maxSessions;
    }

    /// <summary>
    /// Increment the firm and session counters for one inbound application
    /// message. <paramref name="sessionId"/> may be empty/null when the
    /// message arrives before a FIXP session is bound to a logical id;
    /// in that case only the firm dimension is incremented.
    /// </summary>
    public void Inc(uint firmCode, string? sessionId)
    {
        IncInDimension(_firms, firmCode, MaxFirms, ref _firmOverflow);
        if (!string.IsNullOrEmpty(sessionId))
        {
            IncInDimension(_sessions, sessionId, MaxSessions, ref _sessionOverflow);
        }
    }

    private static void IncInDimension<TKey>(ConcurrentDictionary<TKey, long[]> map,
        TKey key, int cap, ref long overflow) where TKey : notnull
    {
        if (map.TryGetValue(key, out var existing))
        {
            Interlocked.Increment(ref existing[0]);
            return;
        }
        if (map.Count >= cap)
        {
            Interlocked.Increment(ref overflow);
            return;
        }
        var box = new long[1] { 1 };
        if (!map.TryAdd(key, box))
        {
            // Lost the add race; another thread won. Re-fetch and bump it
            // (correctness preserved — the +1 we counted goes onto the
            // winner's box). On the rare overflow race the size check above
            // already passed for both threads; that's the documented slack.
            if (map.TryGetValue(key, out var winner))
                Interlocked.Increment(ref winner[0]);
            else
                Interlocked.Increment(ref overflow);
        }
    }

    /// <summary>Snapshot of the per-firm counters at the moment of the call.</summary>
    public KeyValuePair<uint, long>[] FirmsSnapshot()
        => _firms.ToArray().Select(kv => new KeyValuePair<uint, long>(kv.Key, Volatile.Read(ref kv.Value[0]))).ToArray();

    /// <summary>Snapshot of the per-session counters at the moment of the call.</summary>
    public KeyValuePair<string, long>[] SessionsSnapshot()
        => _sessions.ToArray().Select(kv => new KeyValuePair<string, long>(kv.Key, Volatile.Read(ref kv.Value[0]))).ToArray();

    public long FirmOverflowCount => Volatile.Read(ref _firmOverflow);
    public long SessionOverflowCount => Volatile.Read(ref _sessionOverflow);
}
