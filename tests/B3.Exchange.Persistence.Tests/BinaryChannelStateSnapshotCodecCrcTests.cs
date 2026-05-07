using B3.Exchange.Core;
using B3.Exchange.Matching;
using B3.Exchange.Persistence;

namespace B3.Exchange.Persistence.Tests;

/// <summary>
/// Issue #285: <see cref="BinaryChannelStateSnapshotCodec"/> appends
/// a 4-byte little-endian Crc32C footer covering every preceding
/// byte. <see cref="BinaryChannelStateSnapshotCodec.Decode"/> must
/// validate the footer when present and reject mismatches; files
/// written by pre-#285 hosts (no footer) must still load (legacy
/// path).
/// </summary>
public class BinaryChannelStateSnapshotCodecCrcTests
{
    private static ChannelStateSnapshot MakeMinimalSnapshot()
    {
        var engine = new EngineStateSnapshot(
            NextOrderId: 100,
            NextTradeId: 7,
            RptSeq: 3,
            Phases: Array.Empty<EngineStateSnapshot.PhaseEntry>(),
            Books: Array.Empty<EngineStateSnapshot.BookSnapshot>(),
            Stops: null);
        return new ChannelStateSnapshot(
            Version: ChannelStateSnapshot.CurrentVersion,
            ChannelNumber: 84,
            SequenceNumber: 42,
            SequenceVersion: 1,
            Engine: engine,
            Owners: Array.Empty<OrderOwnerSnapshot>())
        {
            LastAppliedSeq = 99,
        };
    }

    [Fact]
    public void Encode_AppendsFourByteFooter()
    {
        var snap = MakeMinimalSnapshot();
        var bytes = BinaryChannelStateSnapshotCodec.Encode(snap);
        // Decode parses everything up to the last 4 bytes; the
        // footer is exactly 4 bytes long.
        var roundTripped = BinaryChannelStateSnapshotCodec.Decode(bytes);
        Assert.Equal(snap.SequenceNumber, roundTripped.SequenceNumber);
        Assert.Equal(snap.LastAppliedSeq, roundTripped.LastAppliedSeq);
    }

    [Fact]
    public void Decode_DetectsFlippedByte_InPayload()
    {
        var snap = MakeMinimalSnapshot();
        var bytes = BinaryChannelStateSnapshotCodec.Encode(snap);
        // Flip a byte well inside the structural payload (skip the
        // magic and version header so the decoder still reaches the
        // CRC validation step rather than failing earlier).
        bytes[20] ^= 0xFF;
        var ex = Assert.Throws<InvalidDataException>(
            () => BinaryChannelStateSnapshotCodec.Decode(bytes));
        Assert.Contains("Crc32C mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_DetectsFlippedByte_InFooter()
    {
        var snap = MakeMinimalSnapshot();
        var bytes = BinaryChannelStateSnapshotCodec.Encode(snap);
        bytes[^1] ^= 0xFF;
        Assert.Throws<InvalidDataException>(
            () => BinaryChannelStateSnapshotCodec.Decode(bytes));
    }

    [Fact]
    public void Decode_AcceptsLegacyFile_WithoutFooter()
    {
        // Simulate a pre-#285 binary file: encode normally then
        // strip the trailing 4 bytes. The decoder must accept this
        // (the migration window for pre-#285 binary slots).
        var snap = MakeMinimalSnapshot();
        var bytes = BinaryChannelStateSnapshotCodec.Encode(snap);
        var legacy = bytes.AsSpan(0, bytes.Length - 4).ToArray();
        var decoded = BinaryChannelStateSnapshotCodec.Decode(legacy);
        Assert.Equal(snap.SequenceNumber, decoded.SequenceNumber);
        Assert.Equal(snap.LastAppliedSeq, decoded.LastAppliedSeq);
    }

    [Fact]
    public void Decode_RejectsTrailingGarbage_NotFourBytes()
    {
        var snap = MakeMinimalSnapshot();
        var bytes = BinaryChannelStateSnapshotCodec.Encode(snap);
        // Append 3 bogus bytes after the legitimate footer — total
        // trailing is 7 bytes which matches neither legacy (0) nor
        // modern (4). The decoder must reject.
        var bad = new byte[bytes.Length + 3];
        Buffer.BlockCopy(bytes, 0, bad, 0, bytes.Length);
        bad[^1] = 0xAA;
        Assert.Throws<InvalidDataException>(
            () => BinaryChannelStateSnapshotCodec.Decode(bad));
    }
}
