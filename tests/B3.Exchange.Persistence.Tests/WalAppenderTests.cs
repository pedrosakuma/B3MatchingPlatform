using B3.Exchange.Core;
using B3.Exchange.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Persistence.Tests;

public sealed class WalAppenderTests
{
    private static string NewTempDir() => Directory.CreateTempSubdirectory("wal-app-").FullName;

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

    private static (WalAppender appender, WalDurabilityCoordinator coord, string path) MakeAppender(
        string dir,
        byte channel = 1,
        bool fsyncPerWrite = false,
        long maxBytes = 0,
        WalSizeCapPolicy onFull = WalSizeCapPolicy.Halt)
    {
        var path = Path.Combine(dir, $"channel-{channel}.wal");
        var coord = new WalDurabilityCoordinator(
            channel, groupCommit: false, TimeSpan.Zero, () => 0, NullLogger.Instance);
        var appender = new WalAppender(
            path, dir, channel, fsyncPerWrite, maxBytes, onFull, initialSize: 0, NullLogger.Instance);
        appender.AttachDurability(coord);
        return (appender, coord, path);
    }

    [Fact]
    public void Append_writes_framed_records_on_disk()
    {
        var dir = NewTempDir();
        var (appender, coord, path) = MakeAppender(dir);
        using (appender) using (coord)
        {
            appender.Append(NewRecord(1));
            appender.Append(NewRecord(2));
            appender.Append(NewRecord(3));
            appender.CloseAppendStream();
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        foreach (var line in lines)
        {
            int tab = line.LastIndexOf('\t');
            Assert.True(tab > 0, "missing tab separator");
            Assert.Equal(8, line.Length - tab - 1);
        }
    }

    [Fact]
    public void Append_throws_on_size_cap_halt()
    {
        var dir = NewTempDir();
        var (appender, coord, _) = MakeAppender(dir, maxBytes: 64, onFull: WalSizeCapPolicy.Halt);
        using (appender) using (coord)
        {
            // First write fits; second should exceed and throw.
            Assert.Throws<WalSizeCapExceededException>(() =>
            {
                for (int i = 1; i < 100; i++) appender.Append(NewRecord(i));
            });
        }
    }

    [Fact]
    public void Append_drops_silently_on_size_cap_drop()
    {
        var dir = NewTempDir();
        var (appender, coord, _) = MakeAppender(dir, maxBytes: 64, onFull: WalSizeCapPolicy.Drop);
        using (appender) using (coord)
        {
            for (int i = 1; i < 50; i++) appender.Append(NewRecord(i));
            Assert.True(appender.DropsOnFull > 0);
        }
    }

    [Fact]
    public void Truncate_empties_file_and_resets_size()
    {
        var dir = NewTempDir();
        var (appender, coord, path) = MakeAppender(dir);
        using (appender) using (coord)
        {
            appender.Append(NewRecord(1));
            appender.Append(NewRecord(2));
            Assert.True(appender.CurrentSize > 0);

            appender.Truncate();

            Assert.Equal(0, appender.CurrentSize);
            Assert.Equal(0, new FileInfo(path).Length);
        }
    }

    [Fact]
    public void TruncateThrough_keeps_tail_above_cutoff()
    {
        var dir = NewTempDir();
        var (appender, coord, _) = MakeAppender(dir);
        using (appender) using (coord)
        {
            for (long i = 1; i <= 5; i++) appender.Append(NewRecord(i));

            appender.TruncateThrough(3);

            appender.CloseAppendStream();
            var replay = WalReplay.ReadAll(appender.Path, 1, NullLogger.Instance);
            Assert.Equal(new long[] { 4, 5 }, replay.Records.Select(r => r.Seq));
        }
    }

    [Fact]
    public void TruncateThrough_below_all_records_is_noop()
    {
        var dir = NewTempDir();
        var (appender, coord, _) = MakeAppender(dir);
        using (appender) using (coord)
        {
            for (long i = 5; i <= 7; i++) appender.Append(NewRecord(i));
            long sizeBefore = appender.CurrentSize;

            appender.TruncateThrough(1); // all records have Seq >= 5

            Assert.Equal(sizeBefore, appender.CurrentSize);
            appender.CloseAppendStream();
            var replay = WalReplay.ReadAll(appender.Path, 1, NullLogger.Instance);
            Assert.Equal(3, replay.Records.Count);
        }
    }

    [Fact]
    public void Reset_removes_file()
    {
        var dir = NewTempDir();
        var (appender, coord, path) = MakeAppender(dir);
        using (appender) using (coord)
        {
            appender.Append(NewRecord(1));
            Assert.True(File.Exists(path));

            appender.Reset();

            Assert.False(File.Exists(path));
            Assert.Equal(0, appender.CurrentSize);
        }
    }
}
