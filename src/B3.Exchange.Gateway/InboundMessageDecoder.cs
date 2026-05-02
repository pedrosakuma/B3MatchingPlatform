using System.Runtime.InteropServices;
using B3.Exchange.Matching;

namespace B3.Exchange.Gateway;

/// <summary>
/// Decodes inbound EntryPoint message bodies (no SBE header — the
/// <see cref="FixpSession"/> consumes that first) into matching engine
/// command records. Field offsets are pinned to the V2 SBE layout; the
/// frame-length validation guarantees the body is exactly the right size.
/// </summary>
internal static class InboundMessageDecoder
{
    /// <summary>
    /// Body offsets for SimpleNewOrderV2 (BlockLength=82).
    /// </summary>
    private static class SimpleNewOrderOffsets
    {
        public const int ClOrdID = 20;        // ulong
        public const int SecurityID = 48;     // ulong
        public const int Side = 56;           // byte (overlaps SecurityExchange constant)
        public const int OrdType = 57;        // byte
        public const int TimeInForce = 58;    // byte
        public const int OrderQty = 60;       // ulong
        public const int PriceMantissa = 68;  // long (PriceOptional, MinValue == NULL)
    }

    /// <summary>
    /// Body offsets for SimpleModifyOrderV2 (BlockLength=98).
    /// </summary>
    private static class SimpleModifyOrderOffsets
    {
        public const int ClOrdID = 20;
        public const int SecurityID = 48;
        public const int Side = 56;
        public const int OrderQty = 60;
        public const int PriceMantissa = 68;
        public const int OrderID = 76;        // ulong (0 == null)
        public const int OrigClOrdID = 84;    // ulong (0 == null)
    }

    /// <summary>
    /// Body offsets for OrderCancelRequest V6 (BlockLength=76).
    /// </summary>
    private static class OrderCancelRequestOffsets
    {
        public const int ClOrdID = 20;
        public const int SecurityID = 28;
        public const int OrderID = 36;        // ulong (0 == null)
        public const int OrigClOrdID = 44;    // ulong (0 == null)
        public const int Side = 52;           // byte
    }

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
    /// Body offsets for OrderCancelReplaceRequestV2 (id=104, BlockLength=142).
    /// </summary>
    private static class OrderCancelReplaceOffsets
    {
        public const int MmProtectionReset = 19;  // bool byte
        public const int ClOrdID = 20;            // ulong
        public const int Stp = 47;                // SelfTradePreventionInstruction
        public const int SecurityID = 48;         // ulong
        public const int Side = 56;               // byte
        public const int OrdType = 57;            // byte
        public const int TimeInForce = 58;        // byte (optional, '\0' == 0 == null)
        public const int RoutingInstruction = 59; // byte (255 == null)
        public const int OrderQty = 60;           // ulong
        public const int PriceMantissa = 68;      // long
        public const int OrderID = 76;            // ulong (0 == null)
        public const int OrigClOrdID = 84;        // ulong (0 == null)
        public const int StopPxMantissa = 92;     // long (MinValue == NULL)
        public const int MinQty = 100;            // ulong (0 == null)
        public const int MaxFloor = 108;          // ulong (0 == null)
        public const int ExpireDate = 122;        // ushort (0 == null)
    }

    private const byte RoutingInstructionNull = 255;
    private const byte TimeInForceOptionalNull = 0; // schema null = '\0'

    private const long PriceNull = long.MinValue;

    /// <summary>
    /// Three-valued outcome for the full NewOrderSingle (102) and
    /// OrderCancelReplaceRequest (104) decoders. Distinguishes
    /// session-terminating wire errors (<see cref="DecodeError"/>) from
    /// recoverable business rejects (<see cref="UnsupportedFeature"/>)
    /// per spec §4.10 / #GAP-15.
    /// </summary>
    public enum InboundDecodeOutcome
    {
        Success = 0,
        DecodeError = 1,
        UnsupportedFeature = 2,
    }

    public static bool TryDecodeNewOrder(ReadOnlySpan<byte> body, uint enteringFirm, ulong enteredAtNanos,
        out NewOrderCommand cmd, out ulong clOrdIdValue, out string? error)
    {
        cmd = null!;
        clOrdIdValue = 0;
        error = null;

        ulong clOrdId = MemoryMarshal.Read<ulong>(body.Slice(SimpleNewOrderOffsets.ClOrdID, 8));
        long secId = MemoryMarshal.Read<long>(body.Slice(SimpleNewOrderOffsets.SecurityID, 8));
        byte sideByte = body[SimpleNewOrderOffsets.Side];
        byte ordTypeByte = body[SimpleNewOrderOffsets.OrdType];
        byte tifByte = body[SimpleNewOrderOffsets.TimeInForce];
        long qty = (long)MemoryMarshal.Read<ulong>(body.Slice(SimpleNewOrderOffsets.OrderQty, 8));
        long priceMantissa = MemoryMarshal.Read<long>(body.Slice(SimpleNewOrderOffsets.PriceMantissa, 8));

        if (!TryMapSide(sideByte, out var side)) { error = $"invalid Side={sideByte}"; return false; }
        if (!TryMapOrdType(ordTypeByte, out var ordType)) { error = $"invalid OrdType={ordTypeByte}"; return false; }
        if (!TryMapTif(tifByte, out var tif)) { error = $"invalid TimeInForce={tifByte}"; return false; }

        // Market orders never carry a meaningful price; engine ignores it.
        long enginePrice = ordType == OrderType.Market ? 0L : (priceMantissa == PriceNull ? 0L : priceMantissa);

        clOrdIdValue = clOrdId;
        cmd = new NewOrderCommand(
            ClOrdId: clOrdId.ToString(),
            SecurityId: secId,
            Side: side,
            Type: ordType,
            Tif: tif,
            PriceMantissa: enginePrice,
            Quantity: qty,
            EnteringFirm: enteringFirm,
            EnteredAtNanos: enteredAtNanos);
        return true;
    }

    public static bool TryDecodeReplace(ReadOnlySpan<byte> body, ulong enteredAtNanos,
        out ReplaceOrderCommand cmd, out ulong clOrdIdValue, out ulong origClOrdIdValue, out string? error)
    {
        cmd = null!;
        clOrdIdValue = 0;
        origClOrdIdValue = 0;
        error = null;

        ulong clOrdId = MemoryMarshal.Read<ulong>(body.Slice(SimpleModifyOrderOffsets.ClOrdID, 8));
        long secId = MemoryMarshal.Read<long>(body.Slice(SimpleModifyOrderOffsets.SecurityID, 8));
        long qty = (long)MemoryMarshal.Read<ulong>(body.Slice(SimpleModifyOrderOffsets.OrderQty, 8));
        long priceMantissa = MemoryMarshal.Read<long>(body.Slice(SimpleModifyOrderOffsets.PriceMantissa, 8));
        ulong orderId = MemoryMarshal.Read<ulong>(body.Slice(SimpleModifyOrderOffsets.OrderID, 8));
        ulong origClOrdId = MemoryMarshal.Read<ulong>(body.Slice(SimpleModifyOrderOffsets.OrigClOrdID, 8));

        if (orderId == 0 && origClOrdId == 0)
        {
            error = "SimpleModifyOrder requires either OrderID or OrigClOrdID";
            return false;
        }
        if (priceMantissa == PriceNull)
        {
            error = "SimpleModifyOrder requires Price (replace cannot remove price)";
            return false;
        }

        clOrdIdValue = clOrdId;
        origClOrdIdValue = origClOrdId;
        cmd = new ReplaceOrderCommand(
            ClOrdId: clOrdId.ToString(),
            SecurityId: secId,
            OrderId: (long)orderId,
            NewPriceMantissa: priceMantissa,
            NewQuantity: qty,
            EnteredAtNanos: enteredAtNanos);
        return true;
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
        if (minQty != 0)
        {
            message = "Minimum-fill orders not supported (MinQty must be NULL)";
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
            EnteredAtNanos: enteredAtNanos);
        return InboundDecodeOutcome.Success;
    }

    /// <summary>
    /// Decodes an OrderCancelReplaceRequestV2 (id=104) body for #GAP-15.
    /// See <see cref="TryDecodeNewOrderSingle"/> for the outcome contract.
    /// TimeInForce on this template is optional in the schema; absence is
    /// accepted (replace inherits the original order's TIF).
    /// </summary>
    public static InboundDecodeOutcome TryDecodeOrderCancelReplace(
        ReadOnlySpan<byte> body, ulong enteredAtNanos,
        out ReplaceOrderCommand cmd, out ulong clOrdIdValue, out ulong origClOrdIdValue, out string? message)
    {
        cmd = null!;
        clOrdIdValue = 0;
        origClOrdIdValue = 0;
        message = null;

        ulong clOrdId = MemoryMarshal.Read<ulong>(body.Slice(OrderCancelReplaceOffsets.ClOrdID, 8));
        long secId = MemoryMarshal.Read<long>(body.Slice(OrderCancelReplaceOffsets.SecurityID, 8));
        byte sideByte = body[OrderCancelReplaceOffsets.Side];
        byte ordTypeByte = body[OrderCancelReplaceOffsets.OrdType];
        byte tifByte = body[OrderCancelReplaceOffsets.TimeInForce];
        byte routing = body[OrderCancelReplaceOffsets.RoutingInstruction];
        long qty = (long)MemoryMarshal.Read<ulong>(body.Slice(OrderCancelReplaceOffsets.OrderQty, 8));
        long priceMantissa = MemoryMarshal.Read<long>(body.Slice(OrderCancelReplaceOffsets.PriceMantissa, 8));
        ulong orderId = MemoryMarshal.Read<ulong>(body.Slice(OrderCancelReplaceOffsets.OrderID, 8));
        ulong origClOrdId = MemoryMarshal.Read<ulong>(body.Slice(OrderCancelReplaceOffsets.OrigClOrdID, 8));
        long stopPx = MemoryMarshal.Read<long>(body.Slice(OrderCancelReplaceOffsets.StopPxMantissa, 8));
        ulong minQty = MemoryMarshal.Read<ulong>(body.Slice(OrderCancelReplaceOffsets.MinQty, 8));
        ulong maxFloor = MemoryMarshal.Read<ulong>(body.Slice(OrderCancelReplaceOffsets.MaxFloor, 8));
        byte mmpReset = body[OrderCancelReplaceOffsets.MmProtectionReset];
        byte stp = body[OrderCancelReplaceOffsets.Stp];
        ushort expireDate = MemoryMarshal.Read<ushort>(body.Slice(OrderCancelReplaceOffsets.ExpireDate, 2));

        clOrdIdValue = clOrdId;
        origClOrdIdValue = origClOrdId;

        if (!TryMapSide(sideByte, out _))
        {
            message = $"invalid Side={sideByte}";
            return InboundDecodeOutcome.DecodeError;
        }
        if (!TryClassifyOrdType(ordTypeByte, out var ordType, out var ordTypeUnsupported))
        {
            message = ordTypeUnsupported is not null
                ? $"OrdType={ordTypeByte:X2} not supported (only Limit on replace)"
                : $"invalid OrdType={ordTypeByte}";
            return ordTypeUnsupported is not null
                ? InboundDecodeOutcome.UnsupportedFeature
                : InboundDecodeOutcome.DecodeError;
        }
        // Replace path can only carry through Limit semantics — the
        // engine's ReplaceOrderCommand has no Type field and reuses the
        // resting order's existing type. Reject Market replace requests
        // explicitly so we don't silently lie about what we did.
        if (ordType != OrderType.Limit)
        {
            message = "Market replace not supported (only Limit replace)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        // TimeInForce on this template is optional in the schema (null =
        // '\0'), meaning "do not change". Anything else than null or Day
        // would request a TIF transition the engine cannot perform on
        // a resting order, so reject with BMR rather than silently
        // accepting and applying the wrong semantics.
        if (tifByte != TimeInForceOptionalNull)
        {
            if (!TryClassifyTif(tifByte, out var tif, out var tifUnsupported))
            {
                message = tifUnsupported is not null
                    ? $"TimeInForce={(char)tifByte} not supported on replace (omit or use Day)"
                    : $"invalid TimeInForce={tifByte}";
                return tifUnsupported is not null
                    ? InboundDecodeOutcome.UnsupportedFeature
                    : InboundDecodeOutcome.DecodeError;
            }
            if (tif != TimeInForce.Day)
            {
                message = "TIF change on replace not supported (omit or use Day)";
                return InboundDecodeOutcome.UnsupportedFeature;
            }
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
        if (minQty != 0)
        {
            message = "Minimum-fill orders not supported (MinQty must be NULL)";
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
        if (orderId == 0 && origClOrdId == 0)
        {
            message = "OrderCancelReplaceRequest requires either OrderID or OrigClOrdID";
            return InboundDecodeOutcome.DecodeError;
        }
        if (priceMantissa == PriceNull)
        {
            message = "OrderCancelReplaceRequest requires Price (replace cannot remove price)";
            return InboundDecodeOutcome.DecodeError;
        }

        cmd = new ReplaceOrderCommand(
            ClOrdId: clOrdId.ToString(),
            SecurityId: secId,
            OrderId: (long)orderId,
            NewPriceMantissa: priceMantissa,
            NewQuantity: qty,
            EnteredAtNanos: enteredAtNanos);
        return InboundDecodeOutcome.Success;
    }

    public static bool TryDecodeCancel(ReadOnlySpan<byte> body, ulong enteredAtNanos,
        out CancelOrderCommand cmd, out ulong clOrdIdValue, out ulong origClOrdIdValue, out string? error)
    {
        cmd = null!;
        clOrdIdValue = 0;
        origClOrdIdValue = 0;
        error = null;

        ulong clOrdId = MemoryMarshal.Read<ulong>(body.Slice(OrderCancelRequestOffsets.ClOrdID, 8));
        long secId = MemoryMarshal.Read<long>(body.Slice(OrderCancelRequestOffsets.SecurityID, 8));
        ulong orderId = MemoryMarshal.Read<ulong>(body.Slice(OrderCancelRequestOffsets.OrderID, 8));
        ulong origClOrdId = MemoryMarshal.Read<ulong>(body.Slice(OrderCancelRequestOffsets.OrigClOrdID, 8));

        if (orderId == 0 && origClOrdId == 0)
        {
            error = "OrderCancelRequest requires either OrderID or OrigClOrdID";
            return false;
        }

        clOrdIdValue = clOrdId;
        origClOrdIdValue = origClOrdId;
        cmd = new CancelOrderCommand(
            ClOrdId: clOrdId.ToString(),
            SecurityId: secId,
            OrderId: (long)orderId,
            EnteredAtNanos: enteredAtNanos);
        return true;
    }

    /// <summary>
    /// Body offsets for NewOrderCross V6 (template 106, root BlockLength=84).
    /// Followed by a NoSides repeating group (3-byte SBE GroupSizeEncoding
    /// header per <c>schemas/b3-entrypoint-messages-8.4.2.xml</c> — uint16
    /// blockLength + uint8 numInGroup, no padding) and DeskID/Memo varData.
    /// </summary>
    private static class NewOrderCrossOffsets
    {
        public const int OrdType = 18;            // CrossOrdType char ('\0' == null == implicit Limit)
        public const int CrossID = 20;            // ulong
        public const int SecurityID = 48;         // ulong
        public const int OrderQty = 56;           // ulong (shared by both legs)
        public const int PriceMantissa = 64;      // long (PriceOptional, MinValue == NULL)
        public const int CrossedIndicator = 72;   // ushort (65535 == null)
        public const int CrossType = 74;          // byte (255 == null)
        public const int CrossPrioritization = 75;// byte (255 == null)
        public const int MaxSweepQty = 76;        // ulong (0 == null)

        // NoSides group entry layout (BlockLength=22 per side).
        public const int SideEntrySize = 22;
        public const int SideEntry_Side = 0;
        public const int SideEntry_EnteringFirm = 6;   // FirmOptional uint32 (0xFFFFFFFF == null)
        public const int SideEntry_ClOrdID = 10;       // ulong
    }

    private const uint FirmOptionalNull = 0xFFFFFFFFu;

    /// <summary>
    /// Decodes a NewOrderCross (template 106) inbound body into a
    /// <see cref="CrossOrderCommand"/> wrapping two
    /// <see cref="NewOrderCommand"/> legs (Buy + Sell at the same
    /// SecurityID/OrderQty/Price). Returns
    /// <see cref="InboundDecodeOutcome.Success"/> when the cross is
    /// well-formed AND uses only the supported subset (Limit cross,
    /// no CrossType / CrossPrioritization / MaxSweepQty, NoSides has
    /// exactly two entries — one Buy and one Sell — with matching
    /// EnteringFirm and well-formed DeskID/Memo varData).
    /// <see cref="InboundDecodeOutcome.UnsupportedFeature"/> is returned
    /// for schema-valid bodies that nonetheless request a cross variant
    /// the simulator does not implement (the caller emits BMR(33003)
    /// using <paramref name="crossId"/> as <c>BusinessRejectRefID</c> and
    /// keeps the session open). <see cref="InboundDecodeOutcome.DecodeError"/>
    /// indicates a wire-level structural error (truncated group / varData
    /// over-runs / invalid Side enum) — the caller terminates with
    /// <c>DECODING_ERROR</c>.
    /// </summary>
    public static InboundDecodeOutcome TryDecodeNewOrderCross(
        ReadOnlySpan<byte> body, uint sessionFirm, ulong enteredAtNanos,
        out CrossOrderCommand cross, out ulong crossId, out string? message)
    {
        cross = null!;
        crossId = 0;
        message = null;

        // 1. Fixed root block sanity (header parsing already verified
        // BlockLength == 84 against the schema; defensive check below).
        const int RootSize = 84;
        if (body.Length < RootSize)
        {
            message = $"NewOrderCross body too short: {body.Length} < {RootSize}";
            return InboundDecodeOutcome.DecodeError;
        }

        crossId = MemoryMarshal.Read<ulong>(body.Slice(NewOrderCrossOffsets.CrossID, 8));
        long secId = MemoryMarshal.Read<long>(body.Slice(NewOrderCrossOffsets.SecurityID, 8));
        long qty = (long)MemoryMarshal.Read<ulong>(body.Slice(NewOrderCrossOffsets.OrderQty, 8));
        long priceMantissa = MemoryMarshal.Read<long>(body.Slice(NewOrderCrossOffsets.PriceMantissa, 8));
        byte ordTypeByte = body[NewOrderCrossOffsets.OrdType];
        byte crossTypeByte = body[NewOrderCrossOffsets.CrossType];
        byte crossPriByte = body[NewOrderCrossOffsets.CrossPrioritization];
        ulong maxSweepQty = MemoryMarshal.Read<ulong>(body.Slice(NewOrderCrossOffsets.MaxSweepQty, 8));

        // 2. Supported-subset gates → BMR(33003).
        if (ordTypeByte != 0 && ordTypeByte != (byte)'2')
        {
            // Schema permits Market crosses + a few others; we only
            // support null-or-Limit. Anything else is "unsupported feature".
            message = "unsupported NewOrderCross OrdType (only Limit / null supported)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (priceMantissa == PriceNull)
        {
            message = "NewOrderCross requires Price (Limit cross cannot omit price)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (qty <= 0)
        {
            message = "NewOrderCross requires positive OrderQty";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (crossTypeByte != 255)
        {
            message = "unsupported NewOrderCross CrossType (only null/AON cross supported)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (crossPriByte != 255)
        {
            message = "unsupported NewOrderCross CrossPrioritization (only null supported)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (maxSweepQty != 0)
        {
            message = "unsupported NewOrderCross MaxSweepQty (sweep semantics not implemented)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }

        // 3. NoSides repeating group: 3-byte header (uint16 blockLength
        // + uint8 numInGroup) + N × 22-byte entries.
        int cursor = RootSize;
        if (body.Length < cursor + 3)
        {
            message = "NewOrderCross truncated before NoSides group header";
            return InboundDecodeOutcome.DecodeError;
        }
        ushort groupBlockLength = MemoryMarshal.Read<ushort>(body.Slice(cursor, 2));
        byte numInGroup = body[cursor + 2];
        cursor += 3;
        if (groupBlockLength != NewOrderCrossOffsets.SideEntrySize)
        {
            message = $"NewOrderCross NoSides blockLength={groupBlockLength} != {NewOrderCrossOffsets.SideEntrySize}";
            return InboundDecodeOutcome.DecodeError;
        }
        if (numInGroup != 2)
        {
            // Spec mandates exactly two sides for NewOrderCross; reject
            // other counts as a business error rather than wire-level.
            message = $"NewOrderCross requires exactly 2 sides (got {numInGroup})";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        int sidesEnd = cursor + numInGroup * NewOrderCrossOffsets.SideEntrySize;
        if (body.Length < sidesEnd)
        {
            message = "NewOrderCross truncated inside NoSides entries";
            return InboundDecodeOutcome.DecodeError;
        }

        // 4. Decode each side; reject duplicate sides + per-side
        // EnteringFirm that does not match the authenticated session
        // firm (prevents a client from spoofing trades attributed to a
        // different firm via the per-side override).
        Span<byte> sidesByte = stackalloc byte[2];
        Span<ulong> clOrdIds = stackalloc ulong[2];
        for (int i = 0; i < 2; i++)
        {
            var entry = body.Slice(cursor + i * NewOrderCrossOffsets.SideEntrySize, NewOrderCrossOffsets.SideEntrySize);
            sidesByte[i] = entry[NewOrderCrossOffsets.SideEntry_Side];
            uint firm = MemoryMarshal.Read<uint>(entry.Slice(NewOrderCrossOffsets.SideEntry_EnteringFirm, 4));
            if (firm != FirmOptionalNull && firm != sessionFirm)
            {
                message = $"NewOrderCross side {i} EnteringFirm={firm} differs from session firm={sessionFirm}";
                return InboundDecodeOutcome.UnsupportedFeature;
            }
            clOrdIds[i] = MemoryMarshal.Read<ulong>(entry.Slice(NewOrderCrossOffsets.SideEntry_ClOrdID, 8));
        }

        if (!TryMapSide(sidesByte[0], out var side0))
        {
            message = $"NewOrderCross side 0 invalid Side={sidesByte[0]}";
            return InboundDecodeOutcome.DecodeError;
        }
        if (!TryMapSide(sidesByte[1], out var side1))
        {
            message = $"NewOrderCross side 1 invalid Side={sidesByte[1]}";
            return InboundDecodeOutcome.DecodeError;
        }
        if (side0 == side1)
        {
            message = "NewOrderCross requires one Buy and one Sell (got duplicate sides)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }

        // 5. Walk varData (DeskID + Memo) so trailing garbage / oversize
        // segments are caught the same way as for other application
        // templates. We do not surface their contents to the engine.
        int varCursor = sidesEnd;
        if (!TryWalkCrossVarData(body, ref varCursor, out var varMessage))
        {
            message = varMessage;
            return InboundDecodeOutcome.DecodeError;
        }

        // 6. Build the two NewOrderCommands (buy first so it rests, sell
        // second so it crosses the resting buy → 1 trade at cross px).
        int buyIndex = side0 == Side.Buy ? 0 : 1;
        int sellIndex = 1 - buyIndex;
        ulong buyClOrd = clOrdIds[buyIndex];
        ulong sellClOrd = clOrdIds[sellIndex];

        var buy = new NewOrderCommand(
            ClOrdId: buyClOrd.ToString(),
            SecurityId: secId,
            Side: Side.Buy,
            Type: OrderType.Limit,
            Tif: TimeInForce.Day,
            PriceMantissa: priceMantissa,
            Quantity: qty,
            EnteringFirm: sessionFirm,
            EnteredAtNanos: enteredAtNanos);
        var sell = new NewOrderCommand(
            ClOrdId: sellClOrd.ToString(),
            SecurityId: secId,
            Side: Side.Sell,
            Type: OrderType.Limit,
            Tif: TimeInForce.Day,
            PriceMantissa: priceMantissa,
            Quantity: qty,
            EnteringFirm: sessionFirm,
            EnteredAtNanos: enteredAtNanos);
        cross = new CrossOrderCommand(buy, sell, buyClOrd, sellClOrd, crossId);
        return InboundDecodeOutcome.Success;
    }

    /// <summary>
    /// Validates the trailing DeskID + Memo varData for NewOrderCross
    /// (length-prefixed bytes per spec §3.5). Mirrors the limits used
    /// by <c>EntryPointVarData</c> for other application templates so
    /// the simulator's behavior is uniform across templates that carry
    /// these fields. Trailing bytes after the last expected segment are
    /// rejected as a structural decode error.
    /// </summary>
    private static bool TryWalkCrossVarData(ReadOnlySpan<byte> body, ref int cursor, out string? message)
    {
        message = null;
        // (Field name, max length in bytes). DeskID is 20, Memo is 40,
        // matching EntryPointVarData's spec for the other application
        // templates that carry these segments.
        for (int i = 0; i < 2; i++)
        {
            string name = i == 0 ? "deskID" : "memo";
            byte max = i == 0 ? (byte)20 : (byte)40;
            if (cursor >= body.Length)
            {
                message = $"NewOrderCross varData truncated before {name} length prefix";
                return false;
            }
            byte len = body[cursor];
            cursor += 1;
            if (len > max)
            {
                message = $"NewOrderCross varData {name} too long ({len} > {max})";
                return false;
            }
            if (cursor + len > body.Length)
            {
                message = $"NewOrderCross varData {name} payload overruns frame";
                return false;
            }
            cursor += len;
        }
        if (cursor != body.Length)
        {
            message = $"NewOrderCross has trailing bytes after varData ({body.Length - cursor})";
            return false;
        }
        return true;
    }

    private static bool TryMapSide(byte b, out Side side)
    {
        switch (b)
        {
            case (byte)'1': side = Side.Buy; return true;
            case (byte)'2': side = Side.Sell; return true;
            default: side = default; return false;
        }
    }

    private static bool TryMapOrdType(byte b, out OrderType type)
    {
        switch (b)
        {
            case (byte)'1': type = OrderType.Market; return true;
            case (byte)'2': type = OrderType.Limit; return true;
            default: type = default; return false;
        }
    }

    private static bool TryMapTif(byte b, out TimeInForce tif)
    {
        switch (b)
        {
            case (byte)'0': tif = TimeInForce.Day; return true;
            case (byte)'3': tif = TimeInForce.IOC; return true;
            case (byte)'4': tif = TimeInForce.FOK; return true;
            default: tif = default; return false;
        }
    }

    /// <summary>
    /// Three-way OrdType classification used by the
    /// NewOrderSingle/OrderCancelReplaceRequest decoders. Returns
    /// <c>true</c> for the supported subset (Market=1, Limit=2). Returns
    /// <c>false</c> with <paramref name="unsupportedReason"/> set when the
    /// byte is a schema-valid OrdType the engine does not implement
    /// (Stop=3, StopLimit=4, MarketWithLeftover=K, RLP=W, Pegged=P —
    /// schema enum values per <c>schemas/b3-entrypoint-messages-8.4.2.xml</c>).
    /// Returns <c>false</c> with <paramref name="unsupportedReason"/>
    /// <c>null</c> for bytes that are not part of the FIX OrdType
    /// enumeration at all — caller terminates with DECODING_ERROR.
    /// </summary>
    private static bool TryClassifyOrdType(byte b, out OrderType type, out string? unsupportedReason)
    {
        unsupportedReason = null;
        switch (b)
        {
            case (byte)'1': type = OrderType.Market; return true;
            case (byte)'2': type = OrderType.Limit; return true;
            case (byte)'3': // STOP_LOSS
            case (byte)'4': // STOP_LIMIT
            case (byte)'K': // MARKET_WITH_LEFTOVER_AS_LIMIT
            case (byte)'W': // RLP
            case (byte)'P': // PEGGED_MIDPOINT
                type = default;
                unsupportedReason = "unsupported OrdType";
                return false;
            default:
                type = default;
                return false;
        }
    }

    /// <summary>
    /// Three-way TimeInForce classification. Returns <c>true</c> for the
    /// supported subset (Day=0, IOC=3, FOK=4). For other schema-valid
    /// values (GTC=1, GTD=6, AtTheClose=7, GoodForAuction='A') returns
    /// <c>false</c> with <paramref name="unsupportedReason"/> populated.
    /// For bytes not in the schema returns <c>false</c> with the reason
    /// <c>null</c>. The schema-NULL byte ('\0') is always reported as a
    /// wire decode error here; callers that accept optional TIF must
    /// short-circuit before invoking this helper.
    /// </summary>
    private static bool TryClassifyTif(byte b, out TimeInForce tif, out string? unsupportedReason)
    {
        unsupportedReason = null;
        switch (b)
        {
            case (byte)'0': tif = TimeInForce.Day; return true;
            case (byte)'3': tif = TimeInForce.IOC; return true;
            case (byte)'4': tif = TimeInForce.FOK; return true;
            case (byte)'1': // GOOD_TILL_CANCEL
            case (byte)'6': // GOOD_TILL_DATE
            case (byte)'7': // AT_THE_CLOSE
            case (byte)'A': // GOOD_FOR_AUCTION
                tif = default;
                unsupportedReason = "unsupported TimeInForce";
                return false;
            default:
                tif = default;
                return false;
        }
    }
}
