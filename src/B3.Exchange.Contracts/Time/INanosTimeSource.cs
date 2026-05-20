namespace B3.Exchange.Contracts.Time;

/// <summary>
/// Single seam for "now" across the per-channel hot path and gateway
/// state machines. Returns Unix-epoch nanoseconds; the production impl
/// wraps <see cref="DateTimeOffset.UtcNow"/> and only delivers
/// millisecond resolution dressed as nanos (the simulator does not
/// need true nanosecond precision). Tests inject a deterministic
/// fake — see <c>tests/.../FakeNanosTimeSource</c>.
///
/// Replaces the per-component <c>Func&lt;ulong&gt;? nowNanos = null</c>
/// constructor parameters and the inline
/// <c>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL</c>
/// arithmetic that had drifted across ~15 sites
/// (issue #382, architectural review 2026-05-20, refactor #3).
/// </summary>
public interface INanosTimeSource
{
    /// <summary>Unix-epoch nanoseconds at the moment of the call.</summary>
    ulong NowNanos();
}
