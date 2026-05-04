using B3.Exchange.Matching;
using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway;

internal static partial class InboundMessageDecoder
{
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
        // V6 trailer (BlockLength=150). Same semantics as
        // NewOrderSingle's trailer — see issue #238.
        public const int StrategyID = 142;        // int32 (0 == null)
        public const int TradingSubAccount = 146; // uint32 (0 == null)
    }

    private const int OrderCancelReplaceV2BodySize = 142;

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
                ? $"OrdType={ordTypeByte:X2} not supported (only Market, Limit)"
                : $"invalid OrdType={ordTypeByte}";
            return ordTypeUnsupported is not null
                ? InboundDecodeOutcome.UnsupportedFeature
                : InboundDecodeOutcome.DecodeError;
        }
        // #204: TimeInForce on this template is optional; absence means
        // "preserve the resting order's original TIF". When present, any
        // value the engine accepts on a NewOrderSingle is allowed here.
        TimeInForce? newTif = null;
        if (tifByte != TimeInForceOptionalNull)
        {
            if (!TryClassifyTif(tifByte, out var tifValue, out var tifUnsupported))
            {
                message = tifUnsupported is not null
                    ? $"TimeInForce={(char)tifByte} not supported"
                    : $"invalid TimeInForce={tifByte}";
                return tifUnsupported is not null
                    ? InboundDecodeOutcome.UnsupportedFeature
                    : InboundDecodeOutcome.DecodeError;
            }
            newTif = tifValue;
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
        if (routing != RoutingInstructionNull && routing != RoutingInstructionDefault)
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
        // #238: V6 trailer reject — see NewOrderSingle decoder.
        if (body.Length > OrderCancelReplaceV2BodySize)
        {
            int strategyId = MemoryMarshal.Read<int>(body.Slice(OrderCancelReplaceOffsets.StrategyID, 4));
            uint tradingSubAccount = MemoryMarshal.Read<uint>(body.Slice(OrderCancelReplaceOffsets.TradingSubAccount, 4));
            if (strategyId != 0)
            {
                message = $"StrategyID={strategyId} not supported";
                return InboundDecodeOutcome.UnsupportedFeature;
            }
            if (tradingSubAccount != 0)
            {
                message = $"TradingSubAccount={tradingSubAccount} not supported";
                return InboundDecodeOutcome.UnsupportedFeature;
            }
        }
        if (orderId == 0 && origClOrdId == 0)
        {
            message = "OrderCancelReplaceRequest requires either OrderID or OrigClOrdID";
            return InboundDecodeOutcome.DecodeError;
        }
        // Limit replace requires a price; Market replace ignores price (the
        // engine zero-fills it before processing).
        if (ordType == OrderType.Limit && priceMantissa == PriceNull)
        {
            message = "OrderCancelReplaceRequest requires Price (replace cannot remove price)";
            return InboundDecodeOutcome.DecodeError;
        }

        long enginePrice = ordType == OrderType.Market
            ? 0L
            : priceMantissa;

        cmd = new ReplaceOrderCommand(
            ClOrdId: clOrdId.ToString(),
            SecurityId: secId,
            OrderId: (long)orderId,
            NewPriceMantissa: enginePrice,
            NewQuantity: qty,
            EnteredAtNanos: enteredAtNanos)
        {
            NewOrdType = ordType,
            NewTif = newTif,
        };
        return InboundDecodeOutcome.Success;
    }
}
