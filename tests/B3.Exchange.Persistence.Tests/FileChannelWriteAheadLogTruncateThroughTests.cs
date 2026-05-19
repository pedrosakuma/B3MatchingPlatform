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
/// Issue #348: <c>TruncateThrough(seq)</c> must drop records with
/// <c>Seq &lt;= seq</c> and KEEP records with <c>Seq &gt; seq</c>.
/// Closes the async-snapshot truncate race where the dispatch thread
/// could append a record past the snapshot's <c>LastAppliedSeq</c>
/// between <c>BackgroundSnapshotWriter.Submit</c> and the writer
/// thread's <c>onSaved</c> callback firing.
/// </summary>
public class FileChannelWriteAheadLogTruncateThroughTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "wal-prefix-" + Guid.NewGuid().ToString("N"));

        public TempDir() { Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    private static WalRecord MakeNew(long seq)
        => new(seq, WalRecordKind.NewOrder, "S", 1u, (ulong)seq, 0,
            new NewOrderCommand(ClOrdId: seq.ToString(), SecurityId: 1, Side: Side.Buy,
                Type: OrderType.Limit, Tif: TimeInForce.Day, PriceMantissa: 100_0000,
                Quantity: 10, EnteringFirm: 1u, EnteredAtNanos: 0),
            null, null);

    [Fact]
    public void TruncateThrough_KeepsTail_BeyondCutoff()
    {
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        for (int i = 1; i <= 5; i++) wal.Append(MakeNew(i));

        wal.TruncateThrough(3);

        var read = wal.ReadAll();
        Assert.Equal(2, read.Count);
        Assert.Equal(4L, read[0].Seq);
        Assert.Equal(5L, read[1].Seq);
        Assert.Equal(0, wal.LastReadCorruptCount);
        Assert.Equal(0, wal.LastReadLegacyCount);
    }

    [Fact]
    public void TruncateThrough_NoTail_EqualsFullTruncate()
    {
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        for (int i = 1; i <= 3; i++) wal.Append(MakeNew(i));

        wal.TruncateThrough(10);

        Assert.Empty(wal.ReadAll());
        Assert.Equal(0, wal.CurrentSizeBytes);
    }

    [Fact]
    public void TruncateThrough_CutoffBelowAllRecords_IsNoOp()
    {
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        for (int i = 5; i <= 7; i++) wal.Append(MakeNew(i));
        long sizeBefore = wal.CurrentSizeBytes;

        wal.TruncateThrough(0);

        var read = wal.ReadAll();
        Assert.Equal(3, read.Count);
        Assert.Equal(new long[] { 5, 6, 7 }, read.Select(r => r.Seq).ToArray());
        Assert.Equal(sizeBefore, wal.CurrentSizeBytes);
    }

    [Fact]
    public void TruncateThrough_PreservesAppendContinuity()
    {
        // After a prefix truncate the next Append must produce a file
        // that ReadAll round-trips back as the surviving tail + the
        // new record. Guards against stale append-stream offsets or
        // a torn rename.
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        for (int i = 1; i <= 4; i++) wal.Append(MakeNew(i));

        wal.TruncateThrough(2);
        wal.Append(MakeNew(5));

        var read = wal.ReadAll();
        Assert.Equal(new long[] { 3, 4, 5 }, read.Select(r => r.Seq).ToArray());
        Assert.Equal(0, wal.LastReadCorruptCount);
    }

    [Fact]
    public void TruncateThrough_CurrentSizeBytes_ReflectsSurvivingTail()
    {
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(dir.Path, channelNumber: 7,
            NullLogger<FileChannelWriteAheadLog>.Instance, fsyncPerWrite: false);
        for (int i = 1; i <= 6; i++) wal.Append(MakeNew(i));
        long sizeFull = wal.CurrentSizeBytes;

        wal.TruncateThrough(4);

        long sizeAfter = wal.CurrentSizeBytes;
        Assert.True(sizeAfter > 0, "kept records → non-zero size");
        Assert.True(sizeAfter < sizeFull, $"prefix truncate must shrink the file (full={sizeFull}, after={sizeAfter})");
        // Round-trip sanity: on-disk size must match what the file
        // actually has after the atomic rename.
        long onDisk = new FileInfo(System.IO.Path.Combine(dir.Path, "channel-7.wal")).Length;
        Assert.Equal(onDisk, sizeAfter);
    }
}
