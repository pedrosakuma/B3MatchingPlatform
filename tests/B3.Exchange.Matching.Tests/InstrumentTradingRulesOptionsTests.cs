using B3.Exchange.Instruments;

namespace B3.Exchange.Matching.Tests;

public class InstrumentTradingRulesOptionsTests
{
    [Fact]
    public void Constructor_OptionInstrument_ExposesOptionMetadataAndAllowsZeroMinPrice()
    {
        var instrument = new Instrument
        {
            Symbol = "PETRH320",
            SecurityId = 900000001234,
            TickSize = 0.01m,
            LotSize = 1,
            MinPrice = 0m,
            MaxPrice = 100m,
            Currency = "BRL",
            Isin = "BROPTEST0001",
            SecurityType = "OPT",
            StrikePrice = 32m,
            ExpirationDate = new DateOnly(2026, 8, 21),
            PutOrCall = PutOrCall.Call,
            ExerciseStyle = ExerciseStyle.American,
            UnderlyingSecurityId = TestFactory.PetrSecId,
            UnderlyingSymbol = "PETR4",
            ContractMultiplier = 100m,
            OptPayoutType = OptPayoutType.Vanilla,
        };

        var rules = new InstrumentTradingRules(instrument);

        Assert.Equal(0, rules.MinPriceMantissa);
        Assert.Equal(100m, rules.ContractMultiplier);
        Assert.Equal(instrument.ExpirationDate.Value.ToDateTime(TimeOnly.MaxValue).Ticks, rules.ExpirationTimestamp);
    }

    [Fact]
    public void Constructor_NonOptionWithZeroMinPrice_Throws()
    {
        var instrument = new Instrument
        {
            Symbol = "PETR4",
            SecurityId = TestFactory.PetrSecId,
            TickSize = 0.01m,
            LotSize = 100,
            MinPrice = 0m,
            MaxPrice = 100m,
            Currency = "BRL",
            Isin = "BRPETRACNPR6",
            SecurityType = "EQUITY",
        };

        Assert.Throws<ArgumentException>(() => _ = new InstrumentTradingRules(instrument));
    }

    [Fact]
    public void Constructor_MarketProtectionFields_ConvertsToEngineScales()
    {
        var instrument = TestFactory.Petr4 with
        {
            AuctionCollarPercent = 2.50m,
            MaxOrderQty = 1_000_000,
            MaxOrderValue = 250_000.25m,
        };

        var rules = new InstrumentTradingRules(instrument);

        Assert.Equal(2_500_000L, rules.AuctionCollarPercentUnits);
        Assert.Equal(1_000_000L, rules.MaxOrderQty);
        Assert.Equal(2_500_002_500L, rules.MaxOrderValueMantissa);
    }
}
