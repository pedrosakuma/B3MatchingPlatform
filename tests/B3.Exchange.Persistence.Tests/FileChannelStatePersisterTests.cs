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
        Assert.Equal(snapshot.Engine.RptSeq, loaded.Engine.RptSeq);
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
            NextOrderId: 99, NextTradeId: 11, RptSeq: 17, Phases: phases, Books: books);
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
