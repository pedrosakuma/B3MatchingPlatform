namespace B3.Exchange.ScenarioReplay;

/// <summary>
/// Pluggable clock so the runner can be exercised under a virtual schedule
/// in tests without real-time waits.
/// </summary>
public interface IClock
{
    /// <summary>Monotonic milliseconds since the clock was started/created.</summary>
    long ElapsedMs { get; }

    /// <summary>Sleep until <see cref="ElapsedMs"/> reaches <paramref name="targetMs"/>.</summary>
    Task DelayUntilAsync(long targetMs, CancellationToken ct);
}

internal sealed class SystemClock : IClock
{
    private readonly long _startTicks = Environment.TickCount64;
    private readonly double _speed;

    public SystemClock(double speed = 1.0)
    {
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        _speed = speed;
    }

    public long ElapsedMs => (long)((Environment.TickCount64 - _startTicks) * _speed);

    public Task DelayUntilAsync(long targetMs, CancellationToken ct)
    {
        var now = ElapsedMs;
        if (targetMs <= now) return Task.CompletedTask;
        // Convert "virtual ms remaining" back into wall-clock ms by dividing
        // by the speed multiplier. speed=10 means 1 virtual ms == 0.1 real ms.
        var realMs = (long)Math.Ceiling((targetMs - now) / _speed);
        return Task.Delay(TimeSpan.FromMilliseconds(realMs), ct);
    }
}
