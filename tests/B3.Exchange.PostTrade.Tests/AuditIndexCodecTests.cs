using System.Buffers.Binary;
using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTradeTests;

public class AuditIndexCodecTests
{
    [Fact]
    public void FileHeader_RoundTrips()
    {
        Span<byte> buf = stackalloc byte[AuditIndexCodec.FileHeaderSize];
        AuditIndexCodec.WriteFileHeader(buf, channelNumber: 9, tradeDate: new DateOnly(2026, 5, 18));
        var (ch, date) = AuditIndexCodec.ReadFileHeader(buf);
        Assert.Equal(9, ch);
        Assert.Equal(new DateOnly(2026, 5, 18), date);
    }

    [Fact]
    public void FileHeader_BadMagic_Throws()
    {
        var buf = new byte[AuditIndexCodec.FileHeaderSize];
        AuditIndexCodec.WriteFileHeader(buf, 1, new DateOnly(2026, 5, 18));
        buf[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => AuditIndexCodec.ReadFileHeader(buf));
    }

    [Fact]
    public void FileHeader_ReservedBytesNonZero_Throws()
    {
        var buf = new byte[AuditIndexCodec.FileHeaderSize];
        AuditIndexCodec.WriteFileHeader(buf, 1, new DateOnly(2026, 5, 18));
        buf[23] = 1;
        Assert.Throws<InvalidDataException>(() => AuditIndexCodec.ReadFileHeader(buf));
    }

    [Fact]
    public void BlockEntry_RoundTrips_WithFirms()
    {
        var firms = new uint[] { 1, 7, 42, 1234 };
        Span<byte> buf = stackalloc byte[AuditIndexCodec.BlockEntryFixedPrefix + (firms.Length * 4)];
        int written = AuditIndexCodec.EncodeBlockEntry(buf, blockLogOffset: 24UL, recordCount: 17, firms);
        Assert.Equal(buf.Length, written);

        byte[] copy = buf.ToArray();
        Assert.True(AuditIndexCodec.TryReadBlockEntry(copy, out var off, out var rc, out var fc, out var total));
        Assert.Equal(24UL, off);
        Assert.Equal(17, rc);
        Assert.Equal(firms.Length, fc);
        Assert.Equal(buf.Length, total);
        for (int i = 0; i < firms.Length; i++)
            Assert.Equal(firms[i], AuditIndexCodec.ReadFirmId(copy, i));
    }

    [Fact]
    public void BlockEntry_EmptyFirmList_RoundTrips()
    {
        Span<byte> buf = stackalloc byte[AuditIndexCodec.BlockEntryFixedPrefix];
        int n = AuditIndexCodec.EncodeBlockEntry(buf, 100UL, 5, ReadOnlySpan<uint>.Empty);
        Assert.Equal(buf.Length, n);
        byte[] copy = buf.ToArray();
        Assert.True(AuditIndexCodec.TryReadBlockEntry(copy, out var off, out var rc, out var fc, out var total));
        Assert.Equal(100UL, off);
        Assert.Equal(5, rc);
        Assert.Equal(0, fc);
        Assert.Equal(buf.Length, total);
    }

    [Fact]
    public void BlockEntry_Truncated_ReturnsFalse()
    {
        var firms = new uint[] { 1, 2, 3 };
        var buf = new byte[AuditIndexCodec.BlockEntryFixedPrefix + (firms.Length * 4)];
        AuditIndexCodec.EncodeBlockEntry(buf, 0, 1, firms);
        Assert.False(AuditIndexCodec.TryReadBlockEntry(buf.AsSpan(0, buf.Length - 1), out _, out _, out _, out _));
    }

    [Fact]
    public void BlockEntry_WrongEntryLen_ReturnsFalse()
    {
        var firms = new uint[] { 5 };
        var buf = new byte[AuditIndexCodec.BlockEntryFixedPrefix + (firms.Length * 4)];
        AuditIndexCodec.EncodeBlockEntry(buf, 0, 1, firms);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), 999); // declare wrong length
        Assert.False(AuditIndexCodec.TryReadBlockEntry(buf, out _, out _, out _, out _));
    }
}
