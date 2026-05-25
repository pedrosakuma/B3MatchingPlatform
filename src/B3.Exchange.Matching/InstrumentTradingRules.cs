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
    public decimal? ContractMultiplier { get; }
    public long? ExpirationTimestamp { get; }
    public long? LowerPriceBandMantissa { get; }
    public long? UpperPriceBandMantissa { get; }
    public bool HasPriceBand => LowerPriceBandMantissa.HasValue && UpperPriceBandMantissa.HasValue;

    public InstrumentTradingRules(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Instrument = instrument;
        TickSizeMantissa = ToMantissaChecked(instrument.TickSize, nameof(instrument.TickSize));
        MinPriceMantissa = ToMantissaChecked(instrument.MinPrice, nameof(instrument.MinPrice));
        MaxPriceMantissa = ToMantissaChecked(instrument.MaxPrice, nameof(instrument.MaxPrice));
        LowerPriceBandMantissa = instrument.LowerPriceBand.HasValue
            ? ToMantissaChecked(instrument.LowerPriceBand.Value, nameof(instrument.LowerPriceBand))
            : null;
        UpperPriceBandMantissa = instrument.UpperPriceBand.HasValue
            ? ToMantissaChecked(instrument.UpperPriceBand.Value, nameof(instrument.UpperPriceBand))
            : null;
        LotSize = instrument.LotSize;
        ContractMultiplier = instrument.ContractMultiplier;
        ExpirationTimestamp = instrument.ExpirationDate is { } expirationDate
            ? expirationDate.ToDateTime(TimeOnly.MaxValue).Ticks
            : null;

        var isOption = InstrumentSecurityTypes.IsOption(instrument.SecurityType);

        if (TickSizeMantissa <= 0) throw new ArgumentException("TickSize must be > 0 after scale", nameof(instrument));
        if (LotSize <= 0) throw new ArgumentException("LotSize must be > 0", nameof(instrument));
        if (MinPriceMantissa < 0 || (!isOption && MinPriceMantissa == 0))
            throw new ArgumentException(isOption ? "MinPrice must be >= 0 after scale" : "MinPrice must be > 0 after scale", nameof(instrument));
        if (MaxPriceMantissa < MinPriceMantissa) throw new ArgumentException("MaxPrice < MinPrice", nameof(instrument));
        if (MinPriceMantissa % TickSizeMantissa != 0) throw new ArgumentException("MinPrice not a multiple of TickSize", nameof(instrument));
        if (MaxPriceMantissa % TickSizeMantissa != 0) throw new ArgumentException("MaxPrice not a multiple of TickSize", nameof(instrument));
        if (LowerPriceBandMantissa.HasValue != UpperPriceBandMantissa.HasValue)
            throw new ArgumentException("LowerPriceBand and UpperPriceBand must both be set or both be null", nameof(instrument));
        if (LowerPriceBandMantissa is { } lower)
        {
            if (lower < MinPriceMantissa || lower > MaxPriceMantissa)
                throw new ArgumentException("LowerPriceBand outside instrument bounds", nameof(instrument));
            if (lower % TickSizeMantissa != 0)
                throw new ArgumentException("LowerPriceBand not a multiple of TickSize", nameof(instrument));
        }
        if (UpperPriceBandMantissa is { } upper)
        {
            if (upper < MinPriceMantissa || upper > MaxPriceMantissa)
                throw new ArgumentException("UpperPriceBand outside instrument bounds", nameof(instrument));
            if (upper % TickSizeMantissa != 0)
                throw new ArgumentException("UpperPriceBand not a multiple of TickSize", nameof(instrument));
        }
        if (LowerPriceBandMantissa is { } low && UpperPriceBandMantissa is { } high && high < low)
            throw new ArgumentException("UpperPriceBand < LowerPriceBand", nameof(instrument));
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
