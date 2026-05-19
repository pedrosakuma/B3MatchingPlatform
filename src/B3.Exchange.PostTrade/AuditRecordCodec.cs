using System.Buffers.Binary;
using System.IO.Hashing;
using B3.Exchange.Matching;

namespace B3.Exchange.PostTrade;

/// <summary>
/// Byte-level encoder/decoder for the on-disk post-trade audit log
/// (issue #329 PR-2; extended for ADR 0008 / issue #369 PR-1 with
/// schema-v2 bust + reject-attempt records).
///
/// File header (<see cref="FileHeaderSize"/> = 24 bytes):
/// <code>
///   magic "B3PT" (4)
///   schemaVersion (uint16)   1 = fills only (legacy)
///                            2 = fills + busts + reject-attempts (ADR 0008)
///   reserved      (uint16)
///   channelNumber (uint8)
///   pad           (3 bytes)
///   tradeDate ASCII YYYY-MM-DD (10 bytes)
/// </code>
///
/// Fill record body (recordLen = 81, total on-disk = 85 bytes):
/// <code>
///   recordLen          (uint32) bytes following this field (always 81)
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
/// The fill body is bit-identical between v1 and v2 — there is NO
/// discriminator byte at the head of a fill body. v1 files contain
/// only fill records (any other recordLen is corruption); v2 files
/// dispatch by recordLen (see <see cref="PeekRecordLen"/>).
///
/// Bust record body (schema v2, recordLen = 40, total = 44 bytes,
/// ADR 0008 §1.1):
/// <code>
///   recordLen                 (uint32) always 40
///   crc32                    (uint32) over the body that follows
///   recordType                (uint8)  always 0x02
///   reserved                  (uint8)  always 0
///   cancelledTradeId          (uint32)
///   bustTransactTimeNanos     (uint64)
///   securityId                (int64)  echo from original fill
///   reasonCode                (uint16) see ADR 0008 §2.2
///   busterFirm                (uint32)
///   correlationId             (uint64)
/// </code>
///
/// Reject-attempt record body (schema v2, recordLen = 36, total = 40
/// bytes, ADR 0008 §2.5):
/// <code>
///   recordLen                 (uint32) always 36
///   crc32                    (uint32) over the body that follows
///   recordType                (uint8)  always 0x03
///   reserved                  (uint8)  always 0
///   attemptedTradeId          (uint32)
///   attemptTransactTimeNanos  (uint64)
///   declaredTradeDate         (int32)  LocalMktDate (days since 1970-01-01); 0 if absent
///   rejectCode                (uint16) see ADR 0008 §2.5
///   busterFirm                (uint32)
///   correlationId             (uint64)
/// </code>
///
/// CRC32 (IEEE 802.3) covers every byte after the crc field. recordLen
/// and crc32 are NOT covered.
///
/// **Dispatch by recordLen** (ADR 0008 §1): a v2 reader peeks the
/// recordLen, looks up the type, and cross-checks the leading
/// recordType byte against the dispatched type. A mismatch is treated
/// as corruption, same policy as a CRC failure. Unknown recordLen on
/// a v1 file is corruption; unknown recordLen on a v2 file is also
/// corruption (no forward-compat record types in this build).
/// </summary>
public static class AuditRecordCodec
{
    /// <summary>The schema version this build writes for newly-created
    /// audit files. ADR 0008 / issue #369 PR-2 bumps the default to V2;
    /// existing V1 files remain readable on resume (per-day schema view
    /// per ADR 0008 §1) and the writer's recovery path dispatches on the
    /// per-file header version.</summary>
    public const ushort SchemaVersion = SchemaVersionV2;

    /// <summary>Legacy fills-only schema (issue #329).</summary>
    public const ushort SchemaVersionV1 = 1;

    /// <summary>ADR 0008 / issue #369 schema. Adds bust + reject-attempt
    /// records on the same stream; fill body is byte-for-byte identical
    /// to v1.</summary>
    public const ushort SchemaVersionV2 = 2;

    public const int FileHeaderSize = 24;

    // Fill (unchanged from v1).
    public const int RecordBodySize = 77;
    public const int RecordSize = RecordBodySize + 8; // +4 recordLen +4 crc32
    public const uint FillRecordLen = (uint)(RecordSize - 4); // 81

    // Bust (ADR 0008 §1.1).
    public const int BustRecordBodySize = 36;
    public const int BustRecordSize = BustRecordBodySize + 8; // 44
    public const uint BustRecordLen = (uint)(BustRecordSize - 4); // 40
    public const byte RecordTypeBust = 0x02;

    // Reject-attempt (ADR 0008 §2.5).
    public const int RejectAttemptRecordBodySize = 32;
    public const int RejectAttemptRecordSize = RejectAttemptRecordBodySize + 8; // 40
    public const uint RejectAttemptRecordLen = (uint)(RejectAttemptRecordSize - 4); // 36
    public const byte RecordTypeRejectAttempt = 0x03;

    /// <summary>Largest on-disk record across all supported schemas.
    /// Callers sizing scratch buffers must use this constant.</summary>
    public const int MaxRecordSize = RecordSize; // 85 (fills are largest)

    private const uint MagicB3PT = 0x5450_3342u; // "B3PT" little-endian

    public static void WriteFileHeader(Span<byte> dst, byte channelNumber, DateOnly tradeDate)
        => WriteFileHeader(dst, channelNumber, tradeDate, SchemaVersion);

    /// <summary>Writes a file header with an explicit schema version.
    /// Production callers use the parameterless overload (current
    /// build's <see cref="SchemaVersion"/>); the explicit overload
    /// exists so tests can stage fixture files at any supported
    /// version without monkey-patching the constant.</summary>
    public static void WriteFileHeader(Span<byte> dst, byte channelNumber, DateOnly tradeDate, ushort schemaVersion)
    {
        if (dst.Length < FileHeaderSize)
            throw new ArgumentException($"buffer too small ({dst.Length}<{FileHeaderSize})", nameof(dst));
        if (schemaVersion is not (SchemaVersionV1 or SchemaVersionV2))
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "supported versions: 1, 2");
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(0, 4), MagicB3PT);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(4, 2), schemaVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(6, 2), 0);
        dst[8] = channelNumber;
        dst[9] = 0; dst[10] = 0; dst[11] = 0;
        // tradeDate ASCII YYYY-MM-DD; ASCII is fixed-width and locale-independent.
        var iso = tradeDate.ToString("yyyy-MM-dd");
        System.Text.Encoding.ASCII.GetBytes(iso, dst.Slice(12, 10));
        // Reserved trailer bytes — kept zero so future readers can validate
        // them strictly. Required because the writer reuses a single scratch
        // buffer for headers and records, so leaving these unwritten would
        // leak bytes from the previously encoded record after a rollover.
        dst[22] = 0; dst[23] = 0;
    }

    /// <summary>
    /// Decodes a file header. Throws <see cref="InvalidDataException"/> if
    /// the magic, schema version, or trade-date encoding is corrupt.
    /// The returned <c>SchemaVersion</c> is the per-file version the
    /// reader must dispatch on (ADR 0008 §1 per-day schema view).
    /// </summary>
    public static (byte ChannelNumber, DateOnly TradeDate, ushort SchemaVersion) ReadFileHeader(ReadOnlySpan<byte> src)
    {
        if (src.Length < FileHeaderSize)
            throw new InvalidDataException("audit file truncated in header");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        if (magic != MagicB3PT)
            throw new InvalidDataException($"audit file magic mismatch: 0x{magic:X8}");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(4, 2));
        if (version is not (SchemaVersionV1 or SchemaVersionV2))
            throw new InvalidDataException($"audit file schema version {version} unsupported (this build accepts {SchemaVersionV1} or {SchemaVersionV2})");
        byte channel = src[8];
        if (src[9] != 0 || src[10] != 0 || src[11] != 0 || src[22] != 0 || src[23] != 0)
            throw new InvalidDataException("audit file reserved bytes non-zero");
        var iso = System.Text.Encoding.ASCII.GetString(src.Slice(12, 10));
        if (!DateOnly.TryParseExact(iso, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date))
            throw new InvalidDataException($"audit file tradeDate field invalid: '{iso}'");
        return (channel, date, version);
    }

    /// <summary>Peeks the <c>recordLen</c> field of a framed record so
    /// the reader can dispatch by record type before allocating a
    /// type-specific scratch buffer. Returns false when the buffer is
    /// too short to even hold the length prefix.</summary>
    public static bool TryPeekRecordLen(ReadOnlySpan<byte> src, out uint recordLen)
    {
        if (src.Length < 4)
        {
            recordLen = 0;
            return false;
        }
        recordLen = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        return true;
    }

    /// <summary>Maps a <c>recordLen</c> to its on-disk byte size
    /// (= <c>4 + recordLen</c>). Returns false for unrecognised lengths
    /// — that is the corruption signal a v2 dispatch loop must short-
    /// circuit on (same policy as a CRC failure: "log ends here").</summary>
    public static bool TryGetRecordSize(uint recordLen, out int totalSize, out AuditRecordKind kind)
    {
        switch (recordLen)
        {
            case FillRecordLen:
                totalSize = RecordSize;
                kind = AuditRecordKind.Fill;
                return true;
            case BustRecordLen:
                totalSize = BustRecordSize;
                kind = AuditRecordKind.Bust;
                return true;
            case RejectAttemptRecordLen:
                totalSize = RejectAttemptRecordSize;
                kind = AuditRecordKind.RejectAttempt;
                return true;
            default:
                totalSize = 0;
                kind = default;
                return false;
        }
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

    // -----------------------------------------------------------------
    // ADR 0008 / issue #369 PR-1 — schema-v2 record types.
    // Fill encode/decode is unchanged above; the methods below add the
    // bust and reject-attempt shapes. No call sites in PR-1 yet — the
    // writer extension lands in PR-2.
    // -----------------------------------------------------------------

    /// <summary>Encodes a <see cref="BustRecord"/> (ADR 0008 §1.1) into
    /// <paramref name="dst"/>. Returns the number of bytes written
    /// (always <see cref="BustRecordSize"/>).</summary>
    public static int EncodeBust(Span<byte> dst, in BustRecord record)
    {
        if (dst.Length < BustRecordSize)
            throw new ArgumentException($"buffer too small ({dst.Length}<{BustRecordSize})", nameof(dst));

        var body = dst.Slice(8, BustRecordBodySize);
        int p = 0;
        body[p] = RecordTypeBust; p += 1;
        body[p] = 0; p += 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(p, 4), record.CancelledTradeId); p += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(p, 8), record.BustTransactTimeNanos); p += 8;
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(p, 8), record.SecurityId); p += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(p, 2), record.ReasonCode); p += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(p, 4), record.BusterFirm); p += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(p, 8), record.CorrelationId); p += 8;
        if (p != BustRecordBodySize)
            throw new InvalidOperationException($"BUG: encoded {p} bust body bytes, expected {BustRecordBodySize}");

        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(0, 4), BustRecordLen);
        var crc = ComputeCrc(body);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4, 4), crc);
        return BustRecordSize;
    }

    /// <summary>Decodes one bust record. Same failure semantics as
    /// <see cref="TryDecode"/>: returns false on short buffer, wrong
    /// length prefix, recordType mismatch, or CRC failure.</summary>
    public static bool TryDecodeBust(ReadOnlySpan<byte> src, out BustRecord record)
    {
        record = default;
        if (src.Length < BustRecordSize) return false;
        uint declaredLen = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        if (declaredLen != BustRecordLen) return false;
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4, 4));
        var body = src.Slice(8, BustRecordBodySize);
        if (ComputeCrc(body) != storedCrc) return false;

        // Cross-check the leading recordType byte against the dispatched
        // length (defence-in-depth per ADR 0008 §1). A mismatch is
        // treated as corruption.
        if (body[0] != RecordTypeBust) return false;
        if (body[1] != 0) return false;

        int p = 2;
        uint cancelledTradeId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(p, 4)); p += 4;
        ulong bustTs = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p, 8)); p += 8;
        long secId = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(p, 8)); p += 8;
        ushort reasonCode = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(p, 2)); p += 2;
        uint busterFirm = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(p, 4)); p += 4;
        ulong correlationId = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p, 8)); p += 8;
        _ = p;

        record = new BustRecord(cancelledTradeId, bustTs, secId, reasonCode, busterFirm, correlationId);
        return true;
    }

    /// <summary>Encodes a <see cref="RejectAttemptRecord"/> (ADR 0008
    /// §2.5) into <paramref name="dst"/>. Returns the number of bytes
    /// written (always <see cref="RejectAttemptRecordSize"/>).</summary>
    public static int EncodeRejectAttempt(Span<byte> dst, in RejectAttemptRecord record)
    {
        if (dst.Length < RejectAttemptRecordSize)
            throw new ArgumentException($"buffer too small ({dst.Length}<{RejectAttemptRecordSize})", nameof(dst));

        var body = dst.Slice(8, RejectAttemptRecordBodySize);
        int p = 0;
        body[p] = RecordTypeRejectAttempt; p += 1;
        body[p] = 0; p += 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(p, 4), record.AttemptedTradeId); p += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(p, 8), record.AttemptTransactTimeNanos); p += 8;
        BinaryPrimitives.WriteInt32LittleEndian(body.Slice(p, 4), record.DeclaredTradeDateDays); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(p, 2), record.RejectCode); p += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(p, 4), record.BusterFirm); p += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(p, 8), record.CorrelationId); p += 8;
        if (p != RejectAttemptRecordBodySize)
            throw new InvalidOperationException($"BUG: encoded {p} reject-attempt body bytes, expected {RejectAttemptRecordBodySize}");

        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(0, 4), RejectAttemptRecordLen);
        var crc = ComputeCrc(body);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4, 4), crc);
        return RejectAttemptRecordSize;
    }

    /// <summary>Decodes one reject-attempt record. Same failure
    /// semantics as <see cref="TryDecode"/>.</summary>
    public static bool TryDecodeRejectAttempt(ReadOnlySpan<byte> src, out RejectAttemptRecord record)
    {
        record = default;
        if (src.Length < RejectAttemptRecordSize) return false;
        uint declaredLen = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        if (declaredLen != RejectAttemptRecordLen) return false;
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4, 4));
        var body = src.Slice(8, RejectAttemptRecordBodySize);
        if (ComputeCrc(body) != storedCrc) return false;

        if (body[0] != RecordTypeRejectAttempt) return false;
        if (body[1] != 0) return false;

        int p = 2;
        uint attemptedTradeId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(p, 4)); p += 4;
        ulong attemptTs = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p, 8)); p += 8;
        int declaredDate = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(p, 4)); p += 4;
        ushort rejectCode = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(p, 2)); p += 2;
        uint busterFirm = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(p, 4)); p += 4;
        ulong correlationId = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p, 8)); p += 8;
        _ = p;

        record = new RejectAttemptRecord(attemptedTradeId, attemptTs, declaredDate, rejectCode, busterFirm, correlationId);
        return true;
    }
}

/// <summary>Discriminator for a framed audit record's type, returned
/// by <see cref="AuditRecordCodec.TryGetRecordSize"/> so callers can
/// route to the correct type-specific decoder without re-parsing the
/// recordLen.</summary>
public enum AuditRecordKind : byte
{
    Fill = 0,
    Bust = AuditRecordCodec.RecordTypeBust,
    RejectAttempt = AuditRecordCodec.RecordTypeRejectAttempt,
}

/// <summary>Bust record body (ADR 0008 §1.1). On disk the record is
/// CRC-framed by <see cref="AuditRecordCodec.EncodeBust"/>.</summary>
public readonly record struct BustRecord(
    uint CancelledTradeId,
    ulong BustTransactTimeNanos,
    long SecurityId,
    ushort ReasonCode,
    uint BusterFirm,
    ulong CorrelationId);

/// <summary>Reject-attempt record body (ADR 0008 §2.5). On disk the
/// record is CRC-framed by
/// <see cref="AuditRecordCodec.EncodeRejectAttempt"/>.</summary>
public readonly record struct RejectAttemptRecord(
    uint AttemptedTradeId,
    ulong AttemptTransactTimeNanos,
    int DeclaredTradeDateDays,
    ushort RejectCode,
    uint BusterFirm,
    ulong CorrelationId);

/// <summary>Union of every record type a schema-v2 audit log can
/// contain. Exactly one of <see cref="Fill"/>, <see cref="Bust"/>, or
/// <see cref="RejectAttempt"/> is set per instance, indicated by
/// <see cref="Kind"/>.</summary>
public readonly record struct AuditEntry
{
    public AuditRecordKind Kind { get; }
    public PostTradeRecord Fill { get; }
    public BustRecord Bust { get; }
    public RejectAttemptRecord RejectAttempt { get; }

    public AuditEntry(in PostTradeRecord fill)
    {
        Kind = AuditRecordKind.Fill;
        Fill = fill;
        Bust = default;
        RejectAttempt = default;
    }

    public AuditEntry(in BustRecord bust)
    {
        Kind = AuditRecordKind.Bust;
        Fill = default;
        Bust = bust;
        RejectAttempt = default;
    }

    public AuditEntry(in RejectAttemptRecord rejectAttempt)
    {
        Kind = AuditRecordKind.RejectAttempt;
        Fill = default;
        Bust = default;
        RejectAttempt = rejectAttempt;
    }
}
