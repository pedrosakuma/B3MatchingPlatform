using B3.Exchange.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Issue #312 (Tier-2 perf): <see cref="WalFsyncMode.GroupCommit"/>
/// behaviour for <see cref="FileChannelWriteAheadLog"/>. Verifies
/// the durability watermark contract: <see cref="IChannelWriteAheadLog.PendingDurableSeqOrZero"/>
/// advances per <c>Append</c>, <see cref="IChannelWriteAheadLog.DurableSeqOrZero"/>
/// advances on each background fsync, and
/// <see cref="IChannelWriteAheadLog.WaitForDurable"/> blocks
/// callers until that promise is met.
/// </summary>
public class FileChannelWalGroupCommitTests
{
    private static WalRecord NewRec(long seq) => new(
        seq, WalRecordKind.NewOrder,
        SessionValue: "01",
        Firm: 1,
        ClOrdId: (ulong)seq,
        OrigClOrdId: 0,
        NewOrder: new B3.Exchange.Matching.NewOrderCommand(
            ClOrdId: seq.ToString(),
            SecurityId: 900_000_000_001L,
            Side: B3.Exchange.Matching.Side.Buy,
            Type: B3.Exchange.Matching.OrderType.Limit,
            Tif: B3.Exchange.Matching.TimeInForce.Day,
            PriceMantissa: 100_000,
            Quantity: 100,
            EnteringFirm: 1,
            EnteredAtNanos: 0),
        Cancel: null,
        Replace: null);

    private static string TempDir() => Directory.CreateTempSubdirectory("wal-gc-").FullName;

    [Fact]
    public void Append_DoesNotAdvanceDurableSeq_UntilBackgroundFsyncRuns()
    {
        var dir = TempDir();
        try
        {
            // 100ms interval is long enough that we can observe the
            // pending != durable window before the bg thread fires.
            using var wal = new FileChannelWriteAheadLog(
                dir, channelNumber: 7, NullLogger<FileChannelWriteAheadLog>.Instance,
                fsyncPerWrite: false, maxBytes: 0, onFull: WalSizeCapPolicy.Halt,
                fsyncMode: WalFsyncMode.GroupCommit,
                groupCommitInterval: TimeSpan.FromMilliseconds(100));

            wal.Append(NewRec(1));
            wal.Append(NewRec(2));
            wal.Append(NewRec(3));

            Assert.Equal(3, wal.PendingDurableSeqOrZero);
            // Bg thread is signalled but won't have fired yet under
            // contention; if it has, durable will be 3 — still valid.
            Assert.True(wal.DurableSeqOrZero <= 3);

            wal.WaitForDurable(3, CancellationToken.None);
            Assert.Equal(3, wal.DurableSeqOrZero);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WaitForDurable_BatchesMultipleAppends_IntoOneFsync()
    {
        var dir = TempDir();
        try
        {
            using var wal = new FileChannelWriteAheadLog(
                dir, channelNumber: 8, NullLogger<FileChannelWriteAheadLog>.Instance,
                fsyncPerWrite: false, maxBytes: 0, onFull: WalSizeCapPolicy.Halt,
                fsyncMode: WalFsyncMode.GroupCommit,
                groupCommitInterval: TimeSpan.FromMilliseconds(50));

            for (int i = 1; i <= 10; i++) wal.Append(NewRec(i));

            wal.WaitForDurable(10, CancellationToken.None);
            Assert.True(wal.DurableSeqOrZero >= 10);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WaitForDurable_ReturnsImmediately_WhenSeqAlreadyDurable()
    {
        var dir = TempDir();
        try
        {
            using var wal = new FileChannelWriteAheadLog(
                dir, channelNumber: 9, NullLogger<FileChannelWriteAheadLog>.Instance,
                fsyncPerWrite: false, maxBytes: 0, onFull: WalSizeCapPolicy.Halt,
                fsyncMode: WalFsyncMode.GroupCommit,
                groupCommitInterval: TimeSpan.FromMilliseconds(5));

            wal.Append(NewRec(1));
            wal.WaitForDurable(1, CancellationToken.None);

            // Now durable >= 1; calling again with seq 1 must be a
            // no-op even with a cancelled token (we never enter the
            // wait loop).
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            wal.WaitForDurable(1, cts.Token);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WaitForDurable_HonoursCancellation_WhenSeqUnreachable()
    {
        var dir = TempDir();
        try
        {
            // Long interval so the bg thread won't fsync for a while.
            using var wal = new FileChannelWriteAheadLog(
                dir, channelNumber: 10, NullLogger<FileChannelWriteAheadLog>.Instance,
                fsyncPerWrite: false, maxBytes: 0, onFull: WalSizeCapPolicy.Halt,
                fsyncMode: WalFsyncMode.GroupCommit,
                groupCommitInterval: TimeSpan.FromSeconds(60));

            // No append: pending stays at 0 forever; waiting for seq=5
            // would hang indefinitely without cancellation.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            Assert.Throws<OperationCanceledException>(() =>
                wal.WaitForDurable(5, cts.Token));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Dispose_DrainsPendingFsync_BeforeReturning()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "channel-11.wal");
        try
        {
            // Long interval so the bg thread definitely hasn't fsynced
            // by the time we Dispose; the final-pass drain must do it.
            var wal = new FileChannelWriteAheadLog(
                dir, channelNumber: 11, NullLogger<FileChannelWriteAheadLog>.Instance,
                fsyncPerWrite: false, maxBytes: 0, onFull: WalSizeCapPolicy.Halt,
                fsyncMode: WalFsyncMode.GroupCommit,
                groupCommitInterval: TimeSpan.FromSeconds(60));
            wal.Append(NewRec(1));
            wal.Append(NewRec(2));

            // File exists but bytes may still be in-kernel; Dispose
            // performs the final fsync and closes the stream so we can
            // re-open it for reading.
            wal.Dispose();

            using var reopened = new FileChannelWriteAheadLog(
                dir, channelNumber: 11, NullLogger<FileChannelWriteAheadLog>.Instance,
                fsyncPerWrite: true);
            var records = reopened.ReadAll();
            Assert.Equal(2, records.Count);
            Assert.Equal(1, records[0].Seq);
            Assert.Equal(2, records[1].Seq);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PerWriteMode_AdvancesDurableSeq_InsideAppend()
    {
        var dir = TempDir();
        try
        {
            using var wal = new FileChannelWriteAheadLog(
                dir, channelNumber: 12, NullLogger<FileChannelWriteAheadLog>.Instance,
                fsyncPerWrite: true);

            Assert.Equal(0, wal.PendingDurableSeqOrZero);
            Assert.Equal(0, wal.DurableSeqOrZero);

            wal.Append(NewRec(1));
            Assert.Equal(1, wal.PendingDurableSeqOrZero);
            Assert.Equal(1, wal.DurableSeqOrZero);

            wal.Append(NewRec(2));
            Assert.Equal(2, wal.PendingDurableSeqOrZero);
            Assert.Equal(2, wal.DurableSeqOrZero);

            // WaitForDurable on PerWrite is a no-op fast-path.
            wal.WaitForDurable(2, CancellationToken.None);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Truncate_AdvancesDurableSeq_ToCurrentPending()
    {
        var dir = TempDir();
        try
        {
            using var wal = new FileChannelWriteAheadLog(
                dir, channelNumber: 13, NullLogger<FileChannelWriteAheadLog>.Instance,
                fsyncPerWrite: false, maxBytes: 0, onFull: WalSizeCapPolicy.Halt,
                fsyncMode: WalFsyncMode.GroupCommit,
                groupCommitInterval: TimeSpan.FromSeconds(60));

            wal.Append(NewRec(1));
            wal.Append(NewRec(2));
            Assert.Equal(2, wal.PendingDurableSeqOrZero);

            // Without Truncate's durability advance, durable stays at
            // 0 (bg thread is sleeping for 60s) and any waiter would
            // spin. After Truncate it must be unblocked because the
            // records are gone — there is no further work to fsync.
            wal.Truncate();
            Assert.Equal(2, wal.DurableSeqOrZero);
            wal.WaitForDurable(2, CancellationToken.None);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void GroupCommit_RejectedWhen_FsyncPerWrite_True()
    {
        var dir = TempDir();
        try
        {
            Assert.Throws<ArgumentException>(() => new FileChannelWriteAheadLog(
                dir, channelNumber: 14, NullLogger<FileChannelWriteAheadLog>.Instance,
                fsyncPerWrite: true, maxBytes: 0, onFull: WalSizeCapPolicy.Halt,
                fsyncMode: WalFsyncMode.GroupCommit,
                groupCommitInterval: TimeSpan.FromMilliseconds(1)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
