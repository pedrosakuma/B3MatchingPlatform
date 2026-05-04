using B3.Exchange.Matching;

namespace B3.Exchange.Gateway;

/// <summary>
/// Decodes inbound EntryPoint message bodies (no SBE header — the
/// <see cref="FixpSession"/> consumes that first) into matching engine
/// command records. Field offsets are pinned to the V2 SBE layout; the
/// frame-length validation guarantees the body is exactly the right size.
///
/// Implemented across multiple <c>partial</c> files, one per template
/// family (issue #139): <c>SimpleNewOrder</c>, <c>SimpleReplace</c>,
/// <c>SimpleCancel</c>, <c>NewOrderSingle</c>, <c>OrderCancelReplace</c>,
/// <c>NewOrderCross</c>, <c>OrderMassAction</c>. This file holds the
/// shared constants, the three-valued <see cref="InboundDecodeOutcome"/>
/// result, and the supported-subset Side/OrdType/TIF mappers reused by
/// every per-template decoder.
/// </summary>
internal static partial class InboundMessageDecoder
{
    private const byte RoutingInstructionNull = 255;
    // #241: B3.EntryPoint.Client 0.8.0 has no public surface to set
    // RoutingInstruction, so it always emits the .NET enum default (0).
    // 0 is not one of the schema's defined non-null values (1..4) and
    // is treated here as synonymous with NULL — i.e. "no special
    // routing", which is exactly what we already do for the NULL byte.
    private const byte RoutingInstructionDefault = 0;
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
            case (byte)'1': tif = TimeInForce.Gtc; return true;
            case (byte)'6': tif = TimeInForce.Gtd; return true;
            case (byte)'7': tif = TimeInForce.AtClose; return true;
            case (byte)'A': tif = TimeInForce.GoodForAuction; return true;
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
            case (byte)'3': type = OrderType.StopLoss; return true;
            case (byte)'4': type = OrderType.StopLimit; return true;
            case (byte)'K': type = OrderType.MarketWithLeftover; return true;
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
    /// Three-way TimeInForce classification. All seven schema-valid TIF
    /// values (Day=0, IOC=3, FOK=4, GTC=1, GTD=6, AtTheClose=7,
    /// GoodForAuction='A') are accepted at the wire layer; semantic
    /// gating (GTD plumbing, phase enforcement) lives in the matching
    /// engine. For bytes not in the schema returns <c>false</c> with the
    /// reason <c>null</c>. The schema-NULL byte ('\0') is always reported
    /// as a wire decode error here; callers that accept optional TIF
    /// must short-circuit before invoking this helper.
    /// </summary>
    private static bool TryClassifyTif(byte b, out TimeInForce tif, out string? unsupportedReason)
    {
        unsupportedReason = null;
        switch (b)
        {
            case (byte)'0': tif = TimeInForce.Day; return true;
            case (byte)'3': tif = TimeInForce.IOC; return true;
            case (byte)'4': tif = TimeInForce.FOK; return true;
            case (byte)'1': tif = TimeInForce.Gtc; return true;
            case (byte)'6': tif = TimeInForce.Gtd; return true;
            case (byte)'7': tif = TimeInForce.AtClose; return true;
            case (byte)'A': tif = TimeInForce.GoodForAuction; return true;
            default:
                tif = default;
                return false;
        }
    }
}
