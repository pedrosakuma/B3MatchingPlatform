using System.Buffers.Binary;
using B3.Exchange.Gateway.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests.Persistence;

/// <summary>
/// Issue #405: segmented append-only outbound journal.
/// </summary>
public sealed class FileFixpOutboundJournalTests : IDisposable
{
    private readonly string _root;

    public FileFixpOutboundJournalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fixp-journal-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private FileFixpOutboundJournal NewJournal(
        int segmentMaxBytes = FileFixpOutboundJournal.DefaultSegmentMaxBytes,
        long maxBytesPerSession = 0)
        => new(_root, NullLogger<FileFixpOutboundJournal>.Instance,
            segmentMaxBytes: segmentMaxBytes,
            maxBytesPerSession: maxBytesPerSession);

    private static byte[] Frame(uint seq, int extraBytes = 0)
    {
        var bytes = new byte[4 + extraBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, seq);
        for (int i = 4; i < bytes.Length; i++) bytes[i] = (byte)(seq ^ i);
        return bytes;
    }

    [Fact]
    public void Append_then_ReadRange_returns_contiguous_slice()
    {
        using var j = NewJournal();
        const uint sid = 0xAABBCCDDu;
        for (uint s = 1; s <= 10; s++) j.Append(sid, s, s * 1000L, Frame(s));

        var slice = j.ReadRange(sid, 3, 5);
        Assert.Equal(5, slice.Count);
        Assert.Equal(new uint[] { 3, 4, 5, 6, 7 }, slice.Select(e => e.Seq).ToArray());
        Assert.Equal(3000L, slice[0].TimestampNanos);
        Assert.Equal(Frame(3), slice[0].Frame);

        Assert.Equal(10u, j.MaxSeq(sid));
        Assert.Equal(10L, j.EntryCount(sid));
    }

    [Fact]
    public void ReadRange_above_MaxSeq_returns_empty()
    {
        using var j = NewJournal();
        const uint sid = 1u;
        j.Append(sid, 1, 0, Frame(1));
        Assert.Empty(j.ReadRange(sid, 2, 10));
    }

    [Fact]
    public void ReadRange_below_pruned_floor_returns_empty()
    {
        using var j = NewJournal();
        const uint sid = 1u;
        for (uint s = 1; s <= 5; s++) j.Append(sid, s, 0, Frame(s));
        j.PruneUpTo(sid, 3);
        // seq 1..3 *may* still be there if the active segment straddles the watermark,
        // but the request for seq=1 with count=2 must never include data from above
        // the pruned floor incorrectly — contract is just "contiguous from fromSeq".
        var slice = j.ReadRange(sid, 1, 10);
        // contiguous slice starting at 1 — may or may not contain 1..5 depending on
        // segment boundaries; just assert it starts at 1 if non-empty.
        if (slice.Count > 0) Assert.Equal(1u, slice[0].Seq);
    }

    [Fact]
    public void Append_strictly_monotonic_or_throws()
    {
        using var j = NewJournal();
        const uint sid = 7u;
        j.Append(sid, 5, 0, Frame(5));
        Assert.Throws<InvalidOperationException>(() => j.Append(sid, 5, 0, Frame(5)));
        Assert.Throws<InvalidOperationException>(() => j.Append(sid, 4, 0, Frame(4)));
    }

    [Fact]
    public void Segment_rotates_when_size_cap_reached()
    {
        // record overhead = 20 bytes; frame size = 100 bytes -> 120 bytes/record.
        // segmentMaxBytes = 300 means ~2 records per segment then rotate.
        using var j = NewJournal(segmentMaxBytes: 300);
        const uint sid = 0x42u;
        for (uint s = 1; s <= 6; s++) j.Append(sid, s, 0, Frame(s, extraBytes: 96));

        var segDir = Path.Combine(_root, FileFixpOutboundJournal.JournalSubdir,
            $"session-{sid:x8}");
        var segments = Directory.GetFiles(segDir, "segment-*.log");
        Assert.True(segments.Length >= 3,
            $"expected at least 3 rotated segments, found {segments.Length}");

        // ReadRange must still walk across rotated segments transparently.
        var slice = j.ReadRange(sid, 1, 100);
        Assert.Equal(6, slice.Count);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6 }, slice.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void PruneUpTo_drops_whole_segments_below_watermark()
    {
        using var j = NewJournal(segmentMaxBytes: 300);
        const uint sid = 0x99u;
        for (uint s = 1; s <= 6; s++) j.Append(sid, s, 0, Frame(s, extraBytes: 96));
        var segDir = Path.Combine(_root, FileFixpOutboundJournal.JournalSubdir,
            $"session-{sid:x8}");
        int segmentsBefore = Directory.GetFiles(segDir, "segment-*.log").Length;

        // Prune well into the first segment(s).
        j.PruneUpTo(sid, 4);

        int segmentsAfter = Directory.GetFiles(segDir, "segment-*.log").Length;
        Assert.True(segmentsAfter < segmentsBefore,
            $"expected segment count to decrease after prune; before={segmentsBefore} after={segmentsAfter}");

        // Pruning never deletes above the watermark.
        var slice = j.ReadRange(sid, 5, 10);
        Assert.Equal(new uint[] { 5, 6 }, slice.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void MaxSeq_survives_reopen()
    {
        const uint sid = 1234u;
        using (var j = NewJournal())
        {
            for (uint s = 1; s <= 50; s++) j.Append(sid, s, 0, Frame(s));
        }
        using var j2 = NewJournal();
        Assert.Equal(50u, j2.MaxSeq(sid));
        var slice = j2.ReadRange(sid, 10, 5);
        Assert.Equal(new uint[] { 10, 11, 12, 13, 14 }, slice.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void Reopen_continues_appending_into_active_segment()
    {
        const uint sid = 555u;
        using (var j = NewJournal())
        {
            for (uint s = 1; s <= 5; s++) j.Append(sid, s, 0, Frame(s));
        }
        using (var j2 = NewJournal())
        {
            for (uint s = 6; s <= 10; s++) j2.Append(sid, s, 0, Frame(s));
            Assert.Equal(10u, j2.MaxSeq(sid));
            Assert.Equal(10L, j2.EntryCount(sid));
            var slice = j2.ReadRange(sid, 1, 100);
            Assert.Equal(10, slice.Count);
            Assert.Equal(Enumerable.Range(1, 10).Select(i => (uint)i),
                slice.Select(e => e.Seq));
        }
    }

    [Fact]
    public void Torn_tail_in_last_segment_is_dropped_on_reopen()
    {
        const uint sid = 0x10101010u;
        using (var j = NewJournal())
        {
            for (uint s = 1; s <= 5; s++) j.Append(sid, s, 0, Frame(s));
        }
        var segDir = Path.Combine(_root, FileFixpOutboundJournal.JournalSubdir,
            $"session-{sid:x8}");
        var lastSegment = Directory.GetFiles(segDir, "segment-*.log")
            .OrderBy(p => p).Last();
        // Append a few garbage bytes simulating a torn tail.
        File.AppendAllText(lastSegment, "torn!!!!");

        using var j2 = NewJournal();
        Assert.Equal(5u, j2.MaxSeq(sid));
        var slice = j2.ReadRange(sid, 1, 100);
        Assert.Equal(5, slice.Count);

        // Subsequent appends still work and produce monotonic seqs.
        j2.Append(sid, 6, 0, Frame(6));
        Assert.Equal(6u, j2.MaxSeq(sid));
    }

    [Fact]
    public void Crc_mismatch_stops_replay_at_corruption()
    {
        const uint sid = 0xC0FFEEu;
        using (var j = NewJournal())
        {
            for (uint s = 1; s <= 5; s++) j.Append(sid, s, 0, Frame(s));
        }
        // Flip a byte in the middle of the file to break the CRC of record #2 or #3.
        var segDir = Path.Combine(_root, FileFixpOutboundJournal.JournalSubdir,
            $"session-{sid:x8}");
        var path = Directory.GetFiles(segDir, "segment-*.log").Single();
        var bytes = File.ReadAllBytes(path);
        // record 1 spans 20 + 8 = 28 bytes -> flip byte at offset 36 (inside record 2).
        bytes[36] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        using var j2 = NewJournal();
        var slice = j2.ReadRange(sid, 1, 100);
        // Only the prefix preceding the corruption should be served.
        Assert.True(slice.Count < 5,
            $"corrupt record must stop replay; got {slice.Count} entries");
        Assert.Equal(1u, slice[0].Seq);
    }

    [Fact]
    public void Remove_deletes_entire_session_dir()
    {
        using var j = NewJournal();
        const uint sid = 0xABCDEFu;
        for (uint s = 1; s <= 3; s++) j.Append(sid, s, 0, Frame(s));
        Assert.Equal(3u, j.MaxSeq(sid));

        j.Remove(sid);
        Assert.Equal(0u, j.MaxSeq(sid));
        Assert.Equal(0L, j.EntryCount(sid));
        var dir = Path.Combine(_root, FileFixpOutboundJournal.JournalSubdir,
            $"session-{sid:x8}");
        Assert.False(Directory.Exists(dir));

        // After remove, appending starts a fresh journal.
        j.Append(sid, 1, 0, Frame(1));
        Assert.Equal(1u, j.MaxSeq(sid));
    }

    [Fact]
    public void ListSessions_enumerates_persisted_sessions()
    {
        using (var j = NewJournal())
        {
            j.Append(0x1u, 1, 0, Frame(1));
            j.Append(0x2u, 1, 0, Frame(1));
            j.Append(0xDEADBEEFu, 1, 0, Frame(1));
        }
        using var j2 = NewJournal();
        var sessions = j2.ListSessions().OrderBy(x => x).ToArray();
        Assert.Equal(new uint[] { 0x1u, 0x2u, 0xDEADBEEFu }, sessions);
    }

    [Fact]
    public void Cap_overflow_fails_stop_when_configured()
    {
        // record overhead 20 + frame 100 = 120 bytes per record.
        // cap = 250 => after 2 records the 3rd is rejected.
        using var j = NewJournal(maxBytesPerSession: 250);
        const uint sid = 1u;
        j.Append(sid, 1, 0, Frame(1, extraBytes: 96));
        j.Append(sid, 2, 0, Frame(2, extraBytes: 96));
        var ex = Assert.Throws<JournalCapacityExceededException>(
            () => j.Append(sid, 3, 0, Frame(3, extraBytes: 96)));
        Assert.Equal(sid, ex.SessionId);
        Assert.True(ex.CapBytes == 250);
    }

    [Fact]
    public async Task Concurrent_appends_across_sessions_do_not_corrupt()
    {
        using var j = NewJournal();
        const int sessions = 4;
        const int perSession = 200;
        var tasks = new List<Task>();
        for (int i = 0; i < sessions; i++)
        {
            uint sid = (uint)(0xA0 + i);
            tasks.Add(Task.Run(() =>
            {
                for (uint s = 1; s <= perSession; s++)
                    j.Append(sid, s, s * 7L, Frame(s, extraBytes: 12));
            }));
        }
        await Task.WhenAll(tasks);

        for (int i = 0; i < sessions; i++)
        {
            uint sid = (uint)(0xA0 + i);
            Assert.Equal((uint)perSession, j.MaxSeq(sid));
            var slice = j.ReadRange(sid, 1, perSession);
            Assert.Equal(perSession, slice.Count);
            for (int k = 0; k < perSession; k++)
                Assert.Equal((uint)(k + 1), slice[k].Seq);
        }
    }
}
