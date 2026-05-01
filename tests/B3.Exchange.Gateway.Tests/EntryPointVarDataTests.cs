using B3.Exchange.Gateway;

namespace B3.Exchange.Gateway.Tests;

public class EntryPointVarDataTests
{
    private static readonly EntryPointVarData.Field MemoField = new("memo", 40);
    private static readonly EntryPointVarData.Field DeskIdField = new("deskID", 20);

    [Fact]
    public void TryValidate_AcceptsEmpty_WhenNoFieldsExpected()
    {
        var ok = EntryPointVarData.TryValidate([], [], out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void TryValidate_AcceptsEmpty_WhenFieldsAreOptional()
    {
        // varData buffer is empty: all declared fields are simply absent.
        var ok = EntryPointVarData.TryValidate([], new[] { MemoField }, out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void TryValidate_AcceptsEmptyButPresentSegment()
    {
        // Memo field present but length=0.
        var ok = EntryPointVarData.TryValidate(new byte[] { 0 }, new[] { MemoField }, out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void TryValidate_AcceptsMaxLengthSegment()
    {
        var buf = new byte[1 + MemoField.MaxLength];
        buf[0] = MemoField.MaxLength;
        var ok = EntryPointVarData.TryValidate(buf, new[] { MemoField }, out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void TryValidate_RejectsLengthAboveMax()
    {
        var buf = new byte[1 + MemoField.MaxLength + 1];
        buf[0] = (byte)(MemoField.MaxLength + 1);
        var ok = EntryPointVarData.TryValidate(buf, new[] { MemoField }, out var err);
        Assert.False(ok);
        Assert.Contains("exceeds max", err);
    }

    [Fact]
    public void TryValidate_RejectsLengthOverrun()
    {
        // Length prefix says 5, but only 2 bytes follow.
        var buf = new byte[] { 5, 0xAA, 0xBB };
        var ok = EntryPointVarData.TryValidate(buf, new[] { MemoField }, out var err);
        Assert.False(ok);
        Assert.Contains("overruns", err);
    }

    [Fact]
    public void TryValidate_RejectsTrailingBytes()
    {
        // Memo field with length=0, then an extra spurious byte.
        var buf = new byte[] { 0, 0x11 };
        var ok = EntryPointVarData.TryValidate(buf, new[] { MemoField }, out var err);
        Assert.False(ok);
        Assert.Contains("trailing", err);
    }

    [Fact]
    public void TryValidate_AcceptsTwoSegmentsInOrder()
    {
        // OrderCancelRequest: deskID then memo.
        var buf = new byte[] { 3, (byte)'A', (byte)'B', (byte)'C', 2, (byte)'h', (byte)'i' };
        var ok = EntryPointVarData.TryValidate(buf, new[] { DeskIdField, MemoField }, out var err);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void ExpectedFor_KnownTemplates_ReturnsExpectedFields()
    {
        Assert.Equal("memo",
            EntryPointVarData.ExpectedFor(EntryPointFrameReader.TidSimpleNewOrder, 2)[0].Name);
        Assert.Equal("memo",
            EntryPointVarData.ExpectedFor(EntryPointFrameReader.TidSimpleModifyOrder, 2)[0].Name);

        var cancel = EntryPointVarData.ExpectedFor(EntryPointFrameReader.TidOrderCancelRequest, 0);
        Assert.Equal(2, cancel.Length);
        Assert.Equal("deskID", cancel[0].Name);
        Assert.Equal("memo", cancel[1].Name);
    }

    [Fact]
    public void ExpectedFor_UnknownTemplate_ReturnsEmpty()
    {
        Assert.Equal(0, EntryPointVarData.ExpectedFor(9999, 0).Length);
    }

    [Fact]
    public void TryReadNext_IteratesAllSegments()
    {
        var buf = new byte[] { 3, (byte)'A', (byte)'B', (byte)'C', 0, 1, (byte)'X' };
        ReadOnlySpan<byte> remaining = buf;

        Assert.True(EntryPointVarData.TryReadNext(ref remaining, out var s1));
        Assert.Equal(3, s1.Length);
        Assert.Equal((byte)'A', s1[0]);

        Assert.True(EntryPointVarData.TryReadNext(ref remaining, out var s2));
        Assert.Equal(0, s2.Length);

        Assert.True(EntryPointVarData.TryReadNext(ref remaining, out var s3));
        Assert.Equal(1, s3.Length);
        Assert.Equal((byte)'X', s3[0]);

        Assert.False(EntryPointVarData.TryReadNext(ref remaining, out _));
    }

    [Fact]
    public void TryReadNext_RejectsOverrun()
    {
        ReadOnlySpan<byte> remaining = new byte[] { 5, 0xAA };
        Assert.False(EntryPointVarData.TryReadNext(ref remaining, out _));
    }

    // ──────────────────────────────────────────────────────────────────
    // GAP-14 (#50): ValidateDetailed classification — distinguishes
    // §4.10 business-rule rejections (FieldTooLong / ContainsLineBreaks)
    // from structural protocol errors.
    // ──────────────────────────────────────────────────────────────────

    private static readonly EntryPointVarData.Field DeskIdNoLineBreaks =
        new("deskID", 20, RejectLineBreaks: true);

    [Fact]
    public void ValidateDetailed_Ok_returns_None_kind()
    {
        var r = EntryPointVarData.ValidateDetailed(new byte[] { 0 }, new[] { MemoField });
        Assert.True(r.IsOk);
        Assert.Equal(EntryPointVarData.FailureKind.None, r.Kind);
        Assert.Null(r.BmrText());
    }

    [Fact]
    public void ValidateDetailed_FieldTooLong_returns_BmrText_too_long()
    {
        var buf = new byte[1 + MemoField.MaxLength + 1];
        buf[0] = (byte)(MemoField.MaxLength + 1);
        var r = EntryPointVarData.ValidateDetailed(buf, new[] { MemoField });
        Assert.Equal(EntryPointVarData.FailureKind.FieldTooLong, r.Kind);
        Assert.True(r.IsBusinessReject);
        Assert.Equal("memo", r.FieldName);
        Assert.Equal("memo too long", r.BmrText());
    }

    [Fact]
    public void ValidateDetailed_ContainsLF_in_RejectLineBreaks_field_returns_BmrText()
    {
        var buf = new byte[] { 3, (byte)'A', 0x0A, (byte)'B' };
        var r = EntryPointVarData.ValidateDetailed(buf, new[] { DeskIdNoLineBreaks });
        Assert.Equal(EntryPointVarData.FailureKind.ContainsLineBreaks, r.Kind);
        Assert.Equal("deskID", r.FieldName);
        Assert.Equal("Line breaks not supported in deskID", r.BmrText());
    }

    [Fact]
    public void ValidateDetailed_ContainsCR_in_RejectLineBreaks_field_returns_BmrText()
    {
        var buf = new byte[] { 3, (byte)'A', 0x0D, (byte)'B' };
        var r = EntryPointVarData.ValidateDetailed(buf, new[] { DeskIdNoLineBreaks });
        Assert.Equal(EntryPointVarData.FailureKind.ContainsLineBreaks, r.Kind);
        Assert.Equal("Line breaks not supported in deskID", r.BmrText());
    }

    [Fact]
    public void ValidateDetailed_LineBreak_in_field_without_flag_is_accepted()
    {
        // Memo (no RejectLineBreaks) is allowed to contain LF/CR.
        var buf = new byte[] { 3, (byte)'A', 0x0A, (byte)'B' };
        var r = EntryPointVarData.ValidateDetailed(buf, new[] { MemoField });
        Assert.True(r.IsOk);
    }

    [Fact]
    public void ValidateDetailed_Truncated_is_protocol_error_not_BMR()
    {
        // Length prefix 5 but only 2 bytes of payload follow → overrun.
        var buf = new byte[] { 5, 0xAA, 0xBB };
        var r = EntryPointVarData.ValidateDetailed(buf, new[] { MemoField });
        Assert.Equal(EntryPointVarData.FailureKind.ProtocolError, r.Kind);
        Assert.False(r.IsBusinessReject);
        Assert.Null(r.BmrText());
    }

    [Fact]
    public void ValidateDetailed_TrailingBytes_is_protocol_error()
    {
        // Memo declared 1 byte, but buffer has trailing data.
        var buf = new byte[] { 1, (byte)'X', 0xFF };
        var r = EntryPointVarData.ValidateDetailed(buf, new[] { MemoField });
        Assert.Equal(EntryPointVarData.FailureKind.ProtocolError, r.Kind);
    }

    [Fact]
    public void DeskID_field_in_OrderCancelRequest_spec_rejects_line_breaks()
    {
        var spec = EntryPointVarData.ExpectedFor(EntryPointFrameReader.TidOrderCancelRequest, 0);
        Assert.Equal(2, spec.Length);
        Assert.Equal("deskID", spec[0].Name);
        Assert.True(spec[0].RejectLineBreaks);
        Assert.Equal("memo", spec[1].Name);
        Assert.False(spec[1].RejectLineBreaks);
    }

    [Fact]
    public void ValidateDetailed_DeskIdTooLong_returns_BmrText_too_long()
    {
        // deskID max=20; declare 21 with full payload present.
        var buf = new byte[1 + 21];
        buf[0] = 21;
        var r = EntryPointVarData.ValidateDetailed(buf, new[] { DeskIdNoLineBreaks });
        Assert.Equal(EntryPointVarData.FailureKind.FieldTooLong, r.Kind);
        Assert.Equal("deskID too long", r.BmrText());
    }

    [Fact]
    public void ValidateDetailed_LengthAboveMax_with_truncated_payload_is_protocol_error()
    {
        // Regression for GPT-5.5 review of #50: a length prefix that
        // both (a) exceeds max AND (b) overruns the buffer must be
        // classified as ProtocolError (DECODING_ERROR / Terminate)
        // rather than FieldTooLong (BMR / keep session). The structural
        // failure dominates because the SBE stream is desynchronised.
        var buf = new byte[] { 200 }; // declares 200 bytes but supplies 0
        var r = EntryPointVarData.ValidateDetailed(buf, new[] { MemoField });
        Assert.Equal(EntryPointVarData.FailureKind.ProtocolError, r.Kind);
        Assert.False(r.IsBusinessReject);
        Assert.Null(r.BmrText());
    }
}

