using B3.Exchange.Matching;
using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway;

internal static partial class InboundMessageDecoder
{
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
}
