using B3.Exchange.Instruments;

namespace B3.Exchange.Matching;

/// <summary>
/// Resolved per-instrument validation parameters in mantissa space (4-decimal
/// scale, matching the SBE Price/PriceOptional Exponent=-4 used by UMDF).
/// </summary>
public sealed class InstrumentTradingRules
{
    /// <summary>Multiplier from a decimal price to a long mantissa
    /// (10^4 = 10_000 for the V16 MBO Price/PriceOptional encoding).</summary>
    public const long PriceScale = 10_000L;

    public Instrument Instrument { get; }
    public long TickSizeMantissa { get; }
    public long MinPriceMantissa { get; }
    public long MaxPriceMantissa { get; }
    public long LotSize { get; }

    public InstrumentTradingRules(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Instrument = instrument;
        TickSizeMantissa = ToMantissaChecked(instrument.TickSize, nameof(instrument.TickSize));
        MinPriceMantissa = ToMantissaChecked(instrument.MinPrice, nameof(instrument.MinPrice));
        MaxPriceMantissa = ToMantissaChecked(instrument.MaxPrice, nameof(instrument.MaxPrice));
        LotSize = instrument.LotSize;
        if (TickSizeMantissa <= 0) throw new ArgumentException("TickSize must be > 0 after scale", nameof(instrument));
        if (LotSize <= 0) throw new ArgumentException("LotSize must be > 0", nameof(instrument));
        if (MinPriceMantissa <= 0) throw new ArgumentException("MinPrice must be > 0 after scale", nameof(instrument));
        if (MaxPriceMantissa < MinPriceMantissa) throw new ArgumentException("MaxPrice < MinPrice", nameof(instrument));
        if (MinPriceMantissa % TickSizeMantissa != 0) throw new ArgumentException("MinPrice not a multiple of TickSize", nameof(instrument));
        if (MaxPriceMantissa % TickSizeMantissa != 0) throw new ArgumentException("MaxPrice not a multiple of TickSize", nameof(instrument));
    }

    private static long ToMantissaChecked(decimal value, string name)
    {
        decimal scaled = value * PriceScale;
        decimal rounded = Math.Truncate(scaled);
        if (rounded != scaled)
            throw new ArgumentException($"{name}={value} has more than 4 decimal places", name);
        if (scaled > long.MaxValue || scaled < long.MinValue)
            throw new ArgumentException($"{name}={value} overflows mantissa range", name);
        return (long)scaled;
    }
}
