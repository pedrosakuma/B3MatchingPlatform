using System.Buffers.Binary;
using System.IO.Hashing;

namespace B3.Exchange.PostTrade;

/// <summary>
/// Sidecar codec for the per-channel audit watermark file (issue #329 PR-5).
/// Persists the highest <c>commandSeq</c> whose trades have been fsync'd
/// to the audit log, so a post-crash boot can gate <see cref="IPostTradeSink"/>
/// during WAL replay and avoid duplicating already-recorded trades.
///
/// Layout (fixed 20 bytes):
/// <list type="bullet">
///   <item><description><c>magic</c> uint32 LE = 0x57503342 ("B3PW")</description></item>
///   <item><description><c>schemaVersion</c> uint16 LE = 1</description></item>
///   <item><description><c>reserved</c> uint16 = 0</description></item>
///   <item><description><c>lastDurableCommandSeq</c> int64 LE</description></item>
///   <item><description><c>crc32</c> uint32 LE — IEEE 802.3 of the first 16 bytes</description></item>
/// </list>
///
/// Atomic update: writer encodes into a tmp file, fsyncs it, then
/// <c>File.Move(..., overwrite: true)</c> — POSIX rename is atomic. A
/// torn or partial file is rejected by the CRC check on read; callers
/// treat that as "watermark unknown" → replay is conservative (re-emits
/// every trade).
/// </summary>
public static class AuditWatermarkCodec
{
    public const uint MagicB3PW = 0x57503342u; // "B3PW" little-endian
    public const ushort SchemaVersion = 1;
    public const int FileSize = 20;
    private const int CrcOffset = 16;

    /// <summary>Encodes the watermark into <paramref name="buffer"/>
    /// (must be at least <see cref="FileSize"/> bytes). Returns the number
    /// of bytes written. The CRC is computed across the leading 16 bytes
    /// and stored in the trailing 4 bytes.</summary>
    public static int Encode(Span<byte> buffer, long lastDurableCommandSeq)
    {
        if (buffer.Length < FileSize)
            throw new ArgumentException($"buffer must be at least {FileSize} bytes", nameof(buffer));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, MagicB3PW);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4, 2), SchemaVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(6, 2), 0);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(8, 8), lastDurableCommandSeq);
        uint crc = Crc32.HashToUInt32(buffer.Slice(0, CrcOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(CrcOffset, 4), crc);
        return FileSize;
    }

    /// <summary>Validates the magic, schema version, reserved bytes, and
    /// CRC and returns the persisted watermark. Returns false (and 0)
    /// when any check fails — callers treat that as "watermark unknown".</summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out long lastDurableCommandSeq)
    {
        lastDurableCommandSeq = 0;
        if (buffer.Length < FileSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer) != MagicB3PW) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2)) != SchemaVersion) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(6, 2)) != 0) return false;
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(CrcOffset, 4));
        uint computed = Crc32.HashToUInt32(buffer.Slice(0, CrcOffset));
        if (stored != computed) return false;
        lastDurableCommandSeq = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(8, 8));
        return true;
    }
}
