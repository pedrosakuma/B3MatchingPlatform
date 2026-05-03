using B3.EntryPoint.Wire;
using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #57 (#GAP-21) — spec §4.10 basic gateway validations applied to
/// the identifier fields shared across the order-entry templates:
///
/// <list type="bullet">
///   <item>CR (0x0D) / LF (0x0A) in <c>senderLocation</c>,
///         <c>enteringTrader</c>, <c>executingTrader</c> (fixed-block
///         ASCII slots) and <c>deskID</c> (varData segment) →
///         <c>BusinessMessageReject(33003 "Line breaks not supported in
///         &lt;fieldName&gt;")</c>.</item>
///   <item><c>memo</c> / <c>deskID</c> varData payload exceeding the
///         schema-declared maximum length →
///         <c>BusinessMessageReject(33003 "&lt;fieldName&gt; too
///         long")</c>.</item>
/// </list>
///
/// These tests exercise the validators directly. End-to-end coverage
/// (the FixpSession dispatch path that turns the validator result into a
/// wire-level BMR frame) lives in
/// <see cref="BusinessHeaderSessionIdValidationTests"/> for sessionID
/// rejects; the per-template CR/LF rejects share the same exit path
/// (WriteBusinessMessageReject with reason=33003).
/// </summary>
public class InboundIdentifierValidationTests
{
    // ---------------------- helpers ----------------------

    private static byte[] BuildFixed(int blockLength)
    {
        // Pad with spaces to mimic the wire (ASCII trader fields are
        // space- or NUL-padded; we use spaces so the validator does not
        // see incidental NUL bytes that could mask issues if a future
        // change inspects them).
        var b = new byte[blockLength];
        b.AsSpan().Fill(0x20);
        return b;
    }

    private static void WriteAscii(byte[] body, int offset, int length, string s)
    {
        var slot = body.AsSpan(offset, length);
        slot.Fill(0x20);
        for (int i = 0; i < s.Length && i < length; i++) slot[i] = (byte)s[i];
    }

    // ---------------------- ContainsCrLf ----------------------

    [Fact]
    public void ContainsCrLf_returns_false_for_clean_ascii()
    {
        Assert.False(EntryPointFixedIdentifiers.ContainsCrLf(System.Text.Encoding.ASCII.GetBytes("TRADER")));
        Assert.False(EntryPointFixedIdentifiers.ContainsCrLf(System.Text.Encoding.ASCII.GetBytes("     ")));
        Assert.False(EntryPointFixedIdentifiers.ContainsCrLf(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData("AB\rCD")]
    [InlineData("AB\nCD")]
    [InlineData("\nABCD")]
    [InlineData("ABCD\r")]
    public void ContainsCrLf_returns_true_for_any_crlf_byte(string content)
    {
        Assert.True(EntryPointFixedIdentifiers.ContainsCrLf(System.Text.Encoding.ASCII.GetBytes(content)));
    }

    // ---------------------- per-template fixed identifier sweep ----------------------

    public static IEnumerable<object[]> IdentifierSlotsByTemplate()
    {
        // (templateId, version, blockLength, fieldName, offset, length)
        yield return new object[] { EntryPointFrameReader.TidNewOrderSingle, (ushort)2, 125, "senderLocation", 32, 10 };
        yield return new object[] { EntryPointFrameReader.TidNewOrderSingle, (ushort)2, 125, "enteringTrader", 42, 5 };
        yield return new object[] { EntryPointFrameReader.TidNewOrderSingle, (ushort)2, 125, "executingTrader", 100, 5 };

        yield return new object[] { EntryPointFrameReader.TidOrderCancelReplaceRequest, (ushort)2, 142, "senderLocation", 32, 10 };
        yield return new object[] { EntryPointFrameReader.TidOrderCancelReplaceRequest, (ushort)2, 142, "enteringTrader", 42, 5 };
        yield return new object[] { EntryPointFrameReader.TidOrderCancelReplaceRequest, (ushort)2, 142, "executingTrader", 116, 5 };

        yield return new object[] { EntryPointFrameReader.TidOrderCancelRequest, (ushort)0, 76, "senderLocation", 56, 10 };
        yield return new object[] { EntryPointFrameReader.TidOrderCancelRequest, (ushort)0, 76, "enteringTrader", 66, 5 };
        yield return new object[] { EntryPointFrameReader.TidOrderCancelRequest, (ushort)0, 76, "executingTrader", 71, 5 };

        yield return new object[] { EntryPointFrameReader.TidNewOrderCross, (ushort)6, 84, "senderLocation", 28, 10 };
        yield return new object[] { EntryPointFrameReader.TidNewOrderCross, (ushort)6, 84, "enteringTrader", 38, 5 };
        yield return new object[] { EntryPointFrameReader.TidNewOrderCross, (ushort)6, 84, "executingTrader", 43, 5 };
    }

    [Theory]
    [MemberData(nameof(IdentifierSlotsByTemplate))]
    public void Validate_rejects_LF_in_identifier(ushort tid, ushort ver, int blockLen, string field, int offset, int length)
    {
        var body = BuildFixed(blockLen);
        WriteAscii(body, offset, length, "AB\nCD");
        var r = EntryPointFixedIdentifiers.Validate(tid, ver, body);
        Assert.False(r.IsOk);
        Assert.Equal(field, r.FieldName);
        Assert.Equal($"Line breaks not supported in {field}", r.BmrText());
    }

    [Theory]
    [MemberData(nameof(IdentifierSlotsByTemplate))]
    public void Validate_rejects_CR_in_identifier(ushort tid, ushort ver, int blockLen, string field, int offset, int length)
    {
        var body = BuildFixed(blockLen);
        WriteAscii(body, offset, length, "AB\rCD");
        var r = EntryPointFixedIdentifiers.Validate(tid, ver, body);
        Assert.False(r.IsOk);
        Assert.Equal(field, r.FieldName);
        Assert.Equal($"Line breaks not supported in {field}", r.BmrText());
    }

    [Theory]
    [InlineData(EntryPointFrameReader.TidNewOrderSingle, (ushort)2, 125)]
    [InlineData(EntryPointFrameReader.TidOrderCancelReplaceRequest, (ushort)2, 142)]
    [InlineData(EntryPointFrameReader.TidOrderCancelRequest, (ushort)0, 76)]
    [InlineData(EntryPointFrameReader.TidNewOrderCross, (ushort)6, 84)]
    public void Validate_passes_clean_identifiers(ushort tid, ushort ver, int blockLen)
    {
        var body = BuildFixed(blockLen);
        var r = EntryPointFixedIdentifiers.Validate(tid, ver, body);
        Assert.True(r.IsOk);
        Assert.Null(r.FieldName);
        Assert.Null(r.BmrText());
    }

    [Theory]
    [InlineData(EntryPointFrameReader.TidSimpleNewOrder, (ushort)2)]
    [InlineData(EntryPointFrameReader.TidSimpleModifyOrder, (ushort)2)]
    public void Validate_skips_templates_without_identifier_slots(ushort tid, ushort ver)
    {
        // SimpleNewOrder / SimpleModifyOrder do not carry the identifier
        // fields in their fixed block; CR/LF anywhere in the body must
        // not produce a §4.10 BMR for these templates.
        var body = new byte[200];
        for (int i = 0; i < body.Length; i++) body[i] = 0x0A; // all LF
        var r = EntryPointFixedIdentifiers.Validate(tid, ver, body);
        Assert.True(r.IsOk);
    }

    // ---------------------- varData over-length / CR-LF on DeskID + Memo ----------------------

    [Fact]
    public void VarData_DeskID_too_long_returns_business_reject_with_spec_text()
    {
        // DeskID schema cap is 20 bytes; declare 21 to trigger
        // FieldTooLong (BMR-eligible per §4.10).
        var spec = EntryPointVarData.ExpectedFor(EntryPointFrameReader.TidNewOrderSingle, 2);
        var blob = new byte[1 /*deskLen*/ + 21 /*payload*/ + 1 /*memoLen*/];
        blob[0] = 21;
        // bytes 1..21 are 0x00 — payload content does not affect length check.
        // blob[22] = 0 (memoLen = 0)
        var r = EntryPointVarData.ValidateDetailed(blob, spec);
        Assert.True(r.IsBusinessReject);
        Assert.Equal(EntryPointVarData.FailureKind.FieldTooLong, r.Kind);
        Assert.Equal("deskID", r.FieldName);
        Assert.Equal("deskID too long", r.BmrText());
    }

    [Fact]
    public void VarData_Memo_too_long_returns_business_reject_with_spec_text()
    {
        // Memo schema cap is 40 bytes; declare 41 on a SimpleNewOrder
        // (Memo-only varData) to trigger FieldTooLong.
        var spec = EntryPointVarData.ExpectedFor(EntryPointFrameReader.TidSimpleNewOrder, 2);
        var blob = new byte[1 + 41];
        blob[0] = 41;
        var r = EntryPointVarData.ValidateDetailed(blob, spec);
        Assert.True(r.IsBusinessReject);
        Assert.Equal(EntryPointVarData.FailureKind.FieldTooLong, r.Kind);
        Assert.Equal("memo", r.FieldName);
        Assert.Equal("memo too long", r.BmrText());
    }

    [Theory]
    [InlineData((byte)0x0A)]
    [InlineData((byte)0x0D)]
    public void VarData_DeskID_with_line_break_returns_business_reject_with_spec_text(byte breakByte)
    {
        var spec = EntryPointVarData.ExpectedFor(EntryPointFrameReader.TidOrderCancelReplaceRequest, 2);
        var blob = new byte[1 + 4 + 1];
        blob[0] = 4;
        blob[1] = (byte)'A';
        blob[2] = breakByte;
        blob[3] = (byte)'B';
        blob[4] = (byte)'C';
        blob[5] = 0; // memo length = 0
        var r = EntryPointVarData.ValidateDetailed(blob, spec);
        Assert.True(r.IsBusinessReject);
        Assert.Equal(EntryPointVarData.FailureKind.ContainsLineBreaks, r.Kind);
        Assert.Equal("deskID", r.FieldName);
        Assert.Equal("Line breaks not supported in deskID", r.BmrText());
    }

    // ---------------------- NewOrderCross varData propagation ----------------------

    private const uint FirmNull = 0xFFFFFFFFu;
    private const uint SessionFirm = 4242u;

    private static byte[] BuildCrossWithVarData(byte deskIdLen, byte memoLen, byte? deskByte = null)
    {
        const int sidesBytes = 2 * 22;
        int total = 84 + 3 + sidesBytes + 1 + deskIdLen + 1 + memoLen;
        var body = new byte[total];
        Span<byte> s = body;

        // Root: minimum required for a Limit cross.
        ulong cross = 7777UL;
        long sec = 12345L;
        ulong qty = 100UL;
        long price = 100_0000L;
        MemoryMarshal.Write(s.Slice(20, 8), in cross);
        MemoryMarshal.Write(s.Slice(48, 8), in sec);
        MemoryMarshal.Write(s.Slice(56, 8), in qty);
        MemoryMarshal.Write(s.Slice(64, 8), in price);
        ushort crossedIndNull = 65535;
        MemoryMarshal.Write(s.Slice(72, 2), in crossedIndNull);
        s[74] = 255; // crossType null
        s[75] = 255; // crossPri null
        // s[76..84] maxSweepQty = 0

        // NoSides group header (3 bytes).
        ushort gbl = 22;
        int cursor = 84;
        MemoryMarshal.Write(s.Slice(cursor, 2), in gbl);
        s[cursor + 2] = 2;
        cursor += 3;

        // Side 0: Buy.
        s[cursor + 0] = (byte)'1';
        uint firmNullLocal = FirmNull;
        MemoryMarshal.Write(s.Slice(cursor + 6, 4), in firmNullLocal);
        ulong c0 = 1001UL;
        MemoryMarshal.Write(s.Slice(cursor + 10, 8), in c0);
        cursor += 22;
        // Side 1: Sell.
        s[cursor + 0] = (byte)'2';
        MemoryMarshal.Write(s.Slice(cursor + 6, 4), in firmNullLocal);
        ulong c1 = 1002UL;
        MemoryMarshal.Write(s.Slice(cursor + 10, 8), in c1);
        cursor += 22;

        // varData.
        s[cursor++] = deskIdLen;
        if (deskByte is byte db)
        {
            for (int i = 0; i < deskIdLen; i++) s[cursor + i] = db;
        }
        cursor += deskIdLen;
        s[cursor++] = memoLen;
        cursor += memoLen;

        return body;
    }

    [Fact]
    public void NewOrderCross_DeskId_too_long_returns_UnsupportedFeature_with_spec_text()
    {
        var body = BuildCrossWithVarData(deskIdLen: 21, memoLen: 0);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Equal("deskID too long", msg);
    }

    [Fact]
    public void NewOrderCross_Memo_too_long_returns_UnsupportedFeature_with_spec_text()
    {
        var body = BuildCrossWithVarData(deskIdLen: 0, memoLen: 41);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Equal("memo too long", msg);
    }

    [Theory]
    [InlineData((byte)0x0A)]
    [InlineData((byte)0x0D)]
    public void NewOrderCross_DeskId_with_line_break_returns_UnsupportedFeature_with_spec_text(byte breakByte)
    {
        // Fill the deskID payload entirely with the offending byte so any
        // index inside the payload trips the line-break detector.
        var body = BuildCrossWithVarData(deskIdLen: 4, memoLen: 0, deskByte: breakByte);
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out var msg);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.UnsupportedFeature, outcome);
        Assert.Equal("Line breaks not supported in deskID", msg);
    }

    [Fact]
    public void NewOrderCross_Clean_varData_returns_Success()
    {
        var body = BuildCrossWithVarData(deskIdLen: 5, memoLen: 10, deskByte: (byte)'X');
        var outcome = InboundMessageDecoder.TryDecodeNewOrderCross(
            body, SessionFirm, 1UL, out _, out _, out _);
        Assert.Equal(InboundMessageDecoder.InboundDecodeOutcome.Success, outcome);
    }
}
