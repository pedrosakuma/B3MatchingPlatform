using B3.Exchange.Contracts.Time;

namespace B3.Exchange.TestSupport;

/// <summary>
/// Deterministic <see cref="INanosTimeSource"/> for unit tests.
/// Returns the value last set by <see cref="Set"/> / <see cref="Advance"/>
/// or 0 by default. Not thread-safe — call from a single test thread.
/// </summary>
public sealed class FakeNanosTimeSource : INanosTimeSource
{
    private ulong _now;

    public FakeNanosTimeSource(ulong initial = 0) { _now = initial; }

    public ulong NowNanos() => _now;

    public void Set(ulong nanos) => _now = nanos;

    public void Advance(ulong deltaNanos) => _now += deltaNanos;
}
