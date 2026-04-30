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

    public required string Currency { get; init; }
    public required string Isin { get; init; }

    /// <summary>FIX SecurityType, e.g. "CS" (common stock), "OPT", "FUT".</summary>
    public required string SecurityType { get; init; }
}
