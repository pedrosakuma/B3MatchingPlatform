using System.Buffers.Binary;
using B3.EntryPoint.Wire;
using B3.Exchange.Matching;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// #236: B3.EntryPoint.Client 0.8.0 stamps inbound business templates
/// with schema version=6 (the schema's root version) — including
/// templates whose root V6 size differs from V2 only by appended
/// optional fields (NewOrderSingle: +strategyID, +tradingSubAccount;
/// OrderCancelReplaceRequest: same +8). Previously the header parser
/// only knew (102, 2)/(104, 2)/(100, 2)/(101, 2), so every business
/// message from EPC 0.8.0 was rejected as UnsupportedTemplate →
/// DECODING_ERROR Terminate.
///
/// The decoder reads only V2-stable offsets, so accepting V6 bodies
/// is a forward-compatible no-op for the engine. These tests pin the
/// new acceptance table entries and prove a 133-byte V6 NewOrderSingle
/// body decodes through <see cref="InboundMessageDecoder"/> with the
/// trailing strategyID/tradingSubAccount silently ignored.
///
/// Native V6 decoding (mapping strategyID/tradingSubAccount into the
/// engine command) is tracked as a follow-up — see issue body for
/// the migration plan.
/// </summary>
public class V6TemplateAcceptanceTests
{
    [Theory]
    [InlineData(EntryPointFrameReader.TidNewOrderSingle, 2, 125)]
    [InlineData(EntryPointFrameReader.TidNewOrderSingle, 6, 133)]
    [InlineData(EntryPointFrameReader.TidOrderCancelReplaceRequest, 2, 142)]
    [InlineData(EntryPointFrameReader.TidOrderCancelReplaceRequest, 6, 150)]
    [InlineData(EntryPointFrameReader.TidSimpleNewOrder, 2, 82)]
    [InlineData(EntryPointFrameReader.TidSimpleNewOrder, 6, 82)]
    [InlineData(EntryPointFrameReader.TidSimpleModifyOrder, 2, 98)]
    [InlineData(EntryPointFrameReader.TidSimpleModifyOrder, 6, 98)]
    public void HeaderParser_AcceptsV2AndV6BlockLengths(ushort templateId, ushort version, int expectedBlockLength)
    {
        Assert.Equal(expectedBlockLength, EntryPointFrameReader.ExpectedInboundBlockLength(templateId, version));
    }

    [Fact]
    public void HeaderParser_RejectsUnknownVersion()
    {
        Assert.Equal(-1, EntryPointFrameReader.ExpectedInboundBlockLength(
            EntryPointFrameReader.TidNewOrderSingle, version: 99));
    }

    /// <summary>
    /// Builds a 133-byte V6 NewOrderSingle body (V2 fixed fields plus
    /// the appended strategyID@125 + tradingSubAccount@129) and asserts
    /// the decoder produces a clean <see cref="NewOrderCommand"/>,
    /// silently ignoring the trailing fields.
    /// </summary>
    [Fact]
    public void V6NewOrderSingleBody_DecodesAsV2_AppendedFieldsIgnored()
    {
        var body = BuildV6NewOrderSingleBody(
            clOrdId: 7777,
            secId: 123_456,
            sideBuy: true,
            qty: 100,
            priceMantissa: 105_000,
            strategyId: 99,
            tradingSubAccount: 88);

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            body, enteringFirm: 1, enteredAtNanos: 0,
            out var cmd, out var clOrdIdValue, out var message);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Null(message);
        Assert.Equal(7777UL, clOrdIdValue);
        Assert.Equal(123_456L, cmd.SecurityId);
        Assert.Equal(Side.Buy, cmd.Side);
        Assert.Equal(OrderType.Limit, cmd.Type);
        Assert.Equal(100L, cmd.Quantity);
        Assert.Equal(105_000L, cmd.PriceMantissa);
    }

    /// <summary>
    /// Same body trimmed to 125 bytes (V2 layout) must keep decoding
    /// identically — V2 clients are still supported.
    /// </summary>
    [Fact]
    public void V2NewOrderSingleBody_StillDecodes()
    {
        var v6 = BuildV6NewOrderSingleBody(
            clOrdId: 4242, secId: 999, sideBuy: false, qty: 50,
            priceMantissa: 200_000, strategyId: 0, tradingSubAccount: 0);
        var v2 = v6.AsSpan(0, 125).ToArray();

        var outcome = InboundMessageDecoder.TryDecodeNewOrderSingle(
            v2, enteringFirm: 1, enteredAtNanos: 0,
            out var cmd, out var clOrdIdValue, out _);

        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
        Assert.Equal(4242UL, clOrdIdValue);
        Assert.Equal(999L, cmd.SecurityId);
        Assert.Equal(Side.Sell, cmd.Side);
        Assert.Equal(50L, cmd.Quantity);
    }

    private static byte[] BuildV6NewOrderSingleBody(
        ulong clOrdId, long secId, bool sideBuy, ulong qty, long priceMantissa,
        int strategyId, uint tradingSubAccount)
    {
        // Layout matches NewOrderSingleOffsets in
        // InboundMessageDecoder.NewOrderSingle.cs. V6 root MESSAGE_SIZE = 133.
        var body = new byte[133];

        // BusinessHeader (16 bytes) — sendingTime(8) + msgSeqNum(4) + possRetransFlag(1) + pad(3)
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(0, 8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8, 4), 1);
        body[12] = 0;

        body[19] = 0; // MmProtectionReset
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(20, 8), clOrdId);
        // Account@28..SecurityID@48 zero-fill is fine (not read).
        body[47] = 0; // Stp
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(48, 8), secId);
        body[56] = sideBuy ? (byte)'1' : (byte)'2';                                            // Side ASCII
        body[57] = (byte)'2';                                                                  // OrdType = Limit (ASCII)
        body[58] = (byte)'0';                                                                  // TimeInForce = Day (ASCII)
        body[59] = 255;                                                                        // RoutingInstruction null
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(60, 8), qty);                     // OrderQty
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(68, 8), priceMantissa);            // PriceMantissa
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(76, 8), long.MinValue);            // StopPxMantissa null
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(84, 8), 0);                       // MinQty
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(92, 8), 0);                       // MaxFloor
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(105, 2), 0);                      // ExpireDate null

        // V6-appended fields (offsets 125, 129) — must be tolerated.
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(125, 4), strategyId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(129, 4), tradingSubAccount);

        return body;
    }
}
