namespace B3.Exchange.ScenarioReplay.Tests;

public class ScriptParserTests
{
    [Fact]
    public void Parses_New_And_Cancel_With_Defaults()
    {
        var script = """
            # comment line ignored
            {"atMs": 0,   "kind": "new",    "clOrdId": 1001, "securityId": 900000000001, "side": "buy",  "qty": 100, "px": 320000}
            {"atMs": 50,  "kind": "new",    "clOrdId": 1002, "securityId": 900000000001, "side": "sell", "type": "limit", "tif": "ioc", "qty": 50,  "px": 320000}
            // also comment
            {"atMs": 200, "kind": "cancel", "clOrdId": 1003, "origClOrdId": 1001, "securityId": 900000000001, "side": "buy"}
            """;
        var events = ScriptParser.Parse(new StringReader(script));
        Assert.Equal(3, events.Count);
        Assert.Equal(ScriptEventKind.New, events[0].Kind);
        Assert.Equal(Tif.Day, events[0].Tif);
        Assert.Equal(OrderType.Limit, events[0].Type);
        Assert.Equal(320000L, events[0].PriceMantissa);
        Assert.Equal(Tif.IOC, events[1].Tif);
        Assert.Equal(ScriptEventKind.Cancel, events[2].Kind);
        Assert.Equal(1001UL, events[2].OrigClOrdId);
    }

    [Fact]
    public void StableSort_PreservesFileOrder_OnTies()
    {
        var script = """
            {"atMs": 5, "kind": "new", "clOrdId": 1, "securityId": 100, "side": "buy", "qty": 10, "px": 1}
            {"atMs": 5, "kind": "new", "clOrdId": 2, "securityId": 100, "side": "buy", "qty": 10, "px": 1}
            {"atMs": 0, "kind": "new", "clOrdId": 3, "securityId": 100, "side": "buy", "qty": 10, "px": 1}
            """;
        var events = ScriptParser.Parse(new StringReader(script));
        Assert.Equal(new ulong[] { 3, 1, 2 }, events.Select(e => e.ClOrdId).ToArray());
    }

    [Fact]
    public void RejectsMalformedJson_WithLineNumber()
    {
        var script = """
            {"atMs": 0, "kind": "new", "clOrdId": 1, "securityId": 100, "side": "buy", "qty": 10, "px": 1}
            {not valid json
            """;
        var ex = Assert.Throws<FormatException>(() => ScriptParser.Parse(new StringReader(script)));
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void Rejects_MissingRequiredField_WithLineNumber()
    {
        var script = """
            {"atMs": 0, "kind": "new", "clOrdId": 1, "side": "buy", "qty": 10, "px": 1}
            """;
        var ex = Assert.Throws<FormatException>(() => ScriptParser.Parse(new StringReader(script)));
        Assert.Contains("securityId", ex.Message);
        Assert.Contains("line 1", ex.Message);
    }

    [Fact]
    public void Rejects_UnknownKind()
    {
        var script = """
            {"atMs": 0, "kind": "modify", "clOrdId": 1, "securityId": 100, "side": "buy", "qty": 10, "px": 1}
            """;
        var ex = Assert.Throws<FormatException>(() => ScriptParser.Parse(new StringReader(script)));
        Assert.Contains("kind", ex.Message);
    }

    [Fact]
    public void Rejects_NonPositiveQty()
    {
        var script = """
            {"atMs": 0, "kind": "new", "clOrdId": 1, "securityId": 100, "side": "buy", "qty": 0, "px": 1}
            """;
        Assert.Throws<FormatException>(() => ScriptParser.Parse(new StringReader(script)));
    }

    [Fact]
    public void Tolerates_UnknownProperties()
    {
        var script = """
            {"atMs": 0, "kind": "new", "clOrdId": 1, "securityId": 100, "side": "buy", "qty": 10, "px": 1, "futureField": "extra"}
            """;
        var events = ScriptParser.Parse(new StringReader(script));
        Assert.Single(events);
    }
}
