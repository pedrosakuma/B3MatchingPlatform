using B3.Exchange.Instruments;

namespace B3.Exchange.Instruments.Tests;

public class InstrumentLoaderTests
{
    private const string ValidSingle = """
        [
          { "symbol": "PETR4", "securityId": 900000000001,
            "tickSize": "0.01", "lotSize": 100,
            "minPx": "0.01", "maxPx": "999999.99",
            "currency": "BRL", "isin": "BRPETRACNPR6", "securityType": "CS" }
        ]
        """;

    [Fact]
    public void Load_ValidSingleInstrument_ReturnsOneInstrument()
    {
        var insts = InstrumentLoader.LoadFromString(ValidSingle);
        Assert.Single(insts);
        var i = insts[0];
        Assert.Equal("PETR4", i.Symbol);
        Assert.Equal(900000000001L, i.SecurityId);
        Assert.Equal(0.01m, i.TickSize);
        Assert.Equal(100, i.LotSize);
        Assert.Equal(0.01m, i.MinPrice);
        Assert.Equal(999999.99m, i.MaxPrice);
        Assert.Equal("BRL", i.Currency);
        Assert.Equal("BRPETRACNPR6", i.Isin);
        Assert.Equal("CS", i.SecurityType);
        Assert.Null(i.LowerPriceBand);
        Assert.Null(i.UpperPriceBand);
        Assert.Null(i.AuctionCollarPercent);
        Assert.Null(i.MaxOrderQty);
        Assert.Null(i.MaxOrderValue);
    }

    [Fact]
    public void Load_DecimalAsNumber_AlsoAccepted()
    {
        var json = "[ { \"symbol\":\"X\", \"securityId\":1, \"tickSize\":0.01, \"lotSize\":1," +
                   "\"minPx\":1.00, \"maxPx\":2.00, \"currency\":\"BRL\", \"isin\":\"X\", \"securityType\":\"CS\" } ]";
        var insts = InstrumentLoader.LoadFromString(json);
        Assert.Equal(0.01m, insts[0].TickSize);
    }

    [Fact]
    public void Load_DuplicateSymbol_Throws()
    {
        var json = """
            [
              { "symbol":"X", "securityId":1, "tickSize":"0.01", "lotSize":1,
                "minPx":"1", "maxPx":"2", "currency":"BRL", "isin":"X", "securityType":"CS" },
              { "symbol":"X", "securityId":2, "tickSize":"0.01", "lotSize":1,
                "minPx":"1", "maxPx":"2", "currency":"BRL", "isin":"Y", "securityType":"CS" }
            ]
            """;
        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(json));
        Assert.Contains("Duplicate symbol", ex.Message);
    }

    [Fact]
    public void Load_DuplicateSecurityId_Throws()
    {
        var json = """
            [
              { "symbol":"A", "securityId":1, "tickSize":"0.01", "lotSize":1,
                "minPx":"1", "maxPx":"2", "currency":"BRL", "isin":"X", "securityType":"CS" },
              { "symbol":"B", "securityId":1, "tickSize":"0.01", "lotSize":1,
                "minPx":"1", "maxPx":"2", "currency":"BRL", "isin":"Y", "securityType":"CS" }
            ]
            """;
        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(json));
        Assert.Contains("Duplicate securityId", ex.Message);
    }

    [Theory]
    [InlineData("symbol", "")]
    [InlineData("securityId", "0")]
    [InlineData("tickSize", "\"0\"")]
    [InlineData("lotSize", "0")]
    [InlineData("minPx", "\"0\"")]
    [InlineData("maxPx", "\"0\"")]
    [InlineData("currency", "\" \"")]
    [InlineData("isin", "\" \"")]
    [InlineData("securityType", "\" \"")]
    public void Load_MissingRequiredField_Throws(string field, string badValue)
    {
        var fields = new Dictionary<string, string>
        {
            ["symbol"] = "\"X\"",
            ["securityId"] = "1",
            ["tickSize"] = "\"0.01\"",
            ["lotSize"] = "1",
            ["minPx"] = "\"1\"",
            ["maxPx"] = "\"2\"",
            ["currency"] = "\"BRL\"",
            ["isin"] = "\"BR\"",
            ["securityType"] = "\"CS\"",
        };
        // Empty string for symbol is the "missing" signal in JSON terms.
        fields[field] = field == "symbol" ? "\"\"" : badValue;
        var json = "[{" + string.Join(",", fields.Select(kv => $"\"{kv.Key}\":{kv.Value}")) + "}]";
        Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(json));
    }

    [Fact]
    public void Load_MaxBelowMin_Throws()
    {
        var json = "[{\"symbol\":\"X\",\"securityId\":1,\"tickSize\":\"0.01\",\"lotSize\":1," +
                   "\"minPx\":\"5\",\"maxPx\":\"2\",\"currency\":\"BRL\",\"isin\":\"X\",\"securityType\":\"CS\"}]";
        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(json));
        Assert.Contains("maxPx", ex.Message);
    }

    [Fact]
    public void Load_PriceNotMultipleOfTick_Throws()
    {
        var json = "[{\"symbol\":\"X\",\"securityId\":1,\"tickSize\":\"0.05\",\"lotSize\":1," +
                   "\"minPx\":\"0.01\",\"maxPx\":\"1.00\",\"currency\":\"BRL\",\"isin\":\"X\",\"securityType\":\"CS\"}]";
        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(json));
        Assert.Contains("multiple of tickSize", ex.Message);
    }

    [Fact]
    public void Load_AllowsCommentsAndTrailingCommas()
    {
        var json = """
            [
              // PETR4 common stock
              { "symbol":"PETR4", "securityId":1, "tickSize":"0.01", "lotSize":100,
                "minPx":"0.01", "maxPx":"999.99", "currency":"BRL", "isin":"BR", "securityType":"CS", },
            ]
            """;
        var insts = InstrumentLoader.LoadFromString(json);
        Assert.Single(insts);
    }

    [Fact]
    public void Load_WithStaticPriceBands_RoundTripsOptionalFields()
    {
        var json = """
            [
              {
                "symbol": "PETR4",
                "securityId": 1,
                "tickSize": "0.01",
                "lotSize": 100,
                "minPx": "10.00",
                "maxPx": "99.99",
                "lowerPriceBand": "11.50",
                "upperPriceBand": "18.25",
                "auctionCollarPercent": "5.50",
                "maxOrderQty": 1000000,
                "maxOrderValue": "250000.00",
                "currency": "BRL",
                "isin": "BRPETRACNPR6",
                "securityType": "CS"
              }
            ]
            """;

        var inst = Assert.Single(InstrumentLoader.LoadFromString(json));
        Assert.Equal(11.50m, inst.LowerPriceBand);
        Assert.Equal(18.25m, inst.UpperPriceBand);
        Assert.Equal(5.50m, inst.AuctionCollarPercent);
        Assert.Equal(1_000_000L, inst.MaxOrderQty);
        Assert.Equal(250_000.00m, inst.MaxOrderValue);
    }

    [Theory]
    [InlineData("auctionCollarPercent", "\"0\"")]
    [InlineData("maxOrderQty", "0")]
    [InlineData("maxOrderValue", "\"0\"")]
    public void Load_InvalidOptionalMarketProtection_Throws(string field, string badValue)
    {
        var json = """
            [
              {
                "symbol": "PETR4",
                "securityId": 1,
                "tickSize": "0.01",
                "lotSize": 100,
                "minPx": "10.00",
                "maxPx": "99.99",
                "currency": "BRL",
                "isin": "BRPETRACNPR6",
                "securityType": "CS",
                "__FIELD__": __VALUE__
              }
            ]
            """.Replace("__FIELD__", field, StringComparison.Ordinal)
                .Replace("__VALUE__", badValue, StringComparison.Ordinal);

        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(json));
        Assert.Contains(field, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_WithOnlyOnePriceBandBound_Throws()
    {
        var json = """
            [
              {
                "symbol": "PETR4",
                "securityId": 1,
                "tickSize": "0.01",
                "lotSize": 100,
                "minPx": "10.00",
                "maxPx": "99.99",
                "lowerPriceBand": "11.50",
                "currency": "BRL",
                "isin": "BRPETRACNPR6",
                "securityType": "CS"
              }
            ]
            """;

        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString(json));
        Assert.Contains("lowerPriceBand and upperPriceBand", ex.Message);
    }

    [Fact]
    public void Load_MalformedJson_ThrowsInstrumentConfigException()
    {
        var ex = Assert.Throws<InstrumentConfigException>(() => InstrumentLoader.LoadFromString("not json"));
        Assert.Contains("Failed to parse", ex.Message);
    }

    [Fact]
    public void LoadFromFile_RoundTrip()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, ValidSingle);
            var insts = InstrumentLoader.LoadFromFile(tmp);
            Assert.Single(insts);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
