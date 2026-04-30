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
