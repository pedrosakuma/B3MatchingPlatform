using B3.Entrypoint.Fixp.Sbe.V6;
using B3.Exchange.Gateway;
using B3.Exchange.Gateway.Persistence;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Tests for issue #405 cold-read fallback: when a
/// <c>RetransmitRequest.fromSeqNo</c> falls below the bounded
/// in-memory ring, <see cref="RetransmitBuffer"/> consults an
/// optional journal callback so the peer can replay frames that
/// have already been evicted from the hot cache.
///
/// <para>The journal here is a fake in-memory list — the real
/// <see cref="FileFixpOutboundJournal"/> contract (contiguous, in
/// strictly increasing seq order, no internal gaps) is what these
/// tests fake out.</para>
/// </summary>
public class RetransmitBufferJournalFallbackTests
{
    private static byte[] FrameWithSeq(uint seq)
    {
        // 64-byte synthetic frame with seq at offset 0; offset 28 is
        // the EventIndicator (PossResend bit) per RetransmitBuffer
        // contract; stays 0 so we can assert the bit was set on the clone.
        var f = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0, 4), seq);
        return f;
    }

    /// <summary>
    /// Fake journal: stores every (seq → frame) Append and serves
    /// contiguous slices on ReadRange, matching the
    /// <see cref="FileFixpOutboundJournal"/> contract.
    /// </summary>
    private sealed class FakeJournal
    {
        private readonly SortedDictionary<uint, byte[]> _entries = new();
        public int ReadRangeCalls { get; private set; }
        public void Append(uint seq, byte[] frame) => _entries[seq] = frame;
        public void Prune(uint uptoExclusive)
        {
            foreach (var k in _entries.Keys.Where(k => k < uptoExclusive).ToList())
                _entries.Remove(k);
        }
        public IReadOnlyList<OutboundJournalEntry> ReadRange(uint fromSeq, int count)
        {
            ReadRangeCalls++;
            var list = new List<OutboundJournalEntry>(count);
            for (int i = 0; i < count; i++)
            {
                uint s = fromSeq + (uint)i;
                if (!_entries.TryGetValue(s, out var f)) break;  // honest gap stops the slice
                list.Add(new OutboundJournalEntry(s, 0L, f));
            }
            return list;
        }
    }

    private static RetransmitBuffer NewBuffer(int capacity, FakeJournal journal,
        out Action<uint, byte[]> mirrorAppendToJournal)
    {
        var j = journal;
        mirrorAppendToJournal = (seq, frame) => j.Append(seq, frame);
        return new RetransmitBuffer(capacity,
            metrics: null,
            isSuspended: null,
            onAppendPersist: null,

            coldRead: (fromSeq, count) => j.ReadRange(fromSeq, count));
    }

    [Fact]
    public void Range_entirely_below_ring_is_served_from_journal()
    {
        var journal = new FakeJournal();
        var buf = NewBuffer(capacity: 4, journal, out var mirror);

        for (uint i = 1; i <= 10; i++)
        {
            var f = FrameWithSeq(i);
            mirror(i, f);
            buf.Append(i, f);
        }
        // Ring holds seq 7..10; journal holds all 10.
        Assert.Equal(7u, buf.FirstAvailableSeqOrZero);

        var snap = buf.TryGet(fromSeq: 2, requestedCount: 3);

        Assert.Null(snap.RejectCode);
        Assert.Equal(2u, snap.FirstSeq);
        Assert.Equal(3u, snap.ActualCount);
        Assert.Equal(3, snap.Frames.Length);
        // PossResend bit must be set on every cold-served clone too.
        for (int i = 0; i < snap.Frames.Length; i++)
            Assert.Equal(RetransmitBuffer.PossResendBit,
                (byte)(snap.Frames[i][28] & RetransmitBuffer.PossResendBit));
        Assert.Equal(1, journal.ReadRangeCalls);
    }

    [Fact]
    public void Range_straddling_boundary_splices_journal_then_ring()
    {
        var journal = new FakeJournal();
        var buf = NewBuffer(capacity: 4, journal, out var mirror);

        for (uint i = 1; i <= 10; i++)
        {
            var f = FrameWithSeq(i);
            mirror(i, f);
            buf.Append(i, f);
        }
        // Ring holds 7..10; request 5..10 must mix journal (5,6) + ring (7..10).
        var snap = buf.TryGet(fromSeq: 5, requestedCount: 6);

        Assert.Null(snap.RejectCode);
        Assert.Equal(5u, snap.FirstSeq);
        Assert.Equal(6u, snap.ActualCount);
        Assert.Equal(6, snap.Frames.Length);
        // Verify contiguity by reading the embedded seq from each clone.
        for (int i = 0; i < 6; i++)
        {
            uint seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(snap.Frames[i].AsSpan(0, 4));
            Assert.Equal((uint)(5 + i), seq);
            Assert.Equal(RetransmitBuffer.PossResendBit,
                (byte)(snap.Frames[i][28] & RetransmitBuffer.PossResendBit));
        }
    }

    [Fact]
    public void Range_entirely_in_ring_does_not_invoke_cold_read()
    {
        var journal = new FakeJournal();
        var buf = NewBuffer(capacity: 4, journal, out var mirror);

        for (uint i = 1; i <= 10; i++)
        {
            var f = FrameWithSeq(i);
            mirror(i, f);
            buf.Append(i, f);
        }
        var snap = buf.TryGet(fromSeq: 8, requestedCount: 3);
        Assert.Null(snap.RejectCode);
        Assert.Equal(3u, snap.ActualCount);
        Assert.Equal(0, journal.ReadRangeCalls);  // hot path only
    }

    [Fact]
    public void Cold_read_gap_below_pruned_watermark_yields_OUT_OF_RANGE()
    {
        var journal = new FakeJournal();
        var buf = NewBuffer(capacity: 4, journal, out var mirror);

        for (uint i = 1; i <= 10; i++)
        {
            var f = FrameWithSeq(i);
            mirror(i, f);
            buf.Append(i, f);
        }
        // Simulate acked-watermark prune at 5 — journal drops seq < 5.
        journal.Prune(uptoExclusive: 5);

        var snap = buf.TryGet(fromSeq: 3, requestedCount: 2);
        Assert.Equal(RetransmitRejectCode.OUT_OF_RANGE, snap.RejectCode);
        Assert.Empty(snap.Frames);
    }

    [Fact]
    public void Cold_read_partial_serves_contiguous_prefix_only()
    {
        var journal = new FakeJournal();
        var buf = NewBuffer(capacity: 4, journal, out var mirror);

        for (uint i = 1; i <= 10; i++)
        {
            var f = FrameWithSeq(i);
            mirror(i, f);
            buf.Append(i, f);
        }
        // Ring holds 7..10. Prune everything below 4 in journal: a
        // request for 2..6 finds nothing at seq=2 → OUT_OF_RANGE
        // (gap is at the start of the requested cold range).
        journal.Prune(uptoExclusive: 4);

        var snap = buf.TryGet(fromSeq: 2, requestedCount: 5);
        Assert.Equal(RetransmitRejectCode.OUT_OF_RANGE, snap.RejectCode);
    }

    [Fact]
    public void No_cold_read_callback_preserves_OUT_OF_RANGE_for_below_window()
    {
        // Parity test: the legacy constructor (no cold-read) must keep
        // returning OUT_OF_RANGE for fromSeq below the ring; this is
        // what existing #46 / #289 callers depend on.
        var buf = new RetransmitBuffer(capacity: 4);
        for (uint i = 1; i <= 10; i++) buf.Append(i, FrameWithSeq(i));

        var snap = buf.TryGet(fromSeq: 2, requestedCount: 3);
        Assert.Equal(RetransmitRejectCode.OUT_OF_RANGE, snap.RejectCode);
        Assert.Empty(snap.Frames);
    }

    [Fact]
    public void Empty_ring_with_journal_serves_from_journal()
    {
        // E.g. cross-restart: ring nasce vazio, journal carrega o histórico.
        var journal = new FakeJournal();
        for (uint i = 1; i <= 5; i++) journal.Append(i, FrameWithSeq(i));
        var buf = new RetransmitBuffer(capacity: 8,
            metrics: null, isSuspended: null,
            onAppendPersist: null,
            coldRead: (fromSeq, count) => journal.ReadRange(fromSeq, count));

        var snap = buf.TryGet(fromSeq: 1, requestedCount: 5);

        Assert.Null(snap.RejectCode);
        Assert.Equal(1u, snap.FirstSeq);
        Assert.Equal(5u, snap.ActualCount);
        for (int i = 0; i < 5; i++)
            Assert.Equal(RetransmitBuffer.PossResendBit,
                (byte)(snap.Frames[i][28] & RetransmitBuffer.PossResendBit));
    }

    [Fact]
    public void Empty_ring_with_empty_journal_returns_OUT_OF_RANGE_not_INVALID()
    {
        // Issue #405: when a journal is wired but happens to be empty,
        // a below-everything request is OUT_OF_RANGE (pruned), not
        // INVALID_FROMSEQNO (the latter is reserved for above-window).
        var journal = new FakeJournal();
        var buf = new RetransmitBuffer(capacity: 8,
            metrics: null, isSuspended: null,
            onAppendPersist: null,
            coldRead: (fromSeq, count) => journal.ReadRange(fromSeq, count));

        var snap = buf.TryGet(fromSeq: 1, requestedCount: 5);

        Assert.Equal(RetransmitRejectCode.OUT_OF_RANGE, snap.RejectCode);
    }

    [Fact]
    public void Above_window_still_returns_INVALID_FROMSEQNO_with_journal()
    {
        var journal = new FakeJournal();
        var buf = NewBuffer(capacity: 4, journal, out var mirror);

        for (uint i = 1; i <= 10; i++)
        {
            var f = FrameWithSeq(i);
            mirror(i, f);
            buf.Append(i, f);
        }
        var snap = buf.TryGet(fromSeq: 11, requestedCount: 5);
        Assert.Equal(RetransmitRejectCode.INVALID_FROMSEQNO, snap.RejectCode);
        Assert.Equal(0, journal.ReadRangeCalls);
    }

    [Fact]
    public void Cold_read_throws_is_treated_as_no_data()
    {
        // Cold-read errors must never crash the dispatch thread; we
        // downgrade to OUT_OF_RANGE so the peer can resync via Negotiate.
        var buf = new RetransmitBuffer(capacity: 4,
            metrics: null, isSuspended: null,
            onAppendPersist: null,
            coldRead: (_, _) => throw new InvalidOperationException("disk failure"));

        for (uint i = 1; i <= 10; i++) buf.Append(i, FrameWithSeq(i));

        var snap = buf.TryGet(fromSeq: 2, requestedCount: 3);
        Assert.Equal(RetransmitRejectCode.OUT_OF_RANGE, snap.RejectCode);
    }

    [Fact]
    public void Misbehaving_journal_with_gap_is_trimmed_at_the_gap()
    {
        // Defensive: even if a buggy journal returns (5, 7, 8) when
        // asked for (5..8), the buffer must trim at the first gap
        // rather than splicing a corrupt non-contiguous reply.
        var buf = new RetransmitBuffer(capacity: 4,
            metrics: null, isSuspended: null,
            onAppendPersist: null,
            coldRead: (fromSeq, count) =>
            {
                // Return seq 5 then jump to 7.
                return new[]
                {
                    new OutboundJournalEntry(5u, 0L, FrameWithSeq(5)),
                    new OutboundJournalEntry(7u, 0L, FrameWithSeq(7)),
                };
            });

        for (uint i = 8; i <= 11; i++) buf.Append(i, FrameWithSeq(i));
        // Ring holds 8..11; request 5..11 → cold should serve only 5
        // (gap at 6 trims), warm portion skipped because cold doesn't
        // reach firstSeq-1 = 7.
        var snap = buf.TryGet(fromSeq: 5, requestedCount: 7);

        Assert.Null(snap.RejectCode);
        Assert.Equal(5u, snap.FirstSeq);
        Assert.Equal(1u, snap.ActualCount);
    }
}
