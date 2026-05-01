using B3.Exchange.EntryPoint;

namespace B3.Exchange.EntryPoint.Tests;

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
}
