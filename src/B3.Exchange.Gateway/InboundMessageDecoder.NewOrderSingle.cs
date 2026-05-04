using B3.Exchange.Matching;
using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway;

internal static partial class InboundMessageDecoder
{
    /// <summary>
    /// Body offsets for NewOrderSingleV2 (id=102, BlockLength=125). The
    /// fixed root carries the supported subset (ClOrdID/SecurityID/Side/
    /// OrdType/TimeInForce/OrderQty/Price) plus the sub-feature flags
    /// (StopPx/MinQty/MaxFloor/RoutingInstruction/MmProtectionReset/
    /// SelfTradePreventionInstruction/ExpireDate) that the engine does
    /// not implement; presence of the latter triggers a
    /// <c>BusinessMessageReject(33003)</c> rather than terminating the
    /// session (#GAP-15).
    /// </summary>
    private static class NewOrderSingleOffsets
    {
        public const int MmProtectionReset = 19;  // bool byte (0=false)
        public const int ClOrdID = 20;            // ulong
        public const int Stp = 47;                // SelfTradePreventionInstruction (0=NONE)
        public const int SecurityID = 48;         // ulong
        public const int Side = 56;               // byte
        public const int OrdType = 57;            // byte
        public const int TimeInForce = 58;        // byte
        public const int RoutingInstruction = 59; // byte (255 == null)
        public const int OrderQty = 60;           // ulong
        public const int PriceMantissa = 68;      // long (PriceOptional, MinValue == NULL)
        public const int StopPxMantissa = 76;     // long (MinValue == NULL)
        public const int MinQty = 84;             // ulong (0 == null)
        public const int MaxFloor = 92;           // ulong (0 == null)
        public const int ExpireDate = 105;        // ushort (0 == null)
    }

    /// <summary>
    /// Decodes a NewOrderSingleV2 (id=102) body for #GAP-15. Returns
    /// <see cref="InboundDecodeOutcome.Success"/> when the order maps
    /// onto the engine's supported subset (Market/Limit, Day/IOC/FOK, no
    /// stop / iceberg / minimum-fill / RLP). Returns
    /// <see cref="InboundDecodeOutcome.UnsupportedFeature"/> with a
    /// <c>BusinessMessageReject(33003)</c>-bound text when the wire fields
    /// are individually valid but request a feature the engine does not
    /// implement; the caller emits BMR and keeps the session open.
    /// Returns <see cref="InboundDecodeOutcome.DecodeError"/> for wire
    /// values that violate the SBE schema (unmapped Side / OrdType / TIF
    /// bytes); the caller terminates with DECODING_ERROR.
    /// </summary>
    public static InboundDecodeOutcome TryDecodeNewOrderSingle(
        ReadOnlySpan<byte> body, uint enteringFirm, ulong enteredAtNanos,
        out NewOrderCommand cmd, out ulong clOrdIdValue, out string? message)
    {
        cmd = null!;
        clOrdIdValue = 0;
        message = null;

        ulong clOrdId = MemoryMarshal.Read<ulong>(body.Slice(NewOrderSingleOffsets.ClOrdID, 8));
        long secId = MemoryMarshal.Read<long>(body.Slice(NewOrderSingleOffsets.SecurityID, 8));
        byte sideByte = body[NewOrderSingleOffsets.Side];
        byte ordTypeByte = body[NewOrderSingleOffsets.OrdType];
        byte tifByte = body[NewOrderSingleOffsets.TimeInForce];
        byte routing = body[NewOrderSingleOffsets.RoutingInstruction];
        long qty = (long)MemoryMarshal.Read<ulong>(body.Slice(NewOrderSingleOffsets.OrderQty, 8));
        long priceMantissa = MemoryMarshal.Read<long>(body.Slice(NewOrderSingleOffsets.PriceMantissa, 8));
        long stopPx = MemoryMarshal.Read<long>(body.Slice(NewOrderSingleOffsets.StopPxMantissa, 8));
        ulong minQty = MemoryMarshal.Read<ulong>(body.Slice(NewOrderSingleOffsets.MinQty, 8));
        ulong maxFloor = MemoryMarshal.Read<ulong>(body.Slice(NewOrderSingleOffsets.MaxFloor, 8));
        byte mmpReset = body[NewOrderSingleOffsets.MmProtectionReset];
        byte stp = body[NewOrderSingleOffsets.Stp];
        ushort expireDate = MemoryMarshal.Read<ushort>(body.Slice(NewOrderSingleOffsets.ExpireDate, 2));

        clOrdIdValue = clOrdId;

        if (!TryMapSide(sideByte, out var side))
        {
            message = $"invalid Side={sideByte}";
            return InboundDecodeOutcome.DecodeError;
        }
        if (!TryClassifyOrdType(ordTypeByte, out var ordType, out var ordTypeUnsupported))
        {
            message = ordTypeUnsupported is not null
                ? $"OrdType={ordTypeByte:X2} not supported (only Market, Limit)"
                : $"invalid OrdType={ordTypeByte}";
            return ordTypeUnsupported is not null
                ? InboundDecodeOutcome.UnsupportedFeature
                : InboundDecodeOutcome.DecodeError;
        }
        if (!TryClassifyTif(tifByte, out var tif, out var tifUnsupported))
        {
            message = tifUnsupported is not null
                ? $"TimeInForce={(char)tifByte} not supported (only Day, IOC, FOK)"
                : $"invalid TimeInForce={tifByte}";
            return tifUnsupported is not null
                ? InboundDecodeOutcome.UnsupportedFeature
                : InboundDecodeOutcome.DecodeError;
        }
        if (stopPx != PriceNull)
        {
            message = "Stop orders not supported (StopPx must be NULL)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (maxFloor != 0)
        {
            message = "Iceberg orders not supported (MaxFloor must be NULL)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (routing != RoutingInstructionNull)
        {
            message = $"RoutingInstruction={routing} not supported";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (mmpReset != 0)
        {
            message = "MMProtectionReset not supported";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (stp != 0)
        {
            message = "SelfTradePreventionInstruction not supported";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (expireDate != 0)
        {
            message = "ExpireDate not supported (only Day/IOC/FOK)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }

        long enginePrice = ordType == OrderType.Market ? 0L : (priceMantissa == PriceNull ? 0L : priceMantissa);

        cmd = new NewOrderCommand(
            ClOrdId: clOrdId.ToString(),
            SecurityId: secId,
            Side: side,
            Type: ordType,
            Tif: tif,
            PriceMantissa: enginePrice,
            Quantity: qty,
            EnteringFirm: enteringFirm,
            EnteredAtNanos: enteredAtNanos)
        {
            MinQty = minQty,
        };
        return InboundDecodeOutcome.Success;
    }
}
