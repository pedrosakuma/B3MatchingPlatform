using B3.Exchange.Matching;
using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway;

internal static partial class InboundMessageDecoder
{
    /// <summary>
    /// Field offsets inside the OrderMassActionRequest (template 701)
    /// SBE root block. Offsets are taken from the generated SBE struct.
    /// </summary>
    private static class OrderMassActionRequestOffsets
    {
        public const int MassActionType = 18;          // byte
        public const int ClOrdID = 20;                 // ulong
        public const int OrdTagID = 29;                // byte (0 == null)
        public const int Side = 30;                    // char (0 == null)
        public const int Asset = 32;                   // 6 bytes, all-zero == null
        public const int SecurityID = 38;              // ulong (0 == null)
        public const int InvestorID = 46;              // 6 bytes (V2 only); zeros == null (overlaps SecurityExchange in V1 base)
    }

    private const byte MassActionTypeCancelOrders = 3;

    /// <summary>
    /// Decodes an <c>OrderMassActionRequest</c> body (template 701, spec
    /// §4.8 / #GAP-19) into a <see cref="MassCancelCommand"/>. Returns
    /// <see cref="InboundDecodeOutcome.Success"/> when the request maps
    /// onto the engine's supported subset (cancel-orders, optional Side
    /// + SecurityID filters). Returns
    /// <see cref="InboundDecodeOutcome.UnsupportedFeature"/> for
    /// schema-valid but unimplemented variants. Returns
    /// <see cref="InboundDecodeOutcome.DecodeError"/> for wire values
    /// that violate the SBE schema.
    /// </summary>
    public static InboundDecodeOutcome TryDecodeOrderMassActionRequest(
        ReadOnlySpan<byte> body, ulong enteredAtNanos,
        out MassCancelCommand cmd, out ulong clOrdIdValue, out string? message)
    {
        cmd = null!;
        clOrdIdValue = 0;
        message = null;

        byte massActionType = body[OrderMassActionRequestOffsets.MassActionType];
        byte sideByte = body[OrderMassActionRequestOffsets.Side];
        byte ordTagId = body[OrderMassActionRequestOffsets.OrdTagID];
        ulong securityIdRaw = MemoryMarshal.Read<ulong>(body.Slice(OrderMassActionRequestOffsets.SecurityID, 8));
        clOrdIdValue = MemoryMarshal.Read<ulong>(body.Slice(OrderMassActionRequestOffsets.ClOrdID, 8));

        if (massActionType != MassActionTypeCancelOrders)
        {
            if (massActionType == 2 || massActionType == 4)
            {
                message = $"MassActionType={massActionType} not supported (only CANCEL_ORDERS=3)";
                return InboundDecodeOutcome.UnsupportedFeature;
            }
            message = $"invalid MassActionType={massActionType}";
            return InboundDecodeOutcome.DecodeError;
        }

        if (ordTagId != 0)
        {
            message = "OrdTagID filter not supported (engine does not track ordTagID per order)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }

        var assetSpan = body.Slice(OrderMassActionRequestOffsets.Asset, 6);
        if (!IsAllZero(assetSpan))
        {
            message = "Asset filter not supported (engine does not track per-instrument asset)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }

        var investorSpan = body.Slice(OrderMassActionRequestOffsets.InvestorID, 6);
        if (!IsMassActionInvestorEmpty(investorSpan))
        {
            message = "InvestorID (mass cancel on behalf) not supported";
            return InboundDecodeOutcome.UnsupportedFeature;
        }

        Side? sideFilter = null;
        if (sideByte != 0)
        {
            if (!TryMapSide(sideByte, out var side))
            {
                message = $"invalid Side={sideByte}";
                return InboundDecodeOutcome.DecodeError;
            }
            sideFilter = side;
        }

        long securityId = securityIdRaw == 0 ? 0L : (long)securityIdRaw;

        cmd = new MassCancelCommand(
            SecurityId: securityId,
            SideFilter: sideFilter,
            EnteredAtNanos: enteredAtNanos);
        return InboundDecodeOutcome.Success;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        for (int i = 0; i < span.Length; i++)
            if (span[i] != 0) return false;
        return true;
    }

    /// <summary>
    /// In the OrderMassActionRequest V1 base layout the bytes at offset
    /// 46 hold the constant <c>SecurityExchange</c> = "BVMF" (4 chars,
    /// padded to 6); in V2 the same offset holds <c>InvestorID</c>.
    /// Treat the slot as "no InvestorID" when zeros, "BVMF" (with
    /// trailing zeros/spaces), all-zero, or all-space.
    /// </summary>
    private static bool IsMassActionInvestorEmpty(ReadOnlySpan<byte> span)
    {
        if (IsAllZero(span)) return true;
        if (span.Length >= 4 && span[0] == 'B' && span[1] == 'V' && span[2] == 'M' && span[3] == 'F')
        {
            for (int i = 4; i < span.Length; i++)
                if (span[i] != 0 && span[i] != (byte)' ') return false;
            return true;
        }
        for (int i = 0; i < span.Length; i++)
            if (span[i] != (byte)' ') return false;
        return true;
    }
}
