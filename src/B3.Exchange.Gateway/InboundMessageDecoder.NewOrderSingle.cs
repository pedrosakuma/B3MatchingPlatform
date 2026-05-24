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
    /// not implement; presence of the latter is forwarded to the engine as
    /// an <c>ExecutionReport_Reject</c> rather than terminating the session
    /// (#GAP-15 / #415).
    /// </summary>
    private static class NewOrderSingleOffsets
    {
        public const int OrdTagID = 18;           // byte (0 == null)
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
        public const int InvestorID = 119;        // Prefix(2) + Document(4), all-zero == null
        // V6 trailer (BlockLength=133). Both fields use 0 as the SBE
        // null sentinel. Currently the engine rejects non-null values with
        // ER_Reject since strategy routing / sub-account tagging aren't
        // supported (issue #238).
        public const int StrategyID = 125;        // int32 (0 == null)
        public const int TradingSubAccount = 129; // uint32 (0 == null)
    }

    // V2 root size (matches ExpectedInboundBlockLength). Bodies of this
    // size carry no V6 trailer; bodies of size 133 carry it. See #238.
    private const int NewOrderSingleV2BodySize = 125;

    /// <summary>
    /// Decodes a NewOrderSingleV2 (id=102) body for #GAP-15. Returns
    /// <see cref="InboundDecodeOutcome.Success"/> when the order maps
    /// onto the engine's supported subset (Market/Limit, Day/IOC/FOK, no
    /// stop / iceberg / minimum-fill / RLP). Returns
    /// <see cref="InboundDecodeOutcome.UnsupportedFeature"/> with a
    /// diagnostic text when the wire fields are individually valid but
    /// request a feature the engine does not implement; the caller enqueues
    /// the populated command so the engine emits ER_Reject and keeps the
    /// session open.
    /// Returns <see cref="InboundDecodeOutcome.DecodeError"/> for wire
    /// values that violate the SBE schema (unmapped Side / OrdType / TIF
    /// bytes); the caller terminates with DECODING_ERROR.
    /// </summary>
    public static InboundDecodeOutcome TryDecodeNewOrderSingle(
        ReadOnlySpan<byte> body, uint enteringFirm, ulong enteredAtNanos,
        out NewOrderCommand cmd, out ulong clOrdIdValue, out string? message,
        InboundFatFingerOptions? fatFingerOptions = null)
    {
        var guardrails = NormalizeFatFingerOptions(fatFingerOptions);
        cmd = null!;
        clOrdIdValue = 0;
        message = null;

        ulong clOrdId = MemoryMarshal.Read<ulong>(body.Slice(NewOrderSingleOffsets.ClOrdID, 8));
        long secId = MemoryMarshal.Read<long>(body.Slice(NewOrderSingleOffsets.SecurityID, 8));
        byte sideByte = body[NewOrderSingleOffsets.Side];
        byte ordTypeByte = body[NewOrderSingleOffsets.OrdType];
        byte tifByte = body[NewOrderSingleOffsets.TimeInForce];
        byte routing = body[NewOrderSingleOffsets.RoutingInstruction];
        ulong qtyRaw = MemoryMarshal.Read<ulong>(body.Slice(NewOrderSingleOffsets.OrderQty, 8));
        long qty = qtyRaw > long.MaxValue ? long.MaxValue : (long)qtyRaw;
        long priceMantissa = MemoryMarshal.Read<long>(body.Slice(NewOrderSingleOffsets.PriceMantissa, 8));
        long stopPx = MemoryMarshal.Read<long>(body.Slice(NewOrderSingleOffsets.StopPxMantissa, 8));
        ulong minQty = MemoryMarshal.Read<ulong>(body.Slice(NewOrderSingleOffsets.MinQty, 8));
        ulong maxFloor = MemoryMarshal.Read<ulong>(body.Slice(NewOrderSingleOffsets.MaxFloor, 8));
        byte ordTagId = body[NewOrderSingleOffsets.OrdTagID];
        byte mmpReset = body[NewOrderSingleOffsets.MmProtectionReset];
        byte stp = body[NewOrderSingleOffsets.Stp];
        ushort expireDate = MemoryMarshal.Read<ushort>(body.Slice(NewOrderSingleOffsets.ExpireDate, 2));

        clOrdIdValue = clOrdId;
        var investorSpan = body.Slice(NewOrderSingleOffsets.InvestorID, 6);
        InvestorId? investorId = IsAllZero(investorSpan) ? null : DecodeInvestorId(investorSpan);

        if (!TryMapSide(sideByte, out var side))
        {
            message = $"invalid Side={sideByte}";
            return InboundDecodeOutcome.DecodeError;
        }

        NewOrderCommand UnsupportedCommand(OrderType type, TimeInForce tifValue, long stopPxForCommand = 0L)
        {
            long unsupportedPrice = (type == OrderType.Market || type == OrderType.MarketWithLeftover)
                ? 0L
                : (priceMantissa == PriceNull ? 0L : priceMantissa);
            return new NewOrderCommand(clOrdId.ToString(), secId, side, type, tifValue, unsupportedPrice, qty, enteringFirm, enteredAtNanos)
            {
                MinQty = minQty,
                MaxFloor = maxFloor,
                StopPxMantissa = stopPxForCommand,
                UnsupportedOrderCharacteristic = true,
            };
        }

        TimeInForce tifForUnsupported = TryClassifyTif(tifByte, out var preclassifiedTif, out _)
            ? preclassifiedTif
            : TimeInForce.Day;
        if (!TryClassifyOrdType(ordTypeByte, out var ordType, out var ordTypeUnsupported))
        {
            message = ordTypeUnsupported is not null
                ? $"OrdType={ordTypeByte:X2} not supported (only Market, Limit)"
                : $"invalid OrdType={ordTypeByte}";
            if (ordTypeUnsupported is null)
                return InboundDecodeOutcome.DecodeError;
            cmd = UnsupportedCommand(OrderType.Limit, tifForUnsupported);
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (!TryClassifyTif(tifByte, out var tif, out var tifUnsupported))
        {
            message = tifUnsupported is not null
                ? $"TimeInForce={(char)tifByte} not supported (only Day, IOC, FOK)"
                : $"invalid TimeInForce={tifByte}";
            if (tifUnsupported is null)
                return InboundDecodeOutcome.DecodeError;
            cmd = UnsupportedCommand(ordType, TimeInForce.Day);
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        bool isStop = ordType == OrderType.StopLoss || ordType == OrderType.StopLimit;
        if (isStop)
        {
            if (stopPx == PriceNull)
            {
                message = "Stop orders require StopPx";
                cmd = UnsupportedCommand(ordType, tif);
                return InboundDecodeOutcome.UnsupportedFeature;
            }
        }
        else if (stopPx != PriceNull)
        {
            message = "StopPx only valid on Stop orders";
            cmd = UnsupportedCommand(ordType, tif);
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (maxFloor != 0)
        {
            // #211: iceberg accepted on NewOrderSingle. The engine validates
            // MaxFloor in (0, Quantity] / lot multiple / Limit + Day|Gtc.
        }
        if (routing != RoutingInstructionNull && routing != RoutingInstructionDefault)
        {
            message = $"RoutingInstruction={routing} not supported";
            cmd = UnsupportedCommand(ordType, tif, isStop ? stopPx : 0L);
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (mmpReset != 0)
        {
            message = "MMProtectionReset not supported";
            cmd = UnsupportedCommand(ordType, tif, isStop ? stopPx : 0L);
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (stp != 0)
        {
            message = "SelfTradePreventionInstruction not supported";
            cmd = UnsupportedCommand(ordType, tif, isStop ? stopPx : 0L);
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        if (expireDate != 0)
        {
            message = "ExpireDate not supported (only Day/IOC/FOK)";
            cmd = UnsupportedCommand(ordType, tif, isStop ? stopPx : 0L);
            return InboundDecodeOutcome.UnsupportedFeature;
        }

        // #238: V6 root carries +strategyID@125 (int32, 0=null) and
        // +tradingSubAccount@129 (uint32, 0=null). The body span has
        // already been sliced to BlockLength by the FIXP dispatch, so
        // the trailer is present iff body.Length > V2 size. Engine
        // doesn't honor either field — surface an ER_Reject rather than
        // silently ignore so partners notice.
        if (body.Length > NewOrderSingleV2BodySize)
        {
            int strategyId = MemoryMarshal.Read<int>(body.Slice(NewOrderSingleOffsets.StrategyID, 4));
            uint tradingSubAccount = MemoryMarshal.Read<uint>(body.Slice(NewOrderSingleOffsets.TradingSubAccount, 4));
            if (strategyId != 0)
            {
                message = $"StrategyID={strategyId} not supported";
                cmd = UnsupportedCommand(ordType, tif, isStop ? stopPx : 0L);
                return InboundDecodeOutcome.UnsupportedFeature;
            }
            if (tradingSubAccount != 0)
            {
                message = $"TradingSubAccount={tradingSubAccount} not supported";
                cmd = UnsupportedCommand(ordType, tif, isStop ? stopPx : 0L);
                return InboundDecodeOutcome.UnsupportedFeature;
            }
        }

        long enginePrice = (ordType == OrderType.Market || ordType == OrderType.MarketWithLeftover)
            ? 0L
            : (priceMantissa == PriceNull ? 0L : priceMantissa);
        long engineStopPx = isStop ? stopPx : 0L;

        if (ValidateFatFinger(secId, qty, enginePrice, guardrails) is { } preTradeRejectReason)
        {
            message = FatFingerRejectMessage(preTradeRejectReason, "OrderQty", guardrails);
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
                MaxFloor = maxFloor,
                StopPxMantissa = engineStopPx,
                OrdTagId = ordTagId,
                InvestorId = investorId,
                PreTradeRejectReason = preTradeRejectReason,
            };
            return InboundDecodeOutcome.UnsupportedFeature;
        }

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
            MaxFloor = maxFloor,
            StopPxMantissa = engineStopPx,
            OrdTagId = ordTagId,
            InvestorId = investorId,
        };
        return InboundDecodeOutcome.Success;
    }
}
