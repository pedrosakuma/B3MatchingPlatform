using System.Text;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using B3.Exchange.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Persistence.Tests;

public sealed class WalReplayTests
{
    private static string NewTempDir() => Directory.CreateTempSubdirectory("wal-rpl-").FullName;

    private static WalRecord NewRecord(long seq)
        => new(
            seq,
            WalRecordKind.NewOrder,
            SessionValue: "S",
            Firm: 1u,
            ClOrdId: (ulong)seq,
            OrigClOrdId: 0,
            NewOrder: null,
            Cancel: null,
            Replace: null);

    private static void WriteFramedLine(FileStream fs, byte[] json)
    {
        uint crc = Crc32C.Compute(json);
        fs.Write(json, 0, json.Length);
        fs.WriteByte((byte)'\t');
        var hex = crc.ToString("X8");
        fs.Write(Encoding.ASCII.GetBytes(hex));
        fs.WriteByte((byte)'\n');
    }

    private static byte[] SerializeRecord(WalRecord rec)
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rec, WalJsonContext.Default.WalRecord);

    [Fact]
    public void ReadAll_returns_empty_when_file_missing()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "channel-1.wal");

        var result = WalReplay.ReadAll(path, 1, NullLogger.Instance);

        Assert.Empty(result.Records);
        Assert.Equal(0, result.CorruptCount);
        Assert.Equal(0, result.LegacyCount);
    }

    [Fact]
    public void ReadAll_reads_back_framed_records()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "channel-1.wal");
        var recs = new[] { NewRecord(1), NewRecord(2), NewRecord(3) };
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            foreach (var r in recs)
            {
                WriteFramedLine(fs, SerializeRecord(r));
            }
        }

        var result = WalReplay.ReadAll(path, 1, NullLogger.Instance);

        Assert.Equal(3, result.Records.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, result.Records.Select(r => r.Seq));
        Assert.Equal(0, result.CorruptCount);
        Assert.Equal(0, result.LegacyCount);
    }

    [Fact]
    public void ReadAll_tolerates_torn_final_crc()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "channel-1.wal");
        var good = new[] { NewRecord(1), NewRecord(2) };
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            foreach (var r in good)
            {
                WriteFramedLine(fs, SerializeRecord(r));
            }
            // Final line with bad CRC.
            var json = SerializeRecord(NewRecord(3));
            fs.Write(json, 0, json.Length);
            fs.WriteByte((byte)'\t');
            fs.Write(Encoding.ASCII.GetBytes("DEADBEEF"));
            fs.WriteByte((byte)'\n');
        }

        var result = WalReplay.ReadAll(path, 1, NullLogger.Instance);

        Assert.Equal(2, result.Records.Count);
        Assert.Equal(1, result.CorruptCount);
    }

    [Fact]
    public void ReadAll_throws_on_midstream_crc_mismatch()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "channel-1.wal");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            WriteFramedLine(fs, SerializeRecord(NewRecord(1)));
            // Bad CRC in the middle.
            var json = SerializeRecord(NewRecord(2));
            fs.Write(json, 0, json.Length);
            fs.WriteByte((byte)'\t');
            fs.Write(Encoding.ASCII.GetBytes("DEADBEEF"));
            fs.WriteByte((byte)'\n');
            WriteFramedLine(fs, SerializeRecord(NewRecord(3)));
        }

        Assert.Throws<WalCorruptionException>(() =>
            WalReplay.ReadAll(path, 1, NullLogger.Instance));
    }

    [Fact]
    public void ReadAll_throws_on_seq_gap()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "channel-1.wal");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            WriteFramedLine(fs, SerializeRecord(NewRecord(1)));
            WriteFramedLine(fs, SerializeRecord(NewRecord(2)));
            // Gap: skip 3.
            WriteFramedLine(fs, SerializeRecord(NewRecord(4)));
        }

        Assert.Throws<WalCorruptionException>(() =>
            WalReplay.ReadAll(path, 1, NullLogger.Instance));
    }

    [Fact]
    public void ReadAll_counts_legacy_lines_without_crc()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "channel-1.wal");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            // Two legacy records (no CRC suffix), followed by one modern.
            var l1 = SerializeRecord(NewRecord(1));
            fs.Write(l1, 0, l1.Length);
            fs.WriteByte((byte)'\n');
            var l2 = SerializeRecord(NewRecord(2));
            fs.Write(l2, 0, l2.Length);
            fs.WriteByte((byte)'\n');
            WriteFramedLine(fs, SerializeRecord(NewRecord(3)));
        }

        var result = WalReplay.ReadAll(path, 1, NullLogger.Instance);

        Assert.Equal(3, result.Records.Count);
        Assert.Equal(2, result.LegacyCount);
        Assert.Equal(0, result.CorruptCount);
    }

    [Fact]
    public void ReplaceOrderCommand_CurrentMarketDate_RoundTripsThroughWalJson()
    {
        var replace = new ReplaceOrderCommand("r1", 900_000_000_001L, 123L, 100_000L, 100L, 2_000UL)
        {
            NewTif = TimeInForce.Gtd,
            NewExpireDate = 19_999,
            CurrentMarketDate = 20_000,
        };
        var rec = new WalRecord(1, WalRecordKind.Replace, "S", 1u, 2u, 1u, null, null, replace);

        var json = SerializeRecord(rec);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize(json, WalJsonContext.Default.WalRecord);

        Assert.NotNull(roundTrip);
        Assert.Equal((ushort)20_000, roundTrip.Replace!.CurrentMarketDate);

        var legacyJson = Encoding.UTF8.GetString(json).Replace(",\"CurrentMarketDate\":20000", "", StringComparison.Ordinal);
        var legacy = System.Text.Json.JsonSerializer.Deserialize(legacyJson, WalJsonContext.Default.WalRecord);
        Assert.NotNull(legacy);
        Assert.Null(legacy.Replace!.CurrentMarketDate);
    }

    [Fact]
    public void ReadAll_tolerates_legacy_parse_fail_on_final_line()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "channel-1.wal");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var l1 = SerializeRecord(NewRecord(1));
            fs.Write(l1, 0, l1.Length);
            fs.WriteByte((byte)'\n');
            // Final legacy line with broken JSON.
            fs.Write(Encoding.UTF8.GetBytes("{not valid json"));
            fs.WriteByte((byte)'\n');
        }

        var result = WalReplay.ReadAll(path, 1, NullLogger.Instance);

        Assert.Single(result.Records);
        Assert.Equal(1, result.Records[0].Seq);
        Assert.Equal(1, result.LegacyCount);
    }
}
