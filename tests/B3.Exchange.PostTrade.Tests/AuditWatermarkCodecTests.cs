using B3.Exchange.PostTrade;

namespace B3.Exchange.PostTrade.Tests;

/// <summary>
/// Tests for the issue #329 PR-5 audit-watermark sidecar codec and the
/// FileAuditLogWriter's reload-on-construction behaviour.
/// </summary>
public class AuditWatermarkCodecTests
{
    [Fact]
    public void Encode_Then_Decode_RoundTrips_PositiveSeq()
    {
        Span<byte> buf = stackalloc byte[AuditWatermarkCodec.FileSize];
        int written = AuditWatermarkCodec.Encode(buf, 0x0123_4567_89AB_CDEFL);
        Assert.Equal(AuditWatermarkCodec.FileSize, written);
        Assert.True(AuditWatermarkCodec.TryDecode(buf, out var seq));
        Assert.Equal(0x0123_4567_89AB_CDEFL, seq);
    }

    [Fact]
    public void Encode_Then_Decode_RoundTrips_Zero()
    {
        Span<byte> buf = stackalloc byte[AuditWatermarkCodec.FileSize];
        AuditWatermarkCodec.Encode(buf, 0L);
        Assert.True(AuditWatermarkCodec.TryDecode(buf, out var seq));
        Assert.Equal(0L, seq);
    }

    [Fact]
    public void TryDecode_BadMagic_ReturnsFalse()
    {
        var buf = new byte[AuditWatermarkCodec.FileSize];
        AuditWatermarkCodec.Encode(buf, 42L);
        buf[0] ^= 0xFF;
        Assert.False(AuditWatermarkCodec.TryDecode(buf, out var seq));
        Assert.Equal(0L, seq);
    }

    [Fact]
    public void TryDecode_CrcMismatch_ReturnsFalse()
    {
        var buf = new byte[AuditWatermarkCodec.FileSize];
        AuditWatermarkCodec.Encode(buf, 12345L);
        // Flip a byte in the payload region (offset 8..15 = seq field)
        buf[8] ^= 0x01;
        Assert.False(AuditWatermarkCodec.TryDecode(buf, out var seq));
        Assert.Equal(0L, seq);
    }

    [Fact]
    public void TryDecode_BadSchemaVersion_ReturnsFalse()
    {
        var buf = new byte[AuditWatermarkCodec.FileSize];
        AuditWatermarkCodec.Encode(buf, 1L);
        // Schema version is uint16 LE at offset 4.
        buf[4] = 0xFE;
        // Recompute CRC so the CRC check passes; we want the schema check
        // to be the one that rejects the buffer.
        uint crc = System.IO.Hashing.Crc32.HashToUInt32(buf.AsSpan(0, 16));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16, 4), crc);
        Assert.False(AuditWatermarkCodec.TryDecode(buf, out _));
    }

    [Fact]
    public void TryDecode_ShortBuffer_ReturnsFalse()
    {
        Assert.False(AuditWatermarkCodec.TryDecode(new byte[AuditWatermarkCodec.FileSize - 1], out _));
    }

    [Fact]
    public void Writer_Reopen_RecoversDurableWatermarkFromSidecar()
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-wm-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte channel = 7;
            using (var w = new FileAuditLogWriter(root, channel))
            {
                Assert.Equal(0, w.DurableThroughCommandSeq);
                w.OnTrade(MakeRec(tradeId: 100));
                w.OnCommandBoundary(commandSeq: 11);
                w.OnTrade(MakeRec(tradeId: 101));
                w.OnCommandBoundary(commandSeq: 12);
                w.Checkpoint();
                Assert.Equal(12, w.DurableThroughCommandSeq);
            }

            using (var w2 = new FileAuditLogWriter(root, channel))
            {
                // Recovered from sidecar.
                Assert.Equal(12, w2.DurableThroughCommandSeq);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Writer_Reopen_WithMissingSidecar_StartsAtZero()
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-wm-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte channel = 3;
            using (var w = new FileAuditLogWriter(root, channel))
            {
                w.OnTrade(MakeRec(tradeId: 1));
                w.OnCommandBoundary(commandSeq: 5);
                // No Checkpoint() — so no sidecar is written.
            }
            using (var w2 = new FileAuditLogWriter(root, channel))
            {
                Assert.Equal(0, w2.DurableThroughCommandSeq);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Writer_Reopen_WithCorruptSidecar_FallsBackToZero()
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-wm-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte channel = 4;
            using (var w = new FileAuditLogWriter(root, channel))
            {
                w.OnCommandBoundary(99);
                w.Checkpoint();
                Assert.Equal(99, w.DurableThroughCommandSeq);
            }
            // Corrupt the sidecar (flip one byte of the seq field).
            var sidecar = Path.Combine(root, channel.ToString(System.Globalization.CultureInfo.InvariantCulture), "audit-watermark.bin");
            Assert.True(File.Exists(sidecar));
            var bytes = File.ReadAllBytes(sidecar);
            bytes[8] ^= 0xFF;
            File.WriteAllBytes(sidecar, bytes);

            using (var w2 = new FileAuditLogWriter(root, channel))
            {
                Assert.Equal(0, w2.DurableThroughCommandSeq);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Writer_Checkpoint_OverwritesSidecarAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-wm-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte channel = 9;
            using var w = new FileAuditLogWriter(root, channel);
            w.OnCommandBoundary(1);
            w.Checkpoint();
            w.OnCommandBoundary(2);
            w.Checkpoint();
            w.OnCommandBoundary(7);
            w.Checkpoint();
            Assert.Equal(7, w.DurableThroughCommandSeq);

            var sidecar = Path.Combine(root, channel.ToString(System.Globalization.CultureInfo.InvariantCulture), "audit-watermark.bin");
            Assert.True(File.Exists(sidecar));
            // No leftover .tmp from atomic-rename
            Assert.False(File.Exists(sidecar + ".tmp"));
            var bytes = File.ReadAllBytes(sidecar);
            Assert.Equal(AuditWatermarkCodec.FileSize, bytes.Length);
            Assert.True(AuditWatermarkCodec.TryDecode(bytes, out var seq));
            Assert.Equal(7, seq);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static PostTradeRecord MakeRec(long tradeId) => new(
        TradeId: (uint)tradeId,
        TransactTimeNanos: (ulong)(1_700_000_000_000_000_000L + tradeId),
        SecurityId: 1234,
        AggressorSide: B3.Exchange.Matching.Side.Buy,
        Quantity: 10,
        PriceMantissa: 100_000,
        BuyClOrdId: (ulong)tradeId,
        SellClOrdId: (ulong)tradeId + 1,
        BuyFirm: 11,
        SellFirm: 22,
        BuyOrderId: tradeId,
        SellOrderId: tradeId + 1);
}
