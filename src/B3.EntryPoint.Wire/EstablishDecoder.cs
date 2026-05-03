using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FixpSbe = B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.EntryPoint.Wire;

/// <summary>
/// Decoded FIXP <c>Establish</c> root block (spec §4.5.3).
/// </summary>
public readonly record struct EstablishRequest(
    uint SessionId,
    ulong SessionVerId,
    ulong TimestampNanos,
    ulong KeepAliveIntervalMillis,
    uint NextSeqNo,
    ushort CancelOnDisconnectType,
    ulong CodTimeoutWindowMillis);

/// <summary>
/// Byte-level decoder for the FIXP <c>Establish</c> message
/// (templateId=4, BLOCK_LENGTH=42). The optional <c>credentials</c>
/// varData segment is validated upstream by
/// <see cref="EntryPointVarData.TryValidate"/> and intentionally not
/// re-parsed here — Negotiate already authenticated the session.
/// </summary>
public static class EstablishDecoder
{
    public const ushort TemplateId = FixpSbe.EstablishData.MESSAGE_ID;
    public const int BlockLength = FixpSbe.EstablishData.BLOCK_LENGTH;

    public static bool TryDecode(ReadOnlySpan<byte> fixedBlock, out EstablishRequest request, out string? error)
    {
        request = default;
        error = null;
        if (fixedBlock.Length < BlockLength)
        {
            error = $"Establish: block length {fixedBlock.Length} < {BlockLength}";
            return false;
        }
        ref readonly var data = ref MemoryMarshal.AsRef<FixpSbe.EstablishData>(fixedBlock);
        request = new EstablishRequest(
            SessionId: (uint)data.SessionID,
            SessionVerId: (ulong)data.SessionVerID,
            TimestampNanos: data.Timestamp.Time,
            // KeepAliveInterval and CodTimeoutWindow are uint64
            // (DeltaInMillis). Read raw from the field positions to
            // avoid depending on the generated wrapper struct shape.
            KeepAliveIntervalMillis: BinaryPrimitives.ReadUInt64LittleEndian(fixedBlock.Slice(20, 8)),
            NextSeqNo: BinaryPrimitives.ReadUInt32LittleEndian(fixedBlock.Slice(28, 4)),
            CancelOnDisconnectType: BinaryPrimitives.ReadUInt16LittleEndian(fixedBlock.Slice(32, 2)),
            CodTimeoutWindowMillis: BinaryPrimitives.ReadUInt64LittleEndian(fixedBlock.Slice(34, 8)));
        return true;
    }
}
