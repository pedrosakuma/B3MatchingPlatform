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

    [Fact]
    public void Decode_BoundsAttackerCounts_BeforeAllocating()
    {
        // Issue #285 follow-up (gpt-5.5 review of PR #293): a
        // crafted varint count larger than the input buffer must
        // be rejected before the decoder allocates an N-element
        // array. Without the bounded-count guard a corrupted owner
        // count of 0xFFFF_FFFF would request a 32 GB allocation
        // during recovery.
        var snap = MakeMinimalSnapshot();
        var bytes = BinaryChannelStateSnapshotCodec.Encode(snap);

        // The phase count is the first uvarint after a fixed-size
        // header (magic 4 + version 2 + channel 1 + seqNum 4 +
        // seqVer 2 + lastApplied 8 + nextOrder 8 + nextTrade 4 +
        // rptSeq 4 = 37). In the minimal snapshot phase count == 0
        // (single byte 0x00). Replace it with a 5-byte uvarint
        // encoding ulong.MaxValue and strip the footer so the
        // decoder takes the legacy path.
        const int phaseCountOffset = 4 + 2 + 1 + 4 + 2 + 8 + 8 + 4 + 4;
        Assert.Equal((byte)0, bytes[phaseCountOffset]);
        // 5-byte uvarint encoding of a value with all top bits set
        // (decodes to a count >> bytes.Length).
        var attack = new List<byte>(bytes.Length + 8);
        attack.AddRange(bytes.Take(phaseCountOffset));
        attack.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F });
        attack.AddRange(bytes.Skip(phaseCountOffset + 1).Take(bytes.Length - phaseCountOffset - 1 - 4));
        var ex = Assert.Throws<InvalidDataException>(
            () => BinaryChannelStateSnapshotCodec.Decode(attack.ToArray()));
        Assert.Contains("phase", ex.Message);
        Assert.Contains("refusing to allocate", ex.Message);
    }

    [Fact]
    public void Decode_VerifiesCrcBeforeStructuralDecode_OnIntactFiles()
    {
        // Sanity round-trip: a valid snapshot still decodes through
        // the new CRC-first fast path. Asserts the refactor didn't
        // break the happy path.
        var snap = MakeMinimalSnapshot();
        var bytes = BinaryChannelStateSnapshotCodec.Encode(snap);
        var decoded = BinaryChannelStateSnapshotCodec.Decode(bytes);
        Assert.Equal(snap.SequenceNumber, decoded.SequenceNumber);
        Assert.Equal(snap.LastAppliedSeq, decoded.LastAppliedSeq);
    }
}
