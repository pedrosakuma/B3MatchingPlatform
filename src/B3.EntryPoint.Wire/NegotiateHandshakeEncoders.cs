using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.EntryPoint.Wire;

/// <summary>
/// Byte-level encoder for the FIXP <c>NegotiateResponse</c> message
/// (templateId=2, schemaId=1, version=6, BlockLength=28). Sent in
/// response to an accepted client <c>Negotiate</c> per spec §4.5.3.
///
/// Layout (V6) — pinned to <see cref="FixpSbe.NegotiateResponseData"/>:
///   header(8) | sessionID(uint32 @0) | sessionVerID(uint64 @4)
///           | requestTimestamp(uint64 @12) | enteringFirm(uint32 @20)
///           | semanticVersion(Version @24, 4 bytes: major,minor,patch,build)
/// Total wire size = 8 + 28 = 36 bytes.
/// </summary>
public static class NegotiateResponseEncoder
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;
    public const int Block = FixpSbe.NegotiateResponseData.BLOCK_LENGTH;
    public const int Total = HeaderSize + Block;
    public const ushort TemplateId = FixpSbe.NegotiateResponseData.MESSAGE_ID;
    public const ushort SchemaVersion = 6;

    public static int Encode(Span<byte> dst, uint sessionId, ulong sessionVerId,
        ulong requestTimestampNanos, uint enteringFirm,
        byte semVerMajor, byte semVerMinor, byte semVerPatch, byte semVerBuild = 0)
    {
        if (dst.Length < Total)
            throw new ArgumentException("buffer too small for NegotiateResponse", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)Total,
            blockLength: Block, TemplateId, version: SchemaVersion);
        var body = dst.Slice(HeaderSize, Block);
        body.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), sessionVerId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(12, 8), requestTimestampNanos);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(20, 4), enteringFirm);
        body[24] = semVerMajor;
        body[25] = semVerMinor;
        body[26] = semVerPatch;
        body[27] = semVerBuild;
        return Total;
    }
}

/// <summary>
/// Byte-level encoder for the FIXP <c>NegotiateReject</c> message
/// (templateId=3, schemaId=1, version=0, BlockLength=36). Per spec
/// §4.5.3.1, the rejecting side then sends a <c>Terminate</c> and closes.
///
/// Layout (V0) — pinned to <see cref="FixpSbe.NegotiateRejectData"/>:
///   header(8) | sessionID(uint32 @0) | sessionVerID(uint64 @4)
///           | requestTimestamp(uint64 @12) | enteringFirm(uint32 optional @20)
///           | negotiationRejectCode(uint8 @24)
///           | (3 bytes implicit padding)
///           | currentSessionVerID(uint64 optional @28)
/// Total wire size = 8 + 36 = 44 bytes.
/// </summary>
public static class NegotiateRejectEncoder
{
    private const int HeaderSize = EntryPointFrameReader.WireHeaderSize;
    public const int Block = FixpSbe.NegotiateRejectData.BLOCK_LENGTH;
    public const int Total = HeaderSize + Block;
    public const ushort TemplateId = FixpSbe.NegotiateRejectData.MESSAGE_ID;
    public const ushort SchemaVersion = 0;

    public static int Encode(Span<byte> dst, uint sessionId, ulong sessionVerId,
        ulong requestTimestampNanos, uint? enteringFirm,
        FixpSbe.NegotiationRejectCode rejectCode, ulong? currentSessionVerId)
    {
        if (dst.Length < Total)
            throw new ArgumentException("buffer too small for NegotiateReject", nameof(dst));
        EntryPointFrameReader.WriteHeader(dst, messageLength: (ushort)Total,
            blockLength: Block, TemplateId, version: SchemaVersion);
        var body = dst.Slice(HeaderSize, Block);
        body.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), sessionVerId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(12, 8), requestTimestampNanos);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(20, 4),
            enteringFirm ?? FixpSbe.NegotiateRejectData.EnteringFirmNullValue);
        body[24] = (byte)rejectCode;
        // bytes 25,26,27 are implicit padding (zeroed by Clear()).
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(28, 8),
            currentSessionVerId ?? FixpSbe.NegotiateRejectData.CurrentSessionVerIDNullValue);
        return Total;
    }
}
