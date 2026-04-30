using System.Runtime.InteropServices;
using B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.Exchange.EntryPoint;

/// <summary>
/// Wire-format constants and parsers for the stripped-down EntryPoint protocol:
/// every TCP frame is the SBE 8-byte <see cref="MessageHeader"/> followed by
/// exactly <c>BlockLength</c> bytes (no FIXP envelope, no repeating groups).
/// Total frame size = 8 + BlockLength bytes.
/// </summary>
public static class EntryPointFrameReader
{
    public const ushort SchemaId = 1;

    /// <summary>Outbound (session-level): Terminate (V0). Used as a
    /// "SessionReject" — see <see cref="SessionRejectEncoder"/>.</summary>
    public const ushort TidTerminate = 7;

    /// <summary>Outbound (business-level): BusinessMessageReject (V0). See
    /// <see cref="BusinessMessageRejectEncoder"/>.</summary>
    public const ushort TidBusinessMessageReject = 206;

    /// <summary>Inbound: SimpleNewOrder (V2).</summary>
    public const ushort TidSimpleNewOrder = 100;
    /// <summary>Inbound: SimpleModifyOrder (V2).</summary>
    public const ushort TidSimpleModifyOrder = 101;
    /// <summary>Inbound: OrderCancelRequest (V6 base, no version variants).</summary>
    public const ushort TidOrderCancelRequest = 105;

    /// <summary>Outbound: ExecutionReport_New (V2).</summary>
    public const ushort TidExecutionReportNew = 200;
    /// <summary>Outbound: ExecutionReport_Modify (V2).</summary>
    public const ushort TidExecutionReportModify = 201;
    /// <summary>Outbound: ExecutionReport_Cancel (V2).</summary>
    public const ushort TidExecutionReportCancel = 202;
    /// <summary>Outbound: ExecutionReport_Trade (V2).</summary>
    public const ushort TidExecutionReportTrade = 203;
    /// <summary>Outbound: ExecutionReport_Reject (V2).</summary>
    public const ushort TidExecutionReportReject = 204;

    /// <summary>BlockLength expected for each supported inbound template.</summary>
    public static int ExpectedInboundBlockLength(ushort templateId, ushort version) => (templateId, version) switch
    {
        (TidSimpleNewOrder, 2) => 82,            // SimpleNewOrderV2.MESSAGE_SIZE
        (TidSimpleModifyOrder, 2) => 98,         // SimpleModifyOrderV2.MESSAGE_SIZE
        (TidOrderCancelRequest, 0) => 76,        // OrderCancelRequest.MESSAGE_SIZE (V6 base)
        _ => -1
    };

    public readonly record struct FrameInfo(ushort TemplateId, ushort SchemaId, ushort Version, int BodyLength);

    /// <summary>
    /// Parses the 8-byte SBE header and validates schema/template/blockLength.
    /// Returns false if the header is unsupported (caller should drop the
    /// connection — the protocol has no skip semantics for unknown frames).
    /// </summary>
    public static bool TryParseInboundHeader(ReadOnlySpan<byte> header, out FrameInfo info, out string? error)
    {
        info = default;
        error = null;
        if (header.Length < MessageHeader.MESSAGE_SIZE)
        {
            error = "header too short";
            return false;
        }
        if (!MessageHeader.TryReadHeader(header, out var blockLength, out var templateId, out var schemaId, out var version))
        {
            error = "header parse failed";
            return false;
        }
        if (schemaId != SchemaId)
        {
            error = $"unexpected SchemaId={schemaId}";
            return false;
        }
        int expected = ExpectedInboundBlockLength(templateId, version);
        if (expected < 0)
        {
            error = $"unsupported template={templateId} version={version}";
            return false;
        }
        if (blockLength != expected)
        {
            error = $"BlockLength={blockLength} mismatch (expected {expected}) for template={templateId}";
            return false;
        }
        info = new FrameInfo(templateId, schemaId, version, blockLength);
        return true;
    }

    /// <summary>Writes the 8-byte SBE header for an outbound message.</summary>
    public static void WriteHeader(Span<byte> dst, ushort blockLength, ushort templateId, ushort version)
    {
        if (dst.Length < MessageHeader.MESSAGE_SIZE)
            throw new ArgumentException("buffer too small for SBE header", nameof(dst));
        ref var hdr = ref MemoryMarshal.AsRef<MessageHeader>(dst);
        hdr.BlockLength = blockLength;
        hdr.TemplateId = templateId;
        hdr.SchemaId = SchemaId;
        hdr.Version = version;
    }
}
