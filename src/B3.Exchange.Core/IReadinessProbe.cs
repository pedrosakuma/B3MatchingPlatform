namespace B3.Exchange.Core;

/// <summary>
/// Single readiness signal. Composes additively: the host's overall
/// readiness is the AND of every registered probe.
///
/// Designed so issue #1 (snapshot rotator) and issue #2 (instrument
/// definition publisher) can each register a probe that flips to ready
/// once they have emitted at least one snapshot/definition per loaded
/// instrument since startup. Until those land, the host registers a
/// single <see cref="StartupReadinessProbe"/> that goes ready as soon
/// as <see cref="ExchangeHost.StartAsync"/> returns.
/// </summary>
public interface IReadinessProbe
{
    /// <summary>Stable identifier for diagnostics (e.g. "startup", "snapshot-rotator").</summary>
    string Name { get; }

    /// <summary>Returns true once this subsystem considers itself ready.</summary>
    bool IsReady { get; }
}

/// <summary>
/// Trivial probe flipped by an explicit <see cref="MarkReady"/> call.
/// Used by the host to mark itself ready once startup completes.
/// </summary>
public sealed class StartupReadinessProbe : IReadinessProbe
{
    private int _ready;

    public StartupReadinessProbe(string name = "startup")
    {
        Name = name;
    }

    public string Name { get; }

    public bool IsReady => Volatile.Read(ref _ready) == 1;

    public void MarkReady() => Interlocked.Exchange(ref _ready, 1);
}
