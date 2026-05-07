using B3.Exchange.Core;
using B3.Exchange.Matching;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Issue #266: integration tests for <see cref="FileChannelStatePersister"/>
/// when configured with <see cref="SnapshotFileFormat.Binary"/>. Covers
/// the on-disk format selection, the auto-sniff load path (binary +
/// JSON files coexist), and the rolling-generation interaction.
/// </summary>
public class FileChannelStatePersisterBinaryTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "b3-binary-snap-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ChannelStateSnapshot Sample(byte channel = 1, uint seq = 100, ushort seqVer = 2)
    {
        var orders = new[]
        {
            new RestingOrderRecord(1, "CLI-A", Side.Buy, 250000, 100, 7, 1UL,
                TimeInForce.Day, 0, 0),
        };
        var engine = new EngineStateSnapshot(
            NextOrderId: 2, NextTradeId: 1, RptSeq: 1,
            Phases: new[] { new EngineStateSnapshot.PhaseEntry(99, TradingPhase.Open) },
            Books: new[] { new EngineStateSnapshot.BookSnapshot(99, orders) },
            Stops: null);
        return new ChannelStateSnapshot(
            Version: 1, ChannelNumber: channel, SequenceNumber: seq, SequenceVersion: seqVer,
            Engine: engine, Owners: Array.Empty<OrderOwnerSnapshot>());
    }

    [Fact]
    public void Save_WithBinaryFormat_WritesMagicBytes()
    {
        var dir = NewTempDir();
        try
        {
            var p = new FileChannelStatePersister(dir, NullLogger<FileChannelStatePersister>.Instance,
                writeFormat: SnapshotFileFormat.Binary);
            var bytes = p.Save(Sample());
            Assert.True(bytes > 0);
            var file = Directory.GetFiles(dir, "channel-1.snapshot.*")[0];
            var leading = File.ReadAllBytes(file).AsSpan(0, 4);
            Assert.True(BinaryChannelStateSnapshotCodec.LooksLikeBinarySnapshot(leading));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TryLoad_AutoDetectsBinary()
    {
        var dir = NewTempDir();
        try
        {
            var writer = new FileChannelStatePersister(dir, NullLogger<FileChannelStatePersister>.Instance,
                writeFormat: SnapshotFileFormat.Binary);
            writer.Save(Sample(channel: 5, seq: 555, seqVer: 7));

            // A reader configured for JSON-write must still successfully
            // read a binary-formatted file thanks to the magic sniff.
            var reader = new FileChannelStatePersister(dir, NullLogger<FileChannelStatePersister>.Instance,
                writeFormat: SnapshotFileFormat.Json);
            var loaded = reader.TryLoad(5);
            Assert.NotNull(loaded);
            Assert.Equal((uint)555, loaded!.SequenceNumber);
            Assert.Equal((ushort)7, loaded.SequenceVersion);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TryLoad_MixedJsonAndBinaryGenerations_BothLoadable()
    {
        // Simulate a deployment that flipped from JSON to binary mid-life:
        // older slots still hold JSON, newer slot holds binary. Both must
        // be readable by the same persister.
        var dir = NewTempDir();
        try
        {
            var jsonWriter = new FileChannelStatePersister(dir, NullLogger<FileChannelStatePersister>.Instance,
                generations: 3, writeFormat: SnapshotFileFormat.Json);
            jsonWriter.Save(Sample(channel: 3, seq: 1));
            jsonWriter.Save(Sample(channel: 3, seq: 2));

            var binaryWriter = new FileChannelStatePersister(dir, NullLogger<FileChannelStatePersister>.Instance,
                generations: 3, writeFormat: SnapshotFileFormat.Binary);
            binaryWriter.Save(Sample(channel: 3, seq: 3));

            var loaded = binaryWriter.TryLoad(3);
            Assert.NotNull(loaded);
            // newest mtime wins, so the binary one should come back.
            Assert.Equal((uint)3, loaded!.SequenceNumber);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TryLoad_FallsBackToOlderGenerationOnCorruptBinary()
    {
        var dir = NewTempDir();
        try
        {
            var p = new FileChannelStatePersister(dir, NullLogger<FileChannelStatePersister>.Instance,
                generations: 3, writeFormat: SnapshotFileFormat.Binary);
            p.Save(Sample(channel: 4, seq: 10));
            p.Save(Sample(channel: 4, seq: 20)); // newest, will be corrupted

            // Corrupt the newest slot by truncating the last 5 bytes.
            var files = Directory.GetFiles(dir, "channel-4.snapshot.*")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
            var newest = files[0];
            var bytes = File.ReadAllBytes(newest);
            File.WriteAllBytes(newest, bytes.AsSpan(0, bytes.Length - 5).ToArray());

            var loaded = p.TryLoad(4);
            Assert.NotNull(loaded);
            Assert.Equal((uint)10, loaded!.SequenceNumber);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
