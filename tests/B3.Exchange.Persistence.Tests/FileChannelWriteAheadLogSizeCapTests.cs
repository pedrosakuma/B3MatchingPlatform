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
/// Issue #291: <see cref="FileChannelWriteAheadLog"/> enforces an
/// optional <c>maxBytes</c> cap with two policies — Halt (default,
/// throws <see cref="WalSizeCapExceededException"/>) and Drop
/// (silently skips the write + bumps
/// <see cref="FileChannelWriteAheadLog.DropsOnFullCount"/>). Both
/// policies leave <see cref="FileChannelWriteAheadLog.CurrentSizeBytes"/>
/// at the pre-overflow value (drops do not advance it; halts never
/// reach the disk).
/// </summary>
public class FileChannelWriteAheadLogSizeCapTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "wal-cap-" + Guid.NewGuid().ToString("N"));
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
    public void NoCap_AcceptsArbitraryAppends()
    {
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(
            dir.Path, channelNumber: 1,
            NullLogger<FileChannelWriteAheadLog>.Instance,
            fsyncPerWrite: false, maxBytes: 0, onFull: WalSizeCapPolicy.Halt);
        for (long s = 1; s <= 50; s++) wal.Append(MakeNew(s, (ulong)s));
        Assert.Equal(50, wal.ReadAll().Count);
        Assert.True(wal.CurrentSizeBytes > 0);
        Assert.Equal(0, wal.DropsOnFullCount);
    }

    [Fact]
    public void Halt_Throws_WhenAppendWouldExceedCap()
    {
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(
            dir.Path, channelNumber: 1,
            NullLogger<FileChannelWriteAheadLog>.Instance,
            fsyncPerWrite: false, maxBytes: 512, onFull: WalSizeCapPolicy.Halt);

        long seq = 1;
        // Fill until the next append would exceed.
        Assert.Throws<WalSizeCapExceededException>(() =>
        {
            for (; seq <= 1_000; seq++) wal.Append(MakeNew(seq, (ulong)seq));
        });

        long sizeAfterThrow = wal.CurrentSizeBytes;
        Assert.True(sizeAfterThrow <= 512);
        Assert.Equal(0, wal.DropsOnFullCount);

        // Subsequent attempts should also throw (no silent advance).
        Assert.Throws<WalSizeCapExceededException>(() => wal.Append(MakeNew(seq, (ulong)seq)));
        Assert.Equal(sizeAfterThrow, wal.CurrentSizeBytes);
    }

    [Fact]
    public void Drop_SilentlySkips_AndBumpsCounter()
    {
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(
            dir.Path, channelNumber: 1,
            NullLogger<FileChannelWriteAheadLog>.Instance,
            fsyncPerWrite: false, maxBytes: 512, onFull: WalSizeCapPolicy.Drop);

        for (long s = 1; s <= 1_000; s++) wal.Append(MakeNew(s, (ulong)s));

        Assert.True(wal.CurrentSizeBytes <= 512);
        Assert.True(wal.DropsOnFullCount > 0);
        // Records that fit are durable.
        var records = wal.ReadAll();
        Assert.True(records.Count > 0);
        Assert.True(records.Count + wal.DropsOnFullCount == 1_000);
    }

    [Fact]
    public void Truncate_ResetsCurrentSize_AndAllowsAppendsAgain()
    {
        using var dir = new TempDir();
        using var wal = new FileChannelWriteAheadLog(
            dir.Path, channelNumber: 1,
            NullLogger<FileChannelWriteAheadLog>.Instance,
            fsyncPerWrite: false, maxBytes: 512, onFull: WalSizeCapPolicy.Halt);

        try { for (long s = 1; s <= 1_000; s++) wal.Append(MakeNew(s, (ulong)s)); }
        catch (WalSizeCapExceededException) { /* expected */ }

        Assert.True(wal.CurrentSizeBytes > 0);
        wal.Truncate();
        Assert.Equal(0, wal.CurrentSizeBytes);

        // Cap is still enforced post-truncate but with a fresh budget.
        wal.Append(MakeNew(1, 1));
        Assert.True(wal.CurrentSizeBytes > 0);
        Assert.True(wal.CurrentSizeBytes <= 512);
    }

    [Fact]
    public void CurrentSize_RehydratedFromDisk_OnReopen()
    {
        using var dir = new TempDir();
        long sizeAfterFirstSession;
        using (var wal = new FileChannelWriteAheadLog(
            dir.Path, channelNumber: 1,
            NullLogger<FileChannelWriteAheadLog>.Instance,
            fsyncPerWrite: true, maxBytes: 0, onFull: WalSizeCapPolicy.Halt))
        {
            for (long s = 1; s <= 5; s++) wal.Append(MakeNew(s, (ulong)s));
            sizeAfterFirstSession = wal.CurrentSizeBytes;
            Assert.True(sizeAfterFirstSession > 0);
        }

        using var reopened = new FileChannelWriteAheadLog(
            dir.Path, channelNumber: 1,
            NullLogger<FileChannelWriteAheadLog>.Instance,
            fsyncPerWrite: true, maxBytes: 0, onFull: WalSizeCapPolicy.Halt);
        Assert.Equal(sizeAfterFirstSession, reopened.CurrentSizeBytes);
    }
}
