using B3.Exchange.Contracts;
using B3.Exchange.Core;
using B3.Exchange.Instruments;
using B3.Exchange.Matching;
using B3.Exchange.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Side = B3.Exchange.Matching.Side;
using TimeInForce = B3.Exchange.Matching.TimeInForce;

namespace B3.Exchange.Persistence.Tests;

public class FileChannelStatePersisterTests
{
    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        using var dir = new TempDir();
        var persister = new FileChannelStatePersister(dir.Path,
            NullLogger<FileChannelStatePersister>.Instance);

        var snapshot = MakeRichSnapshot(channel: 84);

        persister.Save(snapshot);
        var loaded = persister.TryLoad(84);

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Version, loaded!.Version);
        Assert.Equal(snapshot.ChannelNumber, loaded.ChannelNumber);
        Assert.Equal(snapshot.SequenceNumber, loaded.SequenceNumber);
        Assert.Equal(snapshot.SequenceVersion, loaded.SequenceVersion);
        Assert.Equal(snapshot.Engine.NextOrderId, loaded.Engine.NextOrderId);
        Assert.Equal(snapshot.Engine.NextTradeId, loaded.Engine.NextTradeId);
        Assert.Equal(snapshot.Engine.RptSeqBySecurity, loaded.Engine.RptSeqBySecurity);
        Assert.Equal(snapshot.Engine.Phases.Count, loaded.Engine.Phases.Count);
        Assert.Equal(snapshot.Engine.Books.Count, loaded.Engine.Books.Count);

        var origBook = snapshot.Engine.Books.Single();
        var loadedBook = loaded.Engine.Books.Single();
        Assert.Equal(origBook.SecurityId, loadedBook.SecurityId);
        Assert.Equal(origBook.Orders.Count, loadedBook.Orders.Count);
        for (int i = 0; i < origBook.Orders.Count; i++)
            Assert.Equal(origBook.Orders[i], loadedBook.Orders[i]);

        Assert.Equal(snapshot.Owners.Count, loaded.Owners.Count);
        for (int i = 0; i < snapshot.Owners.Count; i++)
            Assert.Equal(snapshot.Owners[i], loaded.Owners[i]);
    }

    [Fact]
    public void TryLoad_ReturnsNull_WhenNoSnapshot()
    {
        using var dir = new TempDir();
        var persister = new FileChannelStatePersister(dir.Path,
            NullLogger<FileChannelStatePersister>.Instance);

        Assert.Null(persister.TryLoad(99));
    }

    [Fact]
    public void Save_OverwritesPreviousSnapshot_Atomically()
    {
        using var dir = new TempDir();
        var persister = new FileChannelStatePersister(dir.Path,
            NullLogger<FileChannelStatePersister>.Instance);

        persister.Save(MakeRichSnapshot(channel: 7) with { SequenceNumber = 1 });
        persister.Save(MakeRichSnapshot(channel: 7) with { SequenceNumber = 42 });

        var loaded = persister.TryLoad(7);
        Assert.NotNull(loaded);
        Assert.Equal(42u, loaded!.SequenceNumber);
        // tmp file must not leak
        Assert.False(File.Exists(Path.Combine(dir.Path, "channel-7.snapshot.tmp")));
    }

    [Fact]
    public void TryLoad_ReturnsNull_OnCorruptFile()
    {
        using var dir = new TempDir();
        var persister = new FileChannelStatePersister(dir.Path,
            NullLogger<FileChannelStatePersister>.Instance);

        File.WriteAllText(Path.Combine(dir.Path, "channel-3.snapshot"), "{not json");

        Assert.Null(persister.TryLoad(3));
    }

    // -------- Issue #264: snapshot rotation (N generations) --------

    [Fact]
    public void Rotation_RoundRobinsAcrossSlots()
    {
        // generations=3 → slot files .0, .1, .2 created in turn.
        using var dir = new TempDir();
        var persister = new FileChannelStatePersister(dir.Path,
            NullLogger<FileChannelStatePersister>.Instance, generations: 3);
        for (int i = 1; i <= 3; i++)
            persister.Save(MakeRichSnapshot(channel: 5) with { SequenceNumber = (uint)i });

        Assert.True(File.Exists(Path.Combine(dir.Path, "channel-5.snapshot.0")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "channel-5.snapshot.1")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "channel-5.snapshot.2")));

        // 4th save reuses slot 0 (round-robin).
        persister.Save(MakeRichSnapshot(channel: 5) with { SequenceNumber = 99 });
        Assert.Equal(99u, persister.TryLoad(5)!.SequenceNumber);
    }

    [Fact]
    public void Rotation_LoadFallsBackToPreviousGeneration_WhenNewestCorrupted()
    {
        // Newest file is structurally invalid → persister must return
        // the previous generation's snapshot, not null.
        using var dir = new TempDir();
        var persister = new FileChannelStatePersister(dir.Path,
            NullLogger<FileChannelStatePersister>.Instance, generations: 3);

        persister.Save(MakeRichSnapshot(channel: 9) with { SequenceNumber = 10 });
        persister.Save(MakeRichSnapshot(channel: 9) with { SequenceNumber = 20 });

        // Find which slot got the newest snapshot (should be slot .1)
        // and corrupt it. Use a tiny sleep to ensure mtime ordering is
        // deterministic on filesystems with low timer resolution.
        Thread.Sleep(50);
        var newest = Path.Combine(dir.Path, "channel-9.snapshot.1");
        Assert.True(File.Exists(newest));
        File.WriteAllText(newest, "{garbage");
        // Bump mtime to make sure it remains "newest" by mtime.
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow);

        var loaded = persister.TryLoad(9);
        Assert.NotNull(loaded);
        Assert.Equal(10u, loaded!.SequenceNumber);
    }

    [Fact]
    public void Rotation_DiscoversNewestSlot_AcrossPersisterRestart()
    {
        // Persister A writes 3 snapshots → slots .0, .1, .2 = seq 1,2,3.
        // Persister B (new instance, same dir) must pick slot .0 next
        // (round-robin from newest=.2), not overwrite slot .0 first.
        using var dir = new TempDir();
        var a = new FileChannelStatePersister(dir.Path,
            NullLogger<FileChannelStatePersister>.Instance, generations: 3);
        for (int i = 1; i <= 3; i++)
        {
            a.Save(MakeRichSnapshot(channel: 11) with { SequenceNumber = (uint)i });
            Thread.Sleep(20); // deterministic mtime ordering
        }

        var b = new FileChannelStatePersister(dir.Path,
            NullLogger<FileChannelStatePersister>.Instance, generations: 3);
        // Newest from A is seq=3 in slot .2; after B's first Save, the
        // newest available should still be seq=3 (slot .2 untouched
        // because B writes to slot .0 next).
        Thread.Sleep(20);
        b.Save(MakeRichSnapshot(channel: 11) with { SequenceNumber = 100 });

        // After this Save, newest is seq=100 in slot .0.
        Assert.Equal(100u, b.TryLoad(11)!.SequenceNumber);
        // Slot .2 (seq=3 from A) must still be intact for rollback.
        var slot2 = File.ReadAllText(Path.Combine(dir.Path, "channel-11.snapshot.2"));
        Assert.Contains("\"SequenceNumber\":3", slot2);
    }

    [Fact]
    public void Rotation_LegacyFileIsLoadedAndThenRetired()
    {
        // A pre-#264 deployment has a single channel-7.snapshot file.
        // First load must return it; first Save must replace it with a
        // generational slot file and remove the legacy.
        using var dir = new TempDir();
        var bootstrap = new FileChannelStatePersister(dir.Path,
            NullLogger<FileChannelStatePersister>.Instance, generations: 3);
        // Write a legacy file by serializing the snapshot under the
        // legacy name directly. We use the persister to produce a
        // valid JSON, then move the slot file into the legacy name.
        bootstrap.Save(MakeRichSnapshot(channel: 7) with { SequenceNumber = 42 });
        var legacy = Path.Combine(dir.Path, "channel-7.snapshot");
        File.Move(Path.Combine(dir.Path, "channel-7.snapshot.0"), legacy);

        var persister = new FileChannelStatePersister(dir.Path,
            NullLogger<FileChannelStatePersister>.Instance, generations: 3);
        Assert.Equal(42u, persister.TryLoad(7)!.SequenceNumber);

        persister.Save(MakeRichSnapshot(channel: 7) with { SequenceNumber = 99 });
        Assert.False(File.Exists(legacy), "legacy file should be removed after first generational save");
        Assert.Equal(99u, persister.TryLoad(7)!.SequenceNumber);
    }

    [Fact]
    public void Rotation_GenerationsArgument_Validated()
    {
        using var dir = new TempDir();
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileChannelStatePersister(
            dir.Path, NullLogger<FileChannelStatePersister>.Instance, generations: 0));
    }

    [Fact]
    public async Task AsyncSaveCapturedBeforeDeleteAll_IsFenced_AndNewGenerationDrainsOnShutdown()
    {
        using var dir = new TempDir();
        var inner = new FileChannelStatePersister(
            dir.Path, NullLogger<FileChannelStatePersister>.Instance);
        var blocking = new BlockingGenerationPersister(inner);
        int savedCallbacks = 0;
        await using var writer = new BackgroundSnapshotWriter(
            channelNumber: 84,
            persister: blocking,
            logger: NullLogger<BackgroundSnapshotWriter>.Instance,
            onSaved: _ => Interlocked.Increment(ref savedCallbacks));

        var stale = MakeRichSnapshot(channel: 84) with
        {
            LastAppliedSeq = 10,
        };
        writer.Submit(stale);
        await blocking.FirstSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        blocking.DeleteAll(84);
        blocking.ReleaseFirstSave();
        await blocking.FirstSaveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(Directory.GetFiles(dir.Path, "channel-84.snapshot*"));
        Assert.Null(inner.TryLoad(84));
        Assert.Equal(0, Volatile.Read(ref savedCallbacks));

        var fresh = stale with
        {
            SequenceNumber = 0,
            SequenceVersion = 1,
            Engine = stale.Engine with
            {
                Books =
                [
                    new EngineStateSnapshot.BookSnapshot(
                        stale.Engine.Books[0].SecurityId,
                        []),
                ],
                Stops = [],
            },
            Owners = [],
            LastAppliedSeq = 1,
        };
        writer.Submit(fresh);
        await writer.StopAsync();

        var loaded = inner.TryLoad(84);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.LastAppliedSeq);
        Assert.Empty(loaded.Engine.Books.Single().Orders);
        Assert.Empty(loaded.Owners);
        Assert.Equal(1, Volatile.Read(ref savedCallbacks));
    }

    [Fact]
    public async Task Submit_DoesNotWaitForInProgressSave_AndDeleteFencesQueuedGeneration()
    {
        using var dir = new TempDir();
        var inner = new FileChannelStatePersister(
            dir.Path, NullLogger<FileChannelStatePersister>.Instance);
        using var releaseSynchronousSave = new ManualResetEventSlim(false);
        var synchronousSaveEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int saveHookCalls = 0;
        inner.BeforeSaveForTesting = () =>
        {
            if (Interlocked.Increment(ref saveHookCalls) != 1)
                return;
            synchronousSaveEntered.TrySetResult(true);
            releaseSynchronousSave.Wait();
        };

        var synchronous = Task.Run(() => inner.Save(
            MakeRichSnapshot(channel: 84) with { LastAppliedSeq = 1 }));
        await synchronousSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var blocking = new BlockingGenerationPersister(inner);
        int savedCallbacks = 0;
        await using var writer = new BackgroundSnapshotWriter(
            channelNumber: 84,
            persister: blocking,
            logger: NullLogger<BackgroundSnapshotWriter>.Instance,
            onSaved: _ => Interlocked.Increment(ref savedCallbacks));
        var queued = MakeRichSnapshot(channel: 84) with
        {
            SequenceNumber = 2,
            LastAppliedSeq = 2,
        };

        var submit = Task.Run(() => writer.Submit(queued));
        await submit.WaitAsync(TimeSpan.FromSeconds(1));
        await blocking.FirstSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var delete = Task.Run(() => blocking.DeleteAll(84));
        releaseSynchronousSave.Set();
        await synchronous.WaitAsync(TimeSpan.FromSeconds(5));
        await delete.WaitAsync(TimeSpan.FromSeconds(5));

        blocking.ReleaseFirstSave();
        await blocking.FirstSaveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await writer.StopAsync();

        Assert.Empty(Directory.GetFiles(dir.Path, "channel-84.snapshot*"));
        Assert.Null(inner.TryLoad(84));
        Assert.Equal(0, Volatile.Read(ref savedCallbacks));
    }

    private sealed class BlockingGenerationPersister(
        FileChannelStatePersister inner) : IChannelStatePersister
    {
        private readonly ManualResetEventSlim _releaseFirst = new(false);
        private int _trySaveCalls;

        public TaskCompletionSource<bool> FirstSaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstSaveCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChannelStateSnapshot? TryLoad(byte channelNumber)
            => inner.TryLoad(channelNumber);

        public long Save(ChannelStateSnapshot snapshot)
            => inner.Save(snapshot);

        public long CaptureSaveGeneration(byte channelNumber)
            => inner.CaptureSaveGeneration(channelNumber);

        public bool TrySave(
            ChannelStateSnapshot snapshot,
            long saveGeneration,
            out long bytesWritten)
        {
            bool first = Interlocked.Increment(ref _trySaveCalls) == 1;
            if (first)
            {
                FirstSaveEntered.TrySetResult(true);
                _releaseFirst.Wait();
            }
            try
            {
                return inner.TrySave(
                    snapshot,
                    saveGeneration,
                    out bytesWritten);
            }
            finally
            {
                if (first)
                    FirstSaveCompleted.TrySetResult(true);
            }
        }

        public int DeleteAll(byte channelNumber)
            => inner.DeleteAll(channelNumber);

        public void ReleaseFirstSave() => _releaseFirst.Set();
    }

    private static ChannelStateSnapshot MakeRichSnapshot(byte channel)
    {
        var orders = new[]
        {
            new RestingOrderRecord(
                OrderId: 1, ClOrdId: "C-1", Side: Side.Buy,
                PriceMantissa: 100_000, RemainingQuantity: 200,
                EnteringFirm: 100, InsertTimestampNanos: 1_000UL,
                Tif: TimeInForce.Day, MaxFloor: 0, HiddenQuantity: 0),
            new RestingOrderRecord(
                OrderId: 2, ClOrdId: "C-2", Side: Side.Sell,
                PriceMantissa: 105_000, RemainingQuantity: 50,
                EnteringFirm: 200, InsertTimestampNanos: 2_000UL,
                Tif: TimeInForce.Gtc, MaxFloor: 100, HiddenQuantity: 400),
        };
        var phases = new[]
        {
            new EngineStateSnapshot.PhaseEntry(900_000_000_001L, TradingPhase.Open),
        };
        var books = new[]
        {
            new EngineStateSnapshot.BookSnapshot(900_000_000_001L, orders),
        };
        var engine = new EngineStateSnapshot(
            NextOrderId: 99,
            NextTradeId: 11,
            RptSeqBySecurity: [new EngineStateSnapshot.RptSeqEntry(900_000_000_001L, 17)],
            Phases: phases,
            Books: books);
        var owners = new[]
        {
            new OrderOwnerSnapshot(1, "10101", 100, 0xDEADBEEF, Side.Buy, 900_000_000_001L),
            new OrderOwnerSnapshot(2, "20201", 200, 0xCAFEBABE, Side.Sell, 900_000_000_001L),
        };
        return new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: channel,
            SequenceNumber: 1234,
            SequenceVersion: 5,
            Engine: engine,
            Owners: owners);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "b3xpersist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
