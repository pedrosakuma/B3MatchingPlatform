namespace B3.Exchange.ScenarioReplay.Tests;

/// <summary>
/// Unit tests for <see cref="TapeDiff"/>: the regression-diffing harness
/// added in #116. Exercises identical / reordered / ignored-field /
/// truncated / malformed cases.
/// </summary>
public class TapeDiffTests
{
    private static readonly HashSet<string> Defaults =
        new(StringComparer.Ordinal) { "t", "sendingTime" };

    private static string WriteTape(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tapediff-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void IdenticalTapes_AreEquivalent()
    {
        var a = WriteTape(
            "{\"t\":1,\"src\":\"er\",\"execType\":\"new\",\"clOrdId\":1,\"orderId\":42}\n" +
            "{\"t\":2,\"src\":\"er\",\"execType\":\"trade\",\"clOrdId\":1,\"orderId\":42,\"lastQty\":100}\n");
        var b = WriteTape(
            "{\"t\":99,\"src\":\"er\",\"execType\":\"new\",\"clOrdId\":1,\"orderId\":42}\n" +
            "{\"t\":100,\"src\":\"er\",\"execType\":\"trade\",\"clOrdId\":1,\"orderId\":42,\"lastQty\":100}\n");
        try
        {
            var result = TapeDiff.Compare(TapeDiff.Load(a, Defaults), TapeDiff.Load(b, Defaults));
            Assert.True(result.IsEquivalent);
            Assert.Null(result.FirstDivergence);
            Assert.Equal(2, result.BaselineCount);
            Assert.Equal(2, result.CandidateCount);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void ReorderedRecords_Diverge()
    {
        var a = WriteTape(
            "{\"t\":1,\"src\":\"er\",\"execType\":\"new\",\"clOrdId\":1}\n" +
            "{\"t\":2,\"src\":\"er\",\"execType\":\"trade\",\"clOrdId\":1}\n");
        var b = WriteTape(
            "{\"t\":1,\"src\":\"er\",\"execType\":\"trade\",\"clOrdId\":1}\n" +
            "{\"t\":2,\"src\":\"er\",\"execType\":\"new\",\"clOrdId\":1}\n");
        try
        {
            var result = TapeDiff.Compare(TapeDiff.Load(a, Defaults), TapeDiff.Load(b, Defaults));
            Assert.False(result.IsEquivalent);
            Assert.Equal(2, result.Modified);
            Assert.NotNull(result.FirstDivergence);
            Assert.Equal(0, result.FirstDivergence!.Index);
            Assert.Equal(TapeDiff.DivergenceKind.Modified, result.FirstDivergence.Kind);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void IgnoredField_OrderId_TolerateDifference()
    {
        var a = WriteTape("{\"t\":1,\"src\":\"er\",\"execType\":\"new\",\"clOrdId\":1,\"orderId\":42}\n");
        var b = WriteTape("{\"t\":2,\"src\":\"er\",\"execType\":\"new\",\"clOrdId\":1,\"orderId\":99}\n");
        try
        {
            var ignoreWithOrderId = new HashSet<string>(Defaults, StringComparer.Ordinal) { "orderId" };
            var resultDefault = TapeDiff.Compare(TapeDiff.Load(a, Defaults), TapeDiff.Load(b, Defaults));
            Assert.False(resultDefault.IsEquivalent);  // orderId differs by default

            var resultIgnored = TapeDiff.Compare(TapeDiff.Load(a, ignoreWithOrderId), TapeDiff.Load(b, ignoreWithOrderId));
            Assert.True(resultIgnored.IsEquivalent);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void TruncatedCandidate_ReportsOnlyInBaseline()
    {
        var a = WriteTape(
            "{\"t\":1,\"src\":\"er\",\"execType\":\"new\",\"clOrdId\":1}\n" +
            "{\"t\":2,\"src\":\"er\",\"execType\":\"trade\",\"clOrdId\":1}\n");
        var b = WriteTape("{\"t\":1,\"src\":\"er\",\"execType\":\"new\",\"clOrdId\":1}\n");
        try
        {
            var result = TapeDiff.Compare(TapeDiff.Load(a, Defaults), TapeDiff.Load(b, Defaults));
            Assert.False(result.IsEquivalent);
            Assert.Equal(0, result.Modified);
            Assert.Equal(1, result.OnlyInBaseline);
            Assert.Equal(0, result.OnlyInCandidate);
            Assert.NotNull(result.FirstDivergence);
            Assert.Equal(TapeDiff.DivergenceKind.OnlyInBaseline, result.FirstDivergence!.Kind);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void ExtraCandidateLines_ReportsOnlyInCandidate()
    {
        var a = WriteTape("{\"t\":1,\"src\":\"er\",\"execType\":\"new\",\"clOrdId\":1}\n");
        var b = WriteTape(
            "{\"t\":1,\"src\":\"er\",\"execType\":\"new\",\"clOrdId\":1}\n" +
            "{\"t\":2,\"src\":\"er\",\"execType\":\"trade\",\"clOrdId\":1}\n");
        try
        {
            var result = TapeDiff.Compare(TapeDiff.Load(a, Defaults), TapeDiff.Load(b, Defaults));
            Assert.False(result.IsEquivalent);
            Assert.Equal(1, result.OnlyInCandidate);
            Assert.Equal(TapeDiff.DivergenceKind.OnlyInCandidate, result.FirstDivergence!.Kind);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void MalformedTape_Throws()
    {
        var a = WriteTape("not json at all\n");
        try
        {
            Assert.Throws<FormatException>(() => TapeDiff.Load(a, Defaults));
        }
        finally { File.Delete(a); }
    }

    [Fact]
    public void BlankLines_AreSkipped()
    {
        var a = WriteTape("{\"t\":1,\"src\":\"er\",\"execType\":\"new\"}\n\n\n");
        var b = WriteTape("{\"t\":99,\"src\":\"er\",\"execType\":\"new\"}\n");
        try
        {
            var result = TapeDiff.Compare(TapeDiff.Load(a, Defaults), TapeDiff.Load(b, Defaults));
            Assert.True(result.IsEquivalent);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void Report_FirstDivergence_IncludesBothLines()
    {
        var a = WriteTape("{\"t\":1,\"src\":\"er\",\"clOrdId\":1,\"qty\":100}\n");
        var b = WriteTape("{\"t\":1,\"src\":\"er\",\"clOrdId\":1,\"qty\":200}\n");
        try
        {
            var result = TapeDiff.Compare(TapeDiff.Load(a, Defaults), TapeDiff.Load(b, Defaults));
            using var sw = new StringWriter();
            TapeDiff.WriteReport(sw, result, a, b, Defaults);
            var report = sw.ToString();
            Assert.Contains("DIVERGED", report);
            Assert.Contains("\"qty\":100", report);
            Assert.Contains("\"qty\":200", report);
        }
        finally { File.Delete(a); File.Delete(b); }
    }
}
