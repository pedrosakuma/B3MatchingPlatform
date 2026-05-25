using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Umdf.WireEncoder;

namespace B3.Exchange.Core;

/// <summary>
/// Periodic incremental-feed publisher for static <c>PriceBand_22</c> frames.
/// The host schedules <see cref="PublishOnce"/> onto the owning dispatch thread
/// so <c>RptSeq</c> allocation stays inside the channel's single-writer domain.
/// </summary>
public sealed class PriceBandPublisher
{
    private readonly Entry[] _entries;

    public TimeSpan Cadence { get; }
    public int Count => _entries.Length;

    public PriceBandPublisher(IReadOnlyList<Instrument> instruments, TimeSpan cadence)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        if (cadence <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cadence), "cadence must be positive");

        Cadence = cadence;
        var entries = new List<Entry>(instruments.Count);
        for (int i = 0; i < instruments.Count; i++)
        {
            var rules = new InstrumentTradingRules(instruments[i]);
            if (!rules.HasPriceBand) continue;
            entries.Add(new Entry(
                rules.Instrument.SecurityId,
                rules.LowerPriceBandMantissa!.Value,
                rules.UpperPriceBandMantissa!.Value));
        }
        _entries = entries.ToArray();
    }

    public int PublishOnce(IUmdfFrameSink sink, Func<uint> nextRptSeq, ulong mdEntryTimestampNanos)
    {
        ArgumentNullException.ThrowIfNull(nextRptSeq);
        if (_entries.Length == 0) return 0;

        foreach (ref readonly var entry in _entries.AsSpan())
        {
            UmdfFrameBuilder.WritePriceBand(
                sink,
                securityId: entry.SecurityId,
                lowLimitPriceMantissa: entry.LowLimitPriceMantissa,
                highLimitPriceMantissa: entry.HighLimitPriceMantissa,
                mdEntryTimestampNanos: mdEntryTimestampNanos,
                rptSeq: nextRptSeq());
        }

        return _entries.Length;
    }

    private readonly record struct Entry(
        long SecurityId,
        long LowLimitPriceMantissa,
        long HighLimitPriceMantissa);
}
