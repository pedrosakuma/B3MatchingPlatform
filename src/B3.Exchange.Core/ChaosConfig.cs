namespace B3.Exchange.Core;

/// <summary>
/// Configuration for <see cref="ChaosUdpPacketSinkDecorator"/>. Probabilities
/// are independent and evaluated in order: drop &gt; duplicate &gt; reorder.
/// All probabilities default to 0 (no chaos). When all are 0 the decorator
/// degenerates to a pass-through.
///
/// <para>Reorder semantics: when a packet is selected for reorder it is
/// placed into a small in-memory queue with a random release-lag in the
/// range <c>[1, ReorderMaxLagPackets]</c>. Subsequent calls to
/// <see cref="ChaosUdpPacketSinkDecorator.Publish"/> decrement the lag of
/// every queued packet and release those whose lag has reached 0 — emitted
/// after the current packet so they appear later in the wire stream.</para>
///
/// <para>The seeded <see cref="System.Random"/> makes runs deterministic for
/// regression tests. Operator runs typically pass a non-zero seed too so
/// drop patterns are reproducible across host restarts.</para>
/// </summary>
public sealed class ChaosConfig
{
    public double DropProbability { get; init; }
    public double DuplicateProbability { get; init; }
    public double ReorderProbability { get; init; }
    public int ReorderMaxLagPackets { get; init; } = 3;
    public int Seed { get; init; }

    public bool IsActive =>
        DropProbability > 0 || DuplicateProbability > 0 || ReorderProbability > 0;

    /// <summary>Throws <see cref="ArgumentException"/> if any probability is
    /// outside [0,1] or <c>ReorderMaxLagPackets</c> is non-positive while
    /// reorder is enabled.</summary>
    public void Validate()
    {
        if (DropProbability < 0 || DropProbability > 1)
            throw new ArgumentException("DropProbability must be in [0, 1]", nameof(DropProbability));
        if (DuplicateProbability < 0 || DuplicateProbability > 1)
            throw new ArgumentException("DuplicateProbability must be in [0, 1]", nameof(DuplicateProbability));
        if (ReorderProbability < 0 || ReorderProbability > 1)
            throw new ArgumentException("ReorderProbability must be in [0, 1]", nameof(ReorderProbability));
        if (ReorderProbability > 0 && ReorderMaxLagPackets <= 0)
            throw new ArgumentException("ReorderMaxLagPackets must be > 0 when reorder is enabled",
                nameof(ReorderMaxLagPackets));
    }
}
