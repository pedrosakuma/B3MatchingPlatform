using System.Buffers.Binary;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.Gateway;

/// <summary>
/// Byte-level encoder for the FIXP <c>EstablishAck</c> message
/// (templateId=5, schemaId=1, version=6, BlockLength=40). Sent in
/// response to an accepted client <c>Establish</c> per spec §4.5.3.
///
/// Layout (V6) — pinned to <see cref="FixpSbe.EstablishAckData"/>:
///   header(8) | sessionID(uint32 @0) | sessionVerID(uint64 @4)
///           | requestTimestamp(uint64 @12) | keepAliveInterval(uint64 @20)
///           | nextSeqNo(uint32 @28) | lastIncomingSeqNo(uint32 @32)
///           | semanticVersion(Version @36, 4 bytes)
/// Total wire size = 8 + 40 = 48 bytes.
/// </summary>
internal static class EstablishAckEncoder
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;
    public const int Block = FixpSbe.EstablishAckData.BLOCK_LENGTH;
    public const int Total = HeaderSize + Block;
    public const ushort TemplateId = FixpSbe.EstablishAckData.MESSAGE_ID;
    public const ushort SchemaVersion = 6;

    public static int Encode(Span<byte> dst, uint sessionId, ulong sessionVerId,
        ulong requestTimestampNanos, ulong keepAliveIntervalMillis,
        uint nextSeqNo, uint lastIncomingSeqNo,
        byte semVerMajor, byte semVerMinor, byte semVerPatch, byte semVerBuild = 0)
    {
        if (dst.Length < Total)
            throw new ArgumentException("buffer too small for EstablishAck", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)Total,
            blockLength: Block, TemplateId, version: SchemaVersion);
        var body = dst.Slice(HeaderSize, Block);
        body.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), sessionVerId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(12, 8), requestTimestampNanos);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(20, 8), keepAliveIntervalMillis);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(28, 4), nextSeqNo);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(32, 4), lastIncomingSeqNo);
        body[36] = semVerMajor;
        body[37] = semVerMinor;
        body[38] = semVerPatch;
        body[39] = semVerBuild;
        return Total;
    }
}

/// <summary>
/// Byte-level encoder for the FIXP <c>EstablishReject</c> message
/// (templateId=6, schemaId=1, version=0, BlockLength=26). Per spec
/// §4.5.3.1, the rejecting side then sends a <c>Terminate</c> and closes.
///
/// Layout (V0) — pinned to <see cref="FixpSbe.EstablishRejectData"/>:
///   header(8) | sessionID(uint32 @0) | sessionVerID(uint64 @4)
///           | requestTimestamp(uint64 @12)
///           | establishmentRejectCode(uint8 @20) | (1 byte padding)
///           | lastIncomingSeqNo(uint32 optional @22)
/// Total wire size = 8 + 26 = 34 bytes.
/// </summary>
internal static class EstablishRejectEncoder
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;
    public const int Block = FixpSbe.EstablishRejectData.BLOCK_LENGTH;
    public const int Total = HeaderSize + Block;
    public const ushort TemplateId = FixpSbe.EstablishRejectData.MESSAGE_ID;
    public const ushort SchemaVersion = 0;

    public static int Encode(Span<byte> dst, uint sessionId, ulong sessionVerId,
        ulong requestTimestampNanos, FixpSbe.EstablishRejectCode rejectCode,
        uint? lastIncomingSeqNo)
    {
        if (dst.Length < Total)
            throw new ArgumentException("buffer too small for EstablishReject", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)Total,
            blockLength: Block, TemplateId, version: SchemaVersion);
        var body = dst.Slice(HeaderSize, Block);
        body.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), sessionVerId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(12, 8), requestTimestampNanos);
        body[20] = (byte)rejectCode;
        // body[21] is implicit padding (zeroed by Clear()).
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(22, 4),
            lastIncomingSeqNo ?? FixpSbe.EstablishRejectData.LastIncomingSeqNoNullValue);
        return Total;
    }
}
