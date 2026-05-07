using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Matching;
using B3.Exchange.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Side = B3.Exchange.Matching.Side;
using OrderType = B3.Exchange.Matching.OrderType;
using TimeInForce = B3.Exchange.Matching.TimeInForce;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Issue #285: WAL records carry a Crc32C suffix; on read a record
/// whose CRC does not match its bytes is dropped (counted via
/// <see cref="FileChannelWriteAheadLog.LastReadCorruptCount"/>) and
/// replay continues past it. Pre-#285 records (no CRC suffix) load
/// as legacy and are counted via
/// <see cref="FileChannelWriteAheadLog.LastReadLegacyCount"/>.
/// </summary>
public class FileChannelWriteAheadLogCrcTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "wal-crc-" + Guid.NewGuid().ToString("N"));

        public TempDir() { Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    private static WalRecord MakeNew(long seq, ulong clOrdId)
        => new(seq, WalRecordKind.NewOrder, "S", 1u, clOrdId, 0,
            new NewOrderCommand(ClOrdId: clOrdId.ToString(), SecurityId: 1, Side: Side.Buy,
                Type: OrderType.Limit, Tif: TimeInForce.Day, PriceMantissa: 100_0000,
                Quantity: 10, EnteringFirm: 1u, EnteredAtNanos: 0),
            null, null);

    [Fact]
    public void Append_Then_Read_RoundTrip_With_Crc()
    {
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        for (int i = 1; i <= 5; i++) wal.Append(MakeNew(i, (ulong)i));
        var read = wal.ReadAll();
        Assert.Equal(5, read.Count);
        Assert.Equal(0, wal.LastReadCorruptCount);
        Assert.Equal(0, wal.LastReadLegacyCount);
    }

    [Fact]
    public void CorruptMiddleRecord_IsSkipped_AndReplayContinues()
    {
        using var dir = new TempDir();
        var walPath = System.IO.Path.Combine(dir.Path, "channel-7.wal");
        using (var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            for (int i = 1; i <= 3; i++) wal.Append(MakeNew(i, (ulong)i));
        }
        // Flip a single byte inside the JSON payload of the middle
        // line to force a CRC mismatch on that record only.
        var bytes = File.ReadAllBytes(walPath);
        // Find the second '\n' and corrupt a byte midway in the
        // second line's JSON (i.e. between the first \n and second \n).
        int firstNl = Array.IndexOf(bytes, (byte)'\n');
        int secondNl = Array.IndexOf(bytes, (byte)'\n', firstNl + 1);
        Assert.True(firstNl >= 0 && secondNl > firstNl);
        int target = firstNl + 5;
        bytes[target] ^= 0xFF;
        File.WriteAllBytes(walPath, bytes);

        using var reread = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var records = reread.ReadAll();
        Assert.Equal(2, records.Count);
        Assert.Equal(1, reread.LastReadCorruptCount);
        Assert.Equal(0, reread.LastReadLegacyCount);
        // Records 1 and 3 survived; the corrupt one in the middle was skipped.
        Assert.Contains(records, r => r.Seq == 1);
        Assert.Contains(records, r => r.Seq == 3);
        Assert.DoesNotContain(records, r => r.Seq == 2);
    }

    [Fact]
    public void LegacyLine_NoCrcSuffix_IsAcceptedAndCounted()
    {
        using var dir = new TempDir();
        var walPath = System.IO.Path.Combine(dir.Path, "channel-7.wal");
        // Hand-craft a single legacy record (pre-#285 format: bare
        // JSON + \n, no \t<crc>) so we exercise the migration path.
        var rec = MakeNew(seq: 99, clOrdId: 99);
        var json = System.Text.Json.JsonSerializer.Serialize(rec, new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });
        File.WriteAllText(walPath, json + "\n");

        using var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var records = wal.ReadAll();
        Assert.Single(records);
        Assert.Equal(99L, records[0].Seq);
        Assert.Equal(0, wal.LastReadCorruptCount);
        Assert.Equal(1, wal.LastReadLegacyCount);
    }

    [Fact]
    public void TornFinalLine_StopsReplay_PreservingPriorRecords()
    {
        using var dir = new TempDir();
        var walPath = System.IO.Path.Combine(dir.Path, "channel-7.wal");
        using (var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            wal.Append(MakeNew(1, 1));
            wal.Append(MakeNew(2, 2));
        }
        // Append a half-written line (no terminator → readline still
        // returns it as the final line; missing CRC suffix → falls to
        // legacy parse → fails JSON → stops replay).
        File.AppendAllText(walPath, "{\"Seq\":3,\"Kind\":");

        using var reread = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var records = reread.ReadAll();
        Assert.Equal(2, records.Count);
        Assert.Equal(0, reread.LastReadCorruptCount);
    }

    [Fact]
    public void LegacyAndModern_Mixed_SurvivesReplay()
    {
        using var dir = new TempDir();
        var walPath = System.IO.Path.Combine(dir.Path, "channel-7.wal");
        // Write a legacy record by hand, then append a modern one
        // through the WAL writer.
        var rec1 = MakeNew(1, 1);
        var json1 = System.Text.Json.JsonSerializer.Serialize(rec1, new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });
        File.WriteAllText(walPath, json1 + "\n");

        using (var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            wal.Append(MakeNew(2, 2));
        }

        using var reread = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var records = reread.ReadAll();
        Assert.Equal(2, records.Count);
        Assert.Equal(1, reread.LastReadLegacyCount);
        Assert.Equal(0, reread.LastReadCorruptCount);
    }
}
