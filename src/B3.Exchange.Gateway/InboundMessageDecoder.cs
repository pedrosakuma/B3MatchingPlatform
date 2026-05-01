using System.Runtime.InteropServices;
using B3.Exchange.Matching;

namespace B3.Exchange.Gateway;

/// <summary>
/// Decodes inbound EntryPoint message bodies (no SBE header — the
/// <see cref="EntryPointSession"/> consumes that first) into matching engine
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

    private const long PriceNull = long.MinValue;

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
}
