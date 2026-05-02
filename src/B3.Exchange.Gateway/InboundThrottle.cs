namespace B3.Exchange.Gateway;

/// <summary>
/// Per-session sliding-window inbound rate limiter (issue #56 / GAP-20,
/// guidelines §4.9). Designed for <see cref="FixpSession"/>'s single-threaded
/// receive loop — no internal synchronization.
///
/// <para>Implementation: ring buffer of monotonic millisecond timestamps,
/// sized to <c>maxMessages</c>. On <see cref="TryAccept"/>:
///   • if the ring is not yet full, the new timestamp is appended and
///     the call is accepted;
///   • if the ring is full, the oldest timestamp is compared against
///     <c>now - timeWindowMs</c>: if it has fallen out of the window we
///     overwrite that slot and accept, otherwise we reject and leave the
///     ring untouched (a rejected message does NOT consume a slot — only
///     accepted messages count toward the cap).
/// This yields exact sliding-window semantics in O(1) per call.</para>
///
/// <para>The clock is injected (<c>nowMs</c>) so tests can drive the
/// window forward without relying on wall-clock time. The default in
/// production is <see cref="Environment.TickCount64"/>.</para>
/// </summary>
internal sealed class InboundThrottle
{
    private readonly long[] _timestamps;
    private readonly long _windowMs;
    private readonly Func<long> _nowMs;
    private int _count;
    private int _head;
    private long _accepted;
    private long _rejected;

    public int MaxMessages => _timestamps.Length;
    public long TimeWindowMs => _windowMs;
    public long Accepted => _accepted;
    public long Rejected => _rejected;

    public InboundThrottle(int maxMessages, long timeWindowMs, Func<long>? nowMs = null)
    {
        if (maxMessages <= 0) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        if (timeWindowMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeWindowMs));
        _timestamps = new long[maxMessages];
        _windowMs = timeWindowMs;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// Records an attempted message arrival. Returns <c>true</c> if the
    /// message is within budget (the slot is consumed), or <c>false</c>
    /// if it exceeds the configured rate (no slot consumed).
    /// </summary>
    public bool TryAccept()
    {
        long now = _nowMs();
        if (_count < _timestamps.Length)
        {
            _timestamps[(_head + _count) % _timestamps.Length] = now;
            _count++;
            _accepted++;
            return true;
        }
        long oldest = _timestamps[_head];
        if (now - oldest >= _windowMs)
        {
            _timestamps[_head] = now;
            _head = (_head + 1) % _timestamps.Length;
            _accepted++;
            return true;
        }
        _rejected++;
        return false;
    }

    /// <summary>
    /// Drops every recorded timestamp without resetting the lifetime
    /// accept/reject counters. Called when a session transitions back
    /// into Established (Negotiate completes or a Suspended session
    /// rebinds) so a fresh budget is granted to the new logical session.
    /// </summary>
    public void Reset()
    {
        _count = 0;
        _head = 0;
    }
}
