using System.Buffers.Binary;

namespace B3.Exchange.PostTrade;

/// <summary>
/// Byte-level encoder/decoder for the sparse per-firm audit index
/// (<c>.idx</c> sidecar) introduced in issue #329 PR-3.
///
/// The index does not encode individual records — instead, it carves the
/// log into fixed-size record blocks (<see cref="DefaultBlockRecords"/>
/// records per block) and stores, per block, the distinct set of firm IDs
/// that participated in any record inside it. A firm-filtered reader can
/// then skip whole blocks that have no entry for the queried firm and
/// linearly scan only the candidate blocks.
///
/// File header (<see cref="FileHeaderSize"/> = 24 bytes):
/// <code>
///   magic              "B3PI" (4)
///   schemaVersion      uint16(=1)
///   reserved           uint16(=0)
///   channelNumber      uint8
///   pad                uint8[3](=0,0,0)
///   tradeDate          ASCII "YYYY-MM-DD" (10)
///   reserved2          uint16(=0)
/// </code>
///
/// Block entry (variable):
/// <code>
///   entryLen           uint16  // bytes following this field (total - 2)
///   blockLogOffset     uint64  // byte offset of first record inside the .log file
///   recordCount        uint16  // records in this block (1..DefaultBlockRecords)
///   firmCount          uint16  // distinct firm IDs in this block
///   firmIds            uint32[firmCount] (sorted ascending, deduplicated)
/// </code>
/// Reader-side recovery uses <c>entryLen</c> to skip a torn entry at
/// end-of-file, mirroring the log's append-only contract.
/// </summary>
public static class AuditIndexCodec
{
    public const ushort SchemaVersion = 1;
    public const int FileHeaderSize = 24;
    public const int DefaultBlockRecords = 64;
    public const int BlockEntryFixedPrefix = 2 + 8 + 2 + 2; // entryLen + blockLogOffset + recordCount + firmCount

    private const uint MagicB3PI = 0x4950_3342u; // "B3PI" little-endian

    public static void WriteFileHeader(Span<byte> dst, byte channelNumber, DateOnly tradeDate)
    {
        if (dst.Length < FileHeaderSize)
            throw new ArgumentException($"buffer too small ({dst.Length}<{FileHeaderSize})", nameof(dst));
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(0, 4), MagicB3PI);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(4, 2), SchemaVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(6, 2), 0);
        dst[8] = channelNumber;
        dst[9] = 0; dst[10] = 0; dst[11] = 0;
        var iso = tradeDate.ToString("yyyy-MM-dd");
        System.Text.Encoding.ASCII.GetBytes(iso, dst.Slice(12, 10));
        dst[22] = 0; dst[23] = 0;
    }

    public static (byte ChannelNumber, DateOnly TradeDate) ReadFileHeader(ReadOnlySpan<byte> src)
    {
        if (src.Length < FileHeaderSize)
            throw new InvalidDataException("audit index truncated in header");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        if (magic != MagicB3PI)
            throw new InvalidDataException($"audit index magic mismatch: 0x{magic:X8}");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(4, 2));
        if (version != SchemaVersion)
            throw new InvalidDataException($"audit index schema version {version} unsupported (this build expects {SchemaVersion})");
        byte channel = src[8];
        if (src[9] != 0 || src[10] != 0 || src[11] != 0 || src[22] != 0 || src[23] != 0)
            throw new InvalidDataException("audit index reserved bytes non-zero");
        var iso = System.Text.Encoding.ASCII.GetString(src.Slice(12, 10));
        if (!DateOnly.TryParseExact(iso, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date))
            throw new InvalidDataException($"audit index tradeDate field invalid: '{iso}'");
        return (channel, date);
    }

    /// <summary>Encodes one block entry into <paramref name="dst"/>. The
    /// firm-ID list must be sorted ascending and deduplicated.</summary>
    public static int EncodeBlockEntry(Span<byte> dst, ulong blockLogOffset, ushort recordCount, ReadOnlySpan<uint> firmIds)
    {
        int total = BlockEntryFixedPrefix + (firmIds.Length * 4);
        if (dst.Length < total)
            throw new ArgumentException($"buffer too small ({dst.Length}<{total})", nameof(dst));
        ushort entryLen = checked((ushort)(total - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(0, 2), entryLen);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(2, 8), blockLogOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(10, 2), recordCount);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(12, 2), checked((ushort)firmIds.Length));
        for (int i = 0; i < firmIds.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(14 + (i * 4), 4), firmIds[i]);
        return total;
    }

    /// <summary>Reads a single block entry header (without the firm IDs).
    /// Returns false (and leaves outputs at default) when the source span
    /// is too short or declares an entry longer than the available bytes —
    /// matching the torn-tail-tolerant log read contract.</summary>
    public static bool TryReadBlockEntry(
        ReadOnlySpan<byte> src,
        out ulong blockLogOffset,
        out ushort recordCount,
        out ushort firmCount,
        out int totalBytes)
    {
        blockLogOffset = 0; recordCount = 0; firmCount = 0; totalBytes = 0;
        if (src.Length < BlockEntryFixedPrefix) return false;
        ushort entryLen = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(0, 2));
        int total = entryLen + 2;
        if (src.Length < total) return false;
        ulong off = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(2, 8));
        ushort rc = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(10, 2));
        ushort fc = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(12, 2));
        if (BlockEntryFixedPrefix + (fc * 4) != total) return false;
        blockLogOffset = off;
        recordCount = rc;
        firmCount = fc;
        totalBytes = total;
        return true;
    }

    public static uint ReadFirmId(ReadOnlySpan<byte> blockEntry, int firmIndex)
        => BinaryPrimitives.ReadUInt32LittleEndian(blockEntry.Slice(14 + (firmIndex * 4), 4));
}
