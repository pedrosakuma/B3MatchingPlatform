using B3.Exchange.Matching;
using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway;

internal static partial class InboundMessageDecoder
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
}
