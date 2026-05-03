using B3.Exchange.Matching;
using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway;

internal static partial class InboundMessageDecoder
{
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
}
