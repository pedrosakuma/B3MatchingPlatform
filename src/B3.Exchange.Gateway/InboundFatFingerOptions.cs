namespace B3.Exchange.Gateway;

/// <summary>
/// Decode-time pre-trade guardrails applied before commands enter Core.
/// Prices use the EntryPoint implicit /10000 mantissa representation.
/// </summary>
public sealed record InboundFatFingerOptions
{
    public const long DefaultMaxOrderQty = 1_000_000_000L;

    /// <summary>
    /// Extremely loose default: 100 billion in decimal price terms
    /// (1e15 mantissa). B3 securities generally trade many orders of
    /// magnitude below this; keeping the default wide preserves simulator
    /// flexibility while rejecting corrupted 64-bit payloads.
    /// </summary>
    public const long DefaultMaxPriceMantissa = 1_000_000_000_000_000L;

    public long MaxOrderQty { get; init; } = DefaultMaxOrderQty;
    public long MaxPriceMantissa { get; init; } = DefaultMaxPriceMantissa;
    public decimal? PriceBandPercent { get; init; }
    public Func<long, long?>? LastTradePriceProvider { get; init; }

    /// <summary>
    /// #504: supplies the current B3 local market date as a wire
    /// <c>LocalMktDate</c> (days since the Unix epoch) so the decoder can
    /// reject a GTD <c>NewOrderSingle</c> whose <c>ExpireDate</c> is already
    /// in the past at entry time, rather than letting it rest until the next
    /// daily-reset sweep. Host-supplied and read only on the live decode
    /// path — never during WAL replay (replay rebuilds commands from WAL
    /// records, bypassing the decoder), so the engine stays clockless
    /// (ADR 0009) and replay stays deterministic. Must not throw; return
    /// <c>null</c> to skip the check (e.g. the date is outside the
    /// <see cref="ushort"/> LocalMktDate range or unconfigured).
    /// </summary>
    public Func<ushort?>? CurrentMarketDateProvider { get; init; }

    public static InboundFatFingerOptions Default { get; } = new();

    public void Validate()
    {
        if (MaxOrderQty <= 0) throw new ArgumentOutOfRangeException(nameof(MaxOrderQty));
        if (MaxPriceMantissa <= 0) throw new ArgumentOutOfRangeException(nameof(MaxPriceMantissa));
        if (PriceBandPercent is <= 0m) throw new ArgumentOutOfRangeException(nameof(PriceBandPercent));
    }
}
