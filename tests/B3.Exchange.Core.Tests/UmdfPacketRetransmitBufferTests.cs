using B3.Exchange.Core;

namespace B3.Exchange.Core.Tests;

/// <summary>
/// Issue #216 (Onda L · L3a) unit tests for the per-channel UMDF
/// retransmit ring. Cover append/lookup/range, FIFO eviction, deep-copy
/// isolation (mutating the input span or the returned array does not
/// corrupt stored entries), and <c>Reset</c> behaviour invoked on
/// <c>SequenceVersion</c> rollover.
/// </summary>
public class UmdfPacketRetransmitBufferTests
{
    [Fact]
    public void Append_ThenTryGet_ReturnsCopyOfStoredBytes()
    {
        var buf = new UmdfPacketRetransmitBuffer(capacity: 4);
        var pkt1 = new byte[] { 1, 2, 3 };
        var pkt2 = new byte[] { 4, 5, 6, 7 };
        buf.Append(seq1, pkt1);
        buf.Append(seq2, pkt2);

        Assert.Equal(2, buf.Count);
        Assert.Equal(seq1, buf.FirstAvailableSeqOrZero);
        Assert.Equal(seq2, buf.LastSeqOrZero);

        Assert.True(buf.TryGet(seq1, out var got1));
        Assert.Equal(pkt1, got1);
        Assert.True(buf.TryGet(seq2, out var got2));
        Assert.Equal(pkt2, got2);
    }

    [Fact]
    public void TryGet_OutsideWindow_ReturnsFalse()
    {
        var buf = new UmdfPacketRetransmitBuffer(capacity: 4);
        buf.Append(10u, new byte[] { 0xAA });
        buf.Append(11u, new byte[] { 0xBB });

        Assert.False(buf.TryGet(9u, out _));   // below window
        Assert.False(buf.TryGet(12u, out _));  // above window
        Assert.False(new UmdfPacketRetransmitBuffer(capacity: 2).TryGet(1u, out _)); // empty
    }

    [Fact]
    public void Append_BeyondCapacity_EvictsOldestFifo()
    {
        var buf = new UmdfPacketRetransmitBuffer(capacity: 3);
        buf.Append(1u, new byte[] { 1 });
        buf.Append(2u, new byte[] { 2 });
        buf.Append(3u, new byte[] { 3 });
        buf.Append(4u, new byte[] { 4 });
        buf.Append(5u, new byte[] { 5 });

        Assert.Equal(3, buf.Count);
        Assert.Equal(2L, buf.Evictions);
        Assert.Equal(3u, buf.FirstAvailableSeqOrZero);
        Assert.Equal(5u, buf.LastSeqOrZero);
        Assert.False(buf.TryGet(1u, out _));
        Assert.False(buf.TryGet(2u, out _));
        Assert.True(buf.TryGet(3u, out var p3)); Assert.Equal(new byte[] { 3 }, p3);
        Assert.True(buf.TryGet(4u, out var p4)); Assert.Equal(new byte[] { 4 }, p4);
        Assert.True(buf.TryGet(5u, out var p5)); Assert.Equal(new byte[] { 5 }, p5);
    }

    [Fact]
    public void Append_DeepCopiesInput_SubsequentMutationsDoNotCorruptStorage()
    {
        var buf = new UmdfPacketRetransmitBuffer(capacity: 2);
        var src = new byte[] { 0x10, 0x20, 0x30 };
        buf.Append(1u, src);
        // Mutate the source after Append — stored copy must be unaffected.
        src[0] = 0xFF;
        src[1] = 0xFF;
        Assert.True(buf.TryGet(1u, out var got));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, got);
    }

    [Fact]
    public void TryGet_ReturnsFreshCopy_CallerMutationsDoNotCorruptStorage()
    {
        var buf = new UmdfPacketRetransmitBuffer(capacity: 2);
        buf.Append(1u, new byte[] { 0x01, 0x02 });
        Assert.True(buf.TryGet(1u, out var first));
        first[0] = 0x99;
        // Second lookup must see the original bytes, not the caller mutation.
        Assert.True(buf.TryGet(1u, out var second));
        Assert.Equal(new byte[] { 0x01, 0x02 }, second);
    }

    [Fact]
    public void TryGetRange_ReturnsContiguousCopiesInOrder()
    {
        var buf = new UmdfPacketRetransmitBuffer(capacity: 8);
        for (uint i = 1; i <= 5; i++) buf.Append(i, new byte[] { (byte)i });

        Assert.True(buf.TryGetRange(fromSeq: 2u, count: 3, out var packets));
        Assert.Equal(3, packets.Length);
        Assert.Equal(new byte[] { 2 }, packets[0]);
        Assert.Equal(new byte[] { 3 }, packets[1]);
        Assert.Equal(new byte[] { 4 }, packets[2]);
    }

    [Fact]
    public void TryGetRange_PartiallyOutsideWindow_ReturnsFalse()
    {
        var buf = new UmdfPacketRetransmitBuffer(capacity: 4);
        for (uint i = 10; i <= 12; i++) buf.Append(i, new byte[] { (byte)i });

        Assert.False(buf.TryGetRange(fromSeq: 9u, count: 2, out _));   // straddles lower edge
        Assert.False(buf.TryGetRange(fromSeq: 12u, count: 2, out _));  // straddles upper edge
        Assert.False(buf.TryGetRange(fromSeq: 11u, count: 0, out _));  // count must be > 0
    }

    [Fact]
    public void Reset_ClearsRing_PreservesEvictionsCounter()
    {
        var buf = new UmdfPacketRetransmitBuffer(capacity: 2);
        buf.Append(1u, new byte[] { 1 });
        buf.Append(2u, new byte[] { 2 });
        buf.Append(3u, new byte[] { 3 }); // evicts seq 1
        Assert.Equal(1L, buf.Evictions);

        buf.Reset();

        Assert.Equal(0, buf.Count);
        Assert.Equal(0u, buf.FirstAvailableSeqOrZero);
        Assert.Equal(0u, buf.LastSeqOrZero);
        Assert.False(buf.TryGet(2u, out _));
        Assert.False(buf.TryGet(3u, out _));
        // Eviction counter is a lifetime monotonic gauge; not reset.
        Assert.Equal(1L, buf.Evictions);

        // After reset, append re-establishes a fresh window.
        buf.Append(100u, new byte[] { 0xAB });
        Assert.True(buf.TryGet(100u, out var got));
        Assert.Equal(new byte[] { 0xAB }, got);
        Assert.Equal(100u, buf.FirstAvailableSeqOrZero);
    }

    [Fact]
    public void Constructor_ZeroOrNegativeCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UmdfPacketRetransmitBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UmdfPacketRetransmitBuffer(-1));
    }

    private const uint seq1 = 100u;
    private const uint seq2 = 101u;
}
