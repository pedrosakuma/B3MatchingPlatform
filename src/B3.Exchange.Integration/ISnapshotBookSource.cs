using B3.Exchange.Matching;

namespace B3.Exchange.Integration;

/// <summary>
/// Minimal read-only view that <see cref="SnapshotRotator"/> needs over a
/// matching engine to materialise a per-symbol UMDF snapshot. Abstracted so
/// the rotator can be unit-tested with a hand-rolled book without spinning
/// a real <see cref="MatchingEngine"/>.
///
/// All members are invoked on the owning <see cref="ChannelDispatcher"/>'s
/// dispatch thread — implementations need not be thread-safe.
/// </summary>
public interface ISnapshotBookSource
{
    /// <summary>Instruments published on this channel, in rotation order.</summary>
    IReadOnlyList<long> SecurityIds { get; }

    /// <summary>
    /// The last <c>RptSeq</c> value emitted on the incremental channel.
    /// A value of <c>0</c> indicates no incremental traffic has been
    /// published yet (the engine has not produced any MBO / Trade event)
    /// and the rotator should publish an "illiquid" snapshot header per
    /// B3 §7.4 (i.e. <c>lastRptSeq</c> absent).
    /// </summary>
    uint CurrentRptSeq { get; }

    /// <summary>
    /// Iterates the resting orders for <paramref name="securityId"/> on the
    /// requested side in price-time priority. Caller must fully drain the
    /// enumerator before mutating the book.
    /// </summary>
    IEnumerable<RestingOrderView> EnumerateBook(long securityId, Side side);
}

/// <summary>
/// Adapter that exposes a <see cref="MatchingEngine"/> as an
/// <see cref="ISnapshotBookSource"/> for a known set of instruments.
/// </summary>
public sealed class MatchingEngineSnapshotSource : ISnapshotBookSource
{
    private readonly MatchingEngine _engine;
    public IReadOnlyList<long> SecurityIds { get; }

    public MatchingEngineSnapshotSource(MatchingEngine engine, IReadOnlyList<long> securityIds)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(securityIds);
        if (securityIds.Count == 0)
            throw new ArgumentException("securityIds must be non-empty", nameof(securityIds));
        _engine = engine;
        SecurityIds = securityIds;
    }

    public uint CurrentRptSeq => _engine.CurrentRptSeq;

    public IEnumerable<RestingOrderView> EnumerateBook(long securityId, Side side)
        => _engine.EnumerateBook(securityId, side);
}
