using B3.Exchange.Instruments;

namespace B3.Exchange.Instruments.Tests;

public class InstrumentLoaderOptionsTests
{
    [Fact]
    public void Load_OptionInstrument_ParsesOptionFields()
    {
        var inst = InstrumentLoader.LoadFromString(BuildOptionJson(("optPayoutType", "\"Binary\"")))[0];

        Assert.Equal(32.00m, inst.StrikePrice);
        Assert.Equal(new DateOnly(2026, 8, 21), inst.ExpirationDate);
        Assert.Equal(PutOrCall.Call, inst.PutOrCall);
        Assert.Equal(ExerciseStyle.American, inst.ExerciseStyle);
        Assert.Equal(900000000001L, inst.UnderlyingSecurityId);
        Assert.Equal("PETR4", inst.UnderlyingSymbol);
        Assert.Equal(100m, inst.ContractMultiplier);
        Assert.Equal(OptPayoutType.Binary, inst.OptPayoutType);
    }

    [Theory]
    [InlineData("strikePrice")]
    [InlineData("expirationDate")]
    [InlineData("putOrCall")]
    [InlineData("exerciseStyle")]
    [InlineData("underlyingSecurityId")]
    [InlineData("underlyingSymbol")]
    [InlineData("contractMultiplier")]
    public void Load_OptionMissingRequiredField_Throws(string field)
    {
        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(BuildOptionJson(omitFields: [field])));
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void Load_OptionWithoutOptPayoutType_DefaultsToVanilla()
    {
        var inst = InstrumentLoader.LoadFromString(BuildOptionJson(omitFields: ["optPayoutType"]))[0];
        Assert.Equal(OptPayoutType.Vanilla, inst.OptPayoutType);
    }

    [Fact]
    public void Load_OptionWithoutLotSize_DefaultsToOneContract()
    {
        var inst = InstrumentLoader.LoadFromString(BuildOptionJson(omitFields: ["lotSize"]))[0];
        Assert.Equal(1, inst.LotSize);
    }

    [Fact]
    public void Load_OptionLotSizeOtherThanOne_Throws()
    {
        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(BuildOptionJson(("lotSize", "100"))));
        Assert.Contains("lotSize must be 1", ex.Message);
    }

    [Fact]
    public void Load_OptionMinPxZero_IsAccepted()
    {
        var inst = InstrumentLoader.LoadFromString(BuildOptionJson(("minPx", "\"0.00\"")))[0];
        Assert.Equal(0m, inst.MinPrice);
    }

    [Fact]
    public void Load_EquityMinPxZero_IsRejected()
    {
        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(BuildEquityJson(("minPx", "\"0.00\""))));
        Assert.Contains("minPx must be > 0", ex.Message);
    }

    [Theory]
    [InlineData("\"0\"")]
    [InlineData("\"-1\"")]
    public void Load_OptionNonPositiveContractMultiplier_Throws(string contractMultiplier)
    {
        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(BuildOptionJson(("contractMultiplier", contractMultiplier))));
        Assert.Contains("contractMultiplier must be > 0", ex.Message);
    }

    [Fact]
    public void Load_ExpiredOption_Throws()
    {
        var expiredDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).ToString("yyyy-MM-dd");
        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(BuildOptionJson(("expirationDate", $"\"{expiredDate}\""))));
        Assert.Contains("expirationDate must be today or later", ex.Message);
    }

    private static string BuildOptionJson((string Key, string Value)? overrideField = null, string[]? omitFields = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["symbol"] = "\"PETRH320\"",
            ["securityId"] = "900000001234",
            ["tickSize"] = "\"0.01\"",
            ["lotSize"] = "1",
            ["minPx"] = "\"0.01\"",
            ["maxPx"] = "\"100.00\"",
            ["currency"] = "\"BRL\"",
            ["isin"] = "\"BROPTEST0001\"",
            ["securityType"] = "\"OPT\"",
            ["strikePrice"] = "\"32.00\"",
            ["expirationDate"] = "\"2026-08-21\"",
            ["putOrCall"] = "\"Call\"",
            ["exerciseStyle"] = "\"American\"",
            ["underlyingSecurityId"] = "900000000001",
            ["underlyingSymbol"] = "\"PETR4\"",
            ["contractMultiplier"] = "\"100\"",
            ["optPayoutType"] = "\"Vanilla\"",
        };

        if (overrideField is { } pair)
            fields[pair.Key] = pair.Value;

        if (omitFields is not null)
        {
            foreach (var field in omitFields)
                fields.Remove(field);
        }

        return "[{" + string.Join(",", fields.Select(kv => $"\"{kv.Key}\":{kv.Value}")) + "}]";
    }

    private static string BuildEquityJson((string Key, string Value)? overrideField = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["symbol"] = "\"PETR4\"",
            ["securityId"] = "900000000001",
            ["tickSize"] = "\"0.01\"",
            ["lotSize"] = "100",
            ["minPx"] = "\"0.01\"",
            ["maxPx"] = "\"100.00\"",
            ["currency"] = "\"BRL\"",
            ["isin"] = "\"BRPETRACNPR6\"",
            ["securityType"] = "\"CS\"",
        };

        if (overrideField is { } pair)
            fields[pair.Key] = pair.Value;

        return "[{" + string.Join(",", fields.Select(kv => $"\"{kv.Key}\":{kv.Value}")) + "}]";
    }
}
