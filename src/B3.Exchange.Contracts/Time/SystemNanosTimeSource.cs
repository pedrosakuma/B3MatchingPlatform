namespace B3.Exchange.Contracts.Time;

/// <summary>
/// Production <see cref="INanosTimeSource"/> backed by
/// <see cref="DateTimeOffset.UtcNow"/>. Singleton — use
/// <see cref="Instance"/>; allocating new instances is wasteful and
/// makes test substitution harder.
/// </summary>
public sealed class SystemNanosTimeSource : INanosTimeSource
{
    public static readonly SystemNanosTimeSource Instance = new();

    private SystemNanosTimeSource() { }

    public ulong NowNanos()
        => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
}
