using B3.EntryPoint.Wire;
using B3.Exchange.Matching;
using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway;

internal static partial class InboundMessageDecoder
{
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

        // CrossType: null/255 → AllOrNone (default). Wire values 1
        // (AllOrNone) and 4 (AgainstBook) accepted; 7/8 (VWAP /
        // ClosingPrice — auction-tied) deferred to a future wave.
        // Issue #218 (Onda L · L5).
        CrossType crossTypeEnum;
        if (crossTypeByte == 255 || crossTypeByte == (byte)CrossType.AllOrNone)
        {
            crossTypeEnum = CrossType.AllOrNone;
        }
        else if (crossTypeByte == (byte)CrossType.AgainstBook)
        {
            crossTypeEnum = CrossType.AgainstBook;
        }
        else
        {
            message = $"unsupported NewOrderCross CrossType={crossTypeByte} (auction-tied variants deferred)";
            return InboundDecodeOutcome.UnsupportedFeature;
        }

        // CrossPrioritization: null/255 → None. Wire 0/1/2 mapped directly.
        CrossPrioritization crossPriEnum;
        if (crossPriByte == 255 || crossPriByte == (byte)CrossPrioritization.None)
        {
            crossPriEnum = CrossPrioritization.None;
        }
        else if (crossPriByte == (byte)CrossPrioritization.BuyPrioritized
            || crossPriByte == (byte)CrossPrioritization.SellPrioritized)
        {
            crossPriEnum = (CrossPrioritization)crossPriByte;
        }
        else
        {
            message = $"invalid NewOrderCross CrossPrioritization={crossPriByte}";
            return InboundDecodeOutcome.UnsupportedFeature;
        }

        // MaxSweepQty: only meaningful when CrossType == AgainstBook;
        // otherwise it must be zero/null (defensive).
        if (maxSweepQty > long.MaxValue)
        {
            message = $"NewOrderCross MaxSweepQty={maxSweepQty} exceeds long.MaxValue";
            return InboundDecodeOutcome.UnsupportedFeature;
        }
        long maxSweepQtySigned = (long)maxSweepQty;
        if (crossTypeEnum != CrossType.AgainstBook && maxSweepQtySigned > 0)
        {
            message = "NewOrderCross MaxSweepQty requires CrossType=AgainstBook";
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
        // §4.10 (#GAP-21): over-length and CR/LF in deskID are
        // BusinessMessageReject(33003) — not session-terminating.
        var varSpan = body.Slice(sidesEnd);
        var varResult = EntryPointVarData.ValidateDetailed(varSpan, EntryPointVarData.NewOrderCrossFields);
        if (!varResult.IsOk)
        {
            if (varResult.IsBusinessReject)
            {
                message = varResult.BmrText();
                return InboundDecodeOutcome.UnsupportedFeature;
            }
            message = varResult.DebugMessage;
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
        cross = new CrossOrderCommand(buy, sell, buyClOrd, sellClOrd, crossId)
        {
            CrossType = crossTypeEnum,
            CrossPrioritization = crossPriEnum,
            MaxSweepQty = maxSweepQtySigned,
        };
        return InboundDecodeOutcome.Success;
    }
}
