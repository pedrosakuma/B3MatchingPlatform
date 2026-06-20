namespace B3.Exchange.Instruments;

/// <summary>
/// Static definition of one tradable instrument as configured for the
/// exchange simulator. All fields are required; presence of nullable
/// fields signals "not part of this instrument's identity".
/// </summary>
public sealed record Instrument
{
    public required string Symbol { get; init; }
    public required long SecurityId { get; init; }

    /// <summary>Minimum price increment, e.g. 0.01.</summary>
    public required decimal TickSize { get; init; }

    /// <summary>Minimum tradable quantity / round-lot.</summary>
    public required int LotSize { get; init; }

    public required decimal MinPrice { get; init; }
    public required decimal MaxPrice { get; init; }

    /// <summary>
    /// Optional static lower price-band limit in human price units. When null,
    /// the host does not emit <c>PriceBand_22</c> for this instrument and the
    /// matching engine does not apply a static band.
    /// </summary>
    public decimal? LowerPriceBand { get; init; }

    /// <summary>
    /// Optional static upper price-band limit in human price units. When null,
    /// the host does not emit <c>PriceBand_22</c> for this instrument and the
    /// matching engine does not apply a static band.
    /// </summary>
    public decimal? UpperPriceBand { get; init; }

    /// <summary>
    /// Optional auction collar percentage around the current theoretical
    /// opening price. When null, the matching engine does not apply an
    /// auction-phase TOP collar for this instrument.
    /// </summary>
    public decimal? AuctionCollarPercent { get; init; }

    /// <summary>
    /// Optional per-order quantity ceiling. When null, only the gateway-level
    /// fat-finger ceiling applies.
    /// </summary>
    public long? MaxOrderQty { get; init; }

    /// <summary>
    /// Optional per-order value ceiling in human price units. The matching
    /// engine compares price × quantity in mantissa space.
    /// </summary>
    public decimal? MaxOrderValue { get; init; }

    public required string Currency { get; init; }
    public required string Isin { get; init; }

    /// <summary>FIX SecurityType, e.g. "CS" (common stock), "OPT", "FUT".</summary>
    public required string SecurityType { get; init; }

    public decimal? StrikePrice { get; init; }
    public DateOnly? ExpirationDate { get; init; }
    public PutOrCall? PutOrCall { get; init; }
    public ExerciseStyle? ExerciseStyle { get; init; }
    public long? UnderlyingSecurityId { get; init; }
    public string? UnderlyingSymbol { get; init; }
    public decimal? ContractMultiplier { get; init; }
    public OptPayoutType? OptPayoutType { get; init; }
}

public enum PutOrCall
{
    Put,
    Call,
}

public enum ExerciseStyle
{
    European,
    American,
}

public enum OptPayoutType
{
    Vanilla,
    Capped,
    Binary,
}
