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
    public void CorruptMiddleRecord_FailsReplay_WithWalCorruptionException()
    {
        // Issue #285 follow-up (gpt-5.5 review of PR #293): silently
        // skipping a mid-stream corrupted record would let replay
        // apply state transitions out of order and fabricate a book
        // state that never existed. The loader must refuse to do
        // that — only a torn FINAL record is tolerable.
        using var dir = new TempDir();
        var walPath = System.IO.Path.Combine(dir.Path, "channel-7.wal");
        using (var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            for (int i = 1; i <= 3; i++) wal.Append(MakeNew(i, (ulong)i));
        }
        var bytes = File.ReadAllBytes(walPath);
        int firstNl = Array.IndexOf(bytes, (byte)'\n');
        int secondNl = Array.IndexOf(bytes, (byte)'\n', firstNl + 1);
        Assert.True(firstNl >= 0 && secondNl > firstNl);
        int target = firstNl + 5;
        bytes[target] ^= 0xFF;
        File.WriteAllBytes(walPath, bytes);

        using var reread = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var ex = Assert.Throws<B3.Exchange.Core.WalCorruptionException>(() => reread.ReadAll());
        Assert.Equal((byte)7, ex.ChannelNumber);
        Assert.Equal(2, ex.RecordNumber);
        Assert.Equal(1, reread.LastReadCorruptCount);
    }

    [Fact]
    public void CorruptFinalRecord_IsToleratedAsTornTail()
    {
        using var dir = new TempDir();
        var walPath = System.IO.Path.Combine(dir.Path, "channel-7.wal");
        using (var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            for (int i = 1; i <= 3; i++) wal.Append(MakeNew(i, (ulong)i));
        }
        // Flip a byte inside the JSON of the LAST line — torn-tail
        // semantics: the final record is dropped, prefix survives.
        var bytes = File.ReadAllBytes(walPath);
        int secondNl = -1, thirdNl = -1;
        for (int i = 0, n = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                n++;
                if (n == 2) secondNl = i;
                else if (n == 3) { thirdNl = i; break; }
            }
        }
        Assert.True(secondNl > 0 && thirdNl > secondNl);
        bytes[secondNl + 5] ^= 0xFF;
        File.WriteAllBytes(walPath, bytes);

        using var reread = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var records = reread.ReadAll();
        Assert.Equal(2, records.Count);
        Assert.Equal(1, reread.LastReadCorruptCount);
        Assert.Contains(records, r => r.Seq == 1);
        Assert.Contains(records, r => r.Seq == 2);
        Assert.DoesNotContain(records, r => r.Seq == 3);
    }

    [Fact]
    public void NonContiguousSeq_FailsReplay_WithWalCorruptionException()
    {
        // Hand-craft a WAL where seq jumps 1 → 3 (record with seq=2
        // is missing). This is the exact failure mode the seq
        // contiguity check is built to catch — without it, the
        // engine would replay r1 + r3 and end up in a state that
        // never existed at runtime.
        using var dir = new TempDir();
        var walPath = System.IO.Path.Combine(dir.Path, "channel-7.wal");
        using (var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false))
        {
            wal.Append(MakeNew(1, 1));
            wal.Append(MakeNew(3, 3));   // intentional gap at seq=2
        }

        using var reread = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        var ex = Assert.Throws<B3.Exchange.Core.WalCorruptionException>(() => reread.ReadAll());
        Assert.Equal((byte)7, ex.ChannelNumber);
        Assert.Equal(2, ex.RecordNumber);
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
