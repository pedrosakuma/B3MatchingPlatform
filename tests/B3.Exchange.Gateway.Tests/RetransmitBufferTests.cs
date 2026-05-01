using B3.Exchange.Gateway;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Tests for <see cref="RetransmitBuffer"/> (issue #46).
/// </summary>
public class RetransmitBufferTests
{
    private static byte[] FrameWithSeq(uint seq, byte[]? body = null)
    {
        // Synthetic 64-byte business frame with a writable EventIndicator
        // byte at offset 28. Real frames are produced by ExecutionReportEncoder
        // and have the same layout invariants.
        var f = new byte[64];
        // mark the seq into the buffer for assertion clarity
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0, 4), seq);
        if (body is not null) Buffer.BlockCopy(body, 0, f, 4, Math.Min(body.Length, 60));
        return f;
    }

    [Fact]
    public void Append_then_TryGet_returns_clones_with_PossResend_set()
    {
        var buf = new RetransmitBuffer(8);
        buf.Append(1, FrameWithSeq(1));
        buf.Append(2, FrameWithSeq(2));
        buf.Append(3, FrameWithSeq(3));

        var snap = buf.TryGet(fromSeq: 2, requestedCount: 2);
        Assert.Null(snap.RejectCode);
        Assert.Equal(2u, snap.FirstSeq);
        Assert.Equal(2u, snap.ActualCount);
        Assert.Equal(2, snap.Frames.Length);
        // Clone must have PossResend bit at offset 28; original must NOT.
        Assert.Equal(RetransmitBuffer.PossResendBit, (byte)(snap.Frames[0][28] & RetransmitBuffer.PossResendBit));
        Assert.Equal(RetransmitBuffer.PossResendBit, (byte)(snap.Frames[1][28] & RetransmitBuffer.PossResendBit));
    }

    [Fact]
    public void TryGet_does_not_mutate_stored_frames()
    {
        var buf = new RetransmitBuffer(4);
        var original = FrameWithSeq(1);
        buf.Append(1, original);
        var snap = buf.TryGet(1, 1);
        Assert.Equal(0, original[28]);              // stored original untouched
        Assert.Equal(RetransmitBuffer.PossResendBit, snap.Frames[0][28]);
        // Repeated calls always return PossResend on the clone (no carry-over corruption).
        var snap2 = buf.TryGet(1, 1);
        Assert.Equal(0, original[28]);
        Assert.Equal(RetransmitBuffer.PossResendBit, snap2.Frames[0][28]);
    }

    [Fact]
    public void Eviction_advances_FirstAvailable()
    {
        var buf = new RetransmitBuffer(3);
        for (uint i = 1; i <= 5; i++) buf.Append(i, FrameWithSeq(i));
        Assert.Equal(3u, buf.FirstAvailableSeqOrZero);     // 1,2 evicted
        Assert.Equal(5u, buf.LastSeqOrZero);
        Assert.Equal(3, buf.Count);
    }

    [Fact]
    public void TryGet_below_window_returns_OUT_OF_RANGE()
    {
        var buf = new RetransmitBuffer(3);
        for (uint i = 1; i <= 5; i++) buf.Append(i, FrameWithSeq(i));     // window = [3,5]
        var snap = buf.TryGet(fromSeq: 1, requestedCount: 2);
        Assert.Equal(B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.OUT_OF_RANGE, snap.RejectCode);
    }

    [Fact]
    public void TryGet_above_window_returns_INVALID_FROMSEQNO()
    {
        var buf = new RetransmitBuffer(8);
        buf.Append(10, FrameWithSeq(10));
        var snap = buf.TryGet(fromSeq: 11, requestedCount: 1);
        Assert.Equal(B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_FROMSEQNO, snap.RejectCode);
    }

    [Fact]
    public void TryGet_empty_buffer_returns_INVALID_FROMSEQNO()
    {
        var buf = new RetransmitBuffer(4);
        var snap = buf.TryGet(1, 1);
        Assert.Equal(B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_FROMSEQNO, snap.RejectCode);
    }

    [Fact]
    public void TryGet_count_zero_returns_INVALID_COUNT()
    {
        var buf = new RetransmitBuffer(4);
        buf.Append(1, FrameWithSeq(1));
        var snap = buf.TryGet(1, 0);
        Assert.Equal(B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_COUNT, snap.RejectCode);
    }

    [Fact]
    public void TryGet_count_above_max_returns_REQUEST_LIMIT_EXCEEDED()
    {
        var buf = new RetransmitBuffer(2048);
        for (uint i = 1; i <= 2000; i++) buf.Append(i, FrameWithSeq(i));
        var snap = buf.TryGet(1, 1001);
        Assert.Equal(B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.REQUEST_LIMIT_EXCEEDED, snap.RejectCode);
    }

    [Fact]
    public void TryGet_clamps_count_when_request_extends_past_last()
    {
        var buf = new RetransmitBuffer(8);
        for (uint i = 1; i <= 3; i++) buf.Append(i, FrameWithSeq(i));
        var snap = buf.TryGet(fromSeq: 2, requestedCount: 10);
        Assert.Null(snap.RejectCode);
        Assert.Equal(2u, snap.FirstSeq);
        Assert.Equal(2u, snap.ActualCount);                     // clamped to last (3) - 2 + 1 = 2
        Assert.Equal(2, snap.Frames.Length);
    }

    [Fact]
    public void TryGet_overflow_arithmetic_does_not_wrap()
    {
        // fromSeq = uint.MaxValue, count > 1 — naive add would overflow.
        // Here the buffer is empty; we expect INVALID_FROMSEQNO not a crash.
        var buf = new RetransmitBuffer(4);
        var snap = buf.TryGet(uint.MaxValue, 5);
        Assert.Equal(B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode.INVALID_FROMSEQNO, snap.RejectCode);
    }
}
