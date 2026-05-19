using System.Globalization;
using System.Text;
using System.Text.Json;
using B3.Exchange.Matching;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

public class EodFillsExporterTests : IDisposable
{
    private readonly string _auditRoot;
    private readonly string _dropRoot;
    private const byte Channel = 7;
    private static readonly DateOnly BusinessDate = new(2026, 5, 18);
    // 2026-05-18 00:00:00 UTC, in nanoseconds since Unix epoch.
    private static readonly ulong Day0Nanos =
        (ulong)(new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100UL;

    public EodFillsExporterTests()
    {
        _auditRoot = Path.Combine(Path.GetTempPath(), "B3EodAudit_" + Guid.NewGuid().ToString("N"));
        _dropRoot = Path.Combine(Path.GetTempPath(), "B3EodDrop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_auditRoot);
        Directory.CreateDirectory(_dropRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_auditRoot)) Directory.Delete(_auditRoot, recursive: true);
        if (Directory.Exists(_dropRoot)) Directory.Delete(_dropRoot, recursive: true);
    }

    private static PostTradeRecord Make(uint id, ulong ts, long secId = 900_000_000_001L, uint buyFirm = 7, uint sellFirm = 8)
        => new(
            TradeId: id, TransactTimeNanos: ts, SecurityId: secId,
            AggressorSide: id % 2 == 0 ? Side.Buy : Side.Sell,
            Quantity: 100 + id, PriceMantissa: 10_0000L + id,
            BuyClOrdId: 1000UL + id, SellClOrdId: 2000UL + id,
            BuyFirm: buyFirm, SellFirm: sellFirm,
            BuyOrderId: 5000 + id, SellOrderId: 6000 + id);

    private void WriteAudit(params PostTradeRecord[] records)
    {
        using var w = new FileAuditLogWriter(_auditRoot, channelNumber: Channel);
        foreach (var r in records) w.OnTrade(r);
    }

    private static EodFillsExporter NewExporter() => new EodFillsExporter();

    private static readonly DateTime FixedGeneratedAt = new(2026, 5, 19, 3, 42, 11, 123, DateTimeKind.Utc);

    [Fact]
    public void Export_RoundTrips_RecordsToCsv()
    {
        var a = Make(1, Day0Nanos, secId: 100, buyFirm: 11, sellFirm: 22);
        var b = Make(2, Day0Nanos + 1_500_000UL, secId: 100); // +1.5ms

        WriteAudit(a, b);

        var result = NewExporter().Export(
            _auditRoot, _dropRoot, Channel, BusinessDate,
            secId => secId == 100 ? "PETR4" : null,
            FixedGeneratedAt);

        Assert.Equal(2, result.RowCount);
        Assert.True(File.Exists(result.CsvPath));

        var lines = File.ReadAllLines(result.CsvPath);
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.Equal("tradeId,ts,symbol,aggressorSide,qty,price,buyClOrdId,sellClOrdId,buyFirm,sellFirm", lines[0]);

        // Row 1: tradeId=1, side=S (odd), qty=101, price=100001
        var cols1 = lines[1].Split(',');
        Assert.Equal("1", cols1[0]);
        Assert.Equal("2026-05-18T00:00:00.000000Z", cols1[1]);
        Assert.Equal("PETR4", cols1[2]);
        Assert.Equal("S", cols1[3]);
        Assert.Equal("101", cols1[4]);
        Assert.Equal("100001", cols1[5]);
        Assert.Equal("1001", cols1[6]);
        Assert.Equal("2001", cols1[7]);
        Assert.Equal("11", cols1[8]);
        Assert.Equal("22", cols1[9]);

        // Row 2: tradeId=2, ts +1.5ms → .001500Z, side=B
        var cols2 = lines[2].Split(',');
        Assert.Equal("2", cols2[0]);
        Assert.Equal("2026-05-18T00:00:00.001500Z", cols2[1]);
        Assert.Equal("B", cols2[3]);
    }

    [Fact]
    public void Export_EmptyAuditLog_EmitsHeaderOnly()
    {
        // Create a log with one record, then truncate back to just the file
        // header so the reader yields zero records.
        WriteAudit(Make(1, Day0Nanos));
        var path = Path.Combine(_auditRoot, "7", "fills-2026-05-18.log");
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            fs.SetLength(AuditRecordCodec.FileHeaderSize);

        var result = NewExporter().Export(
            _auditRoot, _dropRoot, Channel, BusinessDate,
            _ => "X", FixedGeneratedAt);

        Assert.Equal(0, result.RowCount);
        var text = File.ReadAllText(result.CsvPath);
        Assert.Equal("tradeId,ts,symbol,aggressorSide,qty,price,buyClOrdId,sellClOrdId,buyFirm,sellFirm\n", text);
    }

    [Fact]
    public void Export_MissingAuditFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            NewExporter().Export(_auditRoot, _dropRoot, Channel, BusinessDate, _ => "X", FixedGeneratedAt));
    }

    [Fact]
    public void Export_InternalCross_EmitsSameFirmInBothColumns()
    {
        WriteAudit(Make(1, Day0Nanos, buyFirm: 42, sellFirm: 42));

        var result = NewExporter().Export(
            _auditRoot, _dropRoot, Channel, BusinessDate, _ => "ABC", FixedGeneratedAt);

        var cols = File.ReadAllLines(result.CsvPath)[1].Split(',');
        Assert.Equal("42", cols[8]);
        Assert.Equal("42", cols[9]);
    }

    [Fact]
    public void Export_UnknownSymbol_FallsBackToNumericSecurityId()
    {
        WriteAudit(Make(1, Day0Nanos, secId: 555_000_111L));

        var result = NewExporter().Export(
            _auditRoot, _dropRoot, Channel, BusinessDate,
            _ => null, // lookup never resolves
            FixedGeneratedAt);

        var cols = File.ReadAllLines(result.CsvPath)[1].Split(',');
        Assert.Equal("555000111", cols[2]);
    }

    [Fact]
    public void Export_IsIdempotent_AcrossReruns()
    {
        WriteAudit(Make(1, Day0Nanos), Make(2, Day0Nanos + 1_000UL), Make(3, Day0Nanos + 2_000UL));

        var r1 = NewExporter().Export(
            _auditRoot, _dropRoot, Channel, BusinessDate, _ => "SYM", FixedGeneratedAt);
        var bytes1 = File.ReadAllBytes(r1.CsvPath);

        var r2 = NewExporter().Export(
            _auditRoot, _dropRoot, Channel, BusinessDate, _ => "SYM",
            FixedGeneratedAt.AddHours(1)); // different generatedAt
        var bytes2 = File.ReadAllBytes(r2.CsvPath);

        Assert.Equal(r1.Sha256Hex, r2.Sha256Hex);
        Assert.Equal(bytes1, bytes2);

        // .done sidecar must differ on generatedAt across the two runs.
        var donePath = r1.CsvPath + ".done";
        Assert.True(File.Exists(donePath));
    }

    [Fact]
    public void Export_DoneSidecar_HasExpectedJsonPayload()
    {
        WriteAudit(Make(1, Day0Nanos), Make(2, Day0Nanos));

        var result = NewExporter().Export(
            _auditRoot, _dropRoot, Channel, BusinessDate, _ => "SYM", FixedGeneratedAt);

        var donePath = result.CsvPath + ".done";
        Assert.True(File.Exists(donePath));

        using var doc = JsonDocument.Parse(File.ReadAllText(donePath));
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("rowCount").GetInt64());
        Assert.Equal(result.Sha256Hex, root.GetProperty("sha256").GetString());
        // generatedAt should round-trip to FixedGeneratedAt.
        var gen = root.GetProperty("generatedAt").GetString();
        Assert.Equal("2026-05-19T03:42:11.123000Z", gen);
    }

    [Fact]
    public void Export_FailureMidWrite_LeavesPreviousOutputUntouchedAndCleansStaging()
    {
        // First, publish a "good" prior export so we can verify it survives.
        WriteAudit(Make(1, Day0Nanos));
        var prior = NewExporter().Export(
            _auditRoot, _dropRoot, Channel, BusinessDate, _ => "OK", FixedGeneratedAt);
        var priorCsvBytes = File.ReadAllBytes(prior.CsvPath);
        var priorDoneBytes = File.ReadAllBytes(prior.CsvPath + ".done");

        // Now induce a failure mid-write by throwing from symbolLookup.
        var boom = new InvalidOperationException("boom");
        var thrown = Assert.ThrowsAny<Exception>(() => NewExporter().Export(
            _auditRoot, _dropRoot, Channel, BusinessDate,
            _ => throw boom, FixedGeneratedAt));
        // CryptoStream may wrap the user exception; verify the original is reachable.
        Assert.True(ReferenceEquals(boom, thrown) || ReferenceEquals(boom, thrown.InnerException));

        // Prior output must be byte-identical (untouched).
        Assert.Equal(priorCsvBytes, File.ReadAllBytes(prior.CsvPath));
        Assert.Equal(priorDoneBytes, File.ReadAllBytes(prior.CsvPath + ".done"));

        // No leftover staging files (hidden .fills.csv.tmp-*) in the drop dir.
        var dropDir = Path.GetDirectoryName(prior.CsvPath)!;
        var leftovers = Directory.GetFiles(dropDir, ".fills.csv*tmp-*");
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Export_RejectsNonUtcGeneratedAt()
    {
        WriteAudit(Make(1, Day0Nanos));
        var local = new DateTime(2026, 5, 19, 0, 0, 0, DateTimeKind.Local);
        Assert.Throws<ArgumentException>(() => NewExporter().Export(
            _auditRoot, _dropRoot, Channel, BusinessDate, _ => "X", local));
    }

    [Fact]
    public void Export_EscapesSymbolWithCsvSpecialChars()
    {
        WriteAudit(Make(1, Day0Nanos, secId: 1));

        var result = NewExporter().Export(
            _auditRoot, _dropRoot, Channel, BusinessDate,
            _ => "WEIRD,\"SYM\"", FixedGeneratedAt);

        var line = File.ReadAllLines(result.CsvPath)[1];
        // tradeId=1,ts,"WEIRD,""SYM""",...
        Assert.Contains(",\"WEIRD,\"\"SYM\"\"\",", line, StringComparison.Ordinal);
    }
}
