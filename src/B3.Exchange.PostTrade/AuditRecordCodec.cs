using System.Buffers.Binary;
using System.IO.Hashing;
using B3.Exchange.Matching;

namespace B3.Exchange.PostTrade;

/// <summary>
/// Byte-level encoder/decoder for the on-disk post-trade audit log
/// (issue #329 PR-2). Wire layout is fixed-width little-endian and
/// schema-versioned via the file header — the record body is identical
/// across <see cref="SchemaVersion"/>=1 deployments.
///
/// File header (<see cref="FileHeaderSize"/> = 24 bytes):
/// <code>
///   magic "B3PT" (4)
///   schemaVersion (uint16)
///   reserved      (uint16)
///   channelNumber (uint8)
///   pad           (3 bytes)
///   tradeDate ASCII YYYY-MM-DD (10 bytes)
/// </code>
///
/// Record (<see cref="RecordSize"/> = 85 bytes total, including the
/// length+crc header so the on-disk byte count matches the value
/// returned by <see cref="Encode"/>):
/// <code>
///   recordLen          (uint32) bytes following this field (always 77)
///   crc32             (uint32) over the body that follows
///   tradeId            (uint32)
///   transactTimeNanos  (uint64)
///   securityId         (int64)
///   aggressorSide      (uint8)  0=Buy, 1=Sell
///   quantity           (int64)
///   priceMantissa      (int64)
///   buyClOrdId         (uint64)
///   sellClOrdId        (uint64)
///   buyFirm            (uint32)
///   sellFirm           (uint32)
///   buyOrderId         (int64)
///   sellOrderId        (int64)
/// </code>
///
/// CRC32 (IEEE 802.3) covers every byte after the crc field (i.e. the record body
/// from tradeId through sellOrderId). recordLen and crc32 are NOT
/// covered — recordLen is needed before reading the body, and crc32
/// is obviously what's being verified.
/// </summary>
public static class AuditRecordCodec
{
    public const ushort SchemaVersion = 1;
    public const int FileHeaderSize = 24;
    public const int RecordBodySize = 77;
    public const int RecordSize = RecordBodySize + 8; // +4 recordLen +4 crc32

    private const uint MagicB3PT = 0x5450_3342u; // "B3PT" little-endian

    public static void WriteFileHeader(Span<byte> dst, byte channelNumber, DateOnly tradeDate)
    {
        if (dst.Length < FileHeaderSize)
            throw new ArgumentException($"buffer too small ({dst.Length}<{FileHeaderSize})", nameof(dst));
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(0, 4), MagicB3PT);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(4, 2), SchemaVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(6, 2), 0);
        dst[8] = channelNumber;
        dst[9] = 0; dst[10] = 0; dst[11] = 0;
        // tradeDate ASCII YYYY-MM-DD; ASCII is fixed-width and locale-independent.
        var iso = tradeDate.ToString("yyyy-MM-dd");
        System.Text.Encoding.ASCII.GetBytes(iso, dst.Slice(12, 10));
    }

    /// <summary>
    /// Decodes a file header. Throws <see cref="InvalidDataException"/> if
    /// the magic, schema version, or trade-date encoding is corrupt.
    /// </summary>
    public static (byte ChannelNumber, DateOnly TradeDate) ReadFileHeader(ReadOnlySpan<byte> src)
    {
        if (src.Length < FileHeaderSize)
            throw new InvalidDataException("audit file truncated in header");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        if (magic != MagicB3PT)
            throw new InvalidDataException($"audit file magic mismatch: 0x{magic:X8}");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(4, 2));
        if (version != SchemaVersion)
            throw new InvalidDataException($"audit file schema version {version} unsupported (this build expects {SchemaVersion})");
        byte channel = src[8];
        var iso = System.Text.Encoding.ASCII.GetString(src.Slice(12, 10));
        if (!DateOnly.TryParseExact(iso, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date))
            throw new InvalidDataException($"audit file tradeDate field invalid: '{iso}'");
        return (channel, date);
    }

    /// <summary>Encodes <paramref name="record"/> into <paramref name="dst"/>.
    /// Returns the number of bytes written (always <see cref="RecordSize"/>).</summary>
    public static int Encode(Span<byte> dst, in PostTradeRecord record)
    {
        if (dst.Length < RecordSize)
            throw new ArgumentException($"buffer too small ({dst.Length}<{RecordSize})", nameof(dst));

        var body = dst.Slice(8, RecordBodySize);
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(p, 4), record.TradeId); p += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(p, 8), record.TransactTimeNanos); p += 8;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(p, 8), record.SecurityId); p += 8;
        body[p] = record.AggressorSide == Side.Buy ? (byte)0 : (byte)1; p += 1;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(p, 8), record.Quantity); p += 8;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(p, 8), record.PriceMantissa); p += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(p, 8), record.BuyClOrdId); p += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(p, 8), record.SellClOrdId); p += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(p, 4), record.BuyFirm); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(p, 4), record.SellFirm); p += 4;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(p, 8), record.BuyOrderId); p += 8;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(p, 8), record.SellOrderId); p += 8;
        if (p != RecordBodySize)
            throw new InvalidOperationException($"BUG: encoded {p} body bytes, expected {RecordBodySize}");

        // recordLen counts everything after itself: crc(4) + body(77) = 81.
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(0, 4), (uint)(RecordSize - 4));
        var crc = ComputeCrc(body);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4, 4), crc);
        return RecordSize;
    }

    /// <summary>Decodes one record. Returns false (and leaves
    /// <paramref name="record"/> at default) when:
    /// (a) the buffer is shorter than a full record, or
    /// (b) the recordLen field declares a length other than the v1 layout, or
    /// (c) the stored CRC32 (IEEE 802.3) does not match the body.
    /// The (b) and (c) failures are the recovery signal a torn write on
    /// the writer-side leaves behind. Callers reading a log to its end
    /// MUST treat a false return as "log ends here" rather than as
    /// corruption further down — this matches the standard append-only
    /// log read pattern.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> src, out PostTradeRecord record)
    {
        record = default;
        if (src.Length < RecordSize) return false;
        uint declaredLen = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        if (declaredLen != RecordSize - 4) return false;
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4, 4));
        var body = src.Slice(8, RecordBodySize);
        if (ComputeCrc(body) != storedCrc) return false;

        int p = 0;
        uint tradeId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(p, 4)); p += 4;
        ulong ts = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p, 8)); p += 8;
        long secId = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(p, 8)); p += 8;
        Side side = body[p] == 0 ? Side.Buy : Side.Sell; p += 1;
        long qty = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(p, 8)); p += 8;
        long px = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(p, 8)); p += 8;
        ulong buyClOrd = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p, 8)); p += 8;
        ulong sellClOrd = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p, 8)); p += 8;
        uint buyFirm = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(p, 4)); p += 4;
        uint sellFirm = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(p, 4)); p += 4;
        long buyOrderId = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(p, 8)); p += 8;
        long sellOrderId = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(p, 8)); p += 8;
        _ = p;

        record = new PostTradeRecord(tradeId, ts, secId, side, qty, px,
            buyClOrd, sellClOrd, buyFirm, sellFirm, buyOrderId, sellOrderId);
        return true;
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[4];
        Crc32.Hash(data, hash);
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);
    }
}
