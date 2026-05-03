using B3.Exchange.Instruments;

namespace B3.Exchange.Bench;

internal static class BenchInstruments
{
    public const long PetrSecId = 900_000_000_001L;

    public static Instrument Petr4 => new()
    {
        Symbol = "PETR4",
        SecurityId = PetrSecId,
        TickSize = 0.01m,
        LotSize = 100,
        MinPrice = 0.01m,
        MaxPrice = 1_000.00m,
        Currency = "BRL",
        Isin = "BRPETRACNPR6",
        SecurityType = "EQUITY",
    };

    /// <summary>10000 mantissa per 1.00 BRL.</summary>
    public static long Px(decimal p) => (long)(p * 10_000m);
}
