using B3.Exchange.Gateway;
using B3.Exchange.Gateway.Persistence;

namespace B3.Exchange.Gateway.Tests;

/// <summary>
/// Issue #289: RetransmitBuffer + persistence callback wiring.
/// </summary>
public sealed class RetransmitBufferPersistenceTests
{
    private static byte[] Frame(uint seq) => new byte[] { (byte)seq, (byte)(seq >> 8), 0xCA, 0xFE };

    [Fact]
    public void Append_InvokesOnAppendPersist_ForEveryFrame()
    {
        var seen = new List<(uint Seq, byte[] Frame)>();
        var buf = new RetransmitBuffer(capacity: 4,
            metrics: null,
            isSuspended: null,
            onAppendPersist: (s, f) => seen.Add((s, f)),
            onCompactPersist: null);

        buf.Append(1, Frame(1));
        buf.Append(2, Frame(2));
        buf.Append(3, Frame(3));

        Assert.Equal(3, seen.Count);
        Assert.Equal(new uint[] { 1, 2, 3 }, seen.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void Append_TriggersCompactCallback_EveryTwoCapacityAppends()
    {
        int compactCalls = 0;
        IReadOnlyList<RetransmitRingEntry>? lastSnapshot = null;
        var buf = new RetransmitBuffer(capacity: 2,
            metrics: null,
            isSuspended: null,
            onAppendPersist: (_, _) => { },
            onCompactPersist: snap => { compactCalls++; lastSnapshot = snap; });

        // capacity=2 ⇒ compaction every 4 appends
        for (uint i = 1; i <= 4; i++) buf.Append(i, Frame(i));
        Assert.Equal(1, compactCalls);
        Assert.NotNull(lastSnapshot);
        // Live ring contents post-eviction = [3, 4]
        Assert.Equal(new uint[] { 3, 4 }, lastSnapshot!.Select(e => e.Seq).ToArray());

        for (uint i = 5; i <= 8; i++) buf.Append(i, Frame(i));
        Assert.Equal(2, compactCalls);
        Assert.Equal(new uint[] { 7, 8 }, lastSnapshot!.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void Rehydrate_RestoresRingForReplay()
    {
        var buf = new RetransmitBuffer(4);
        buf.RehydrateFromPersistedSnapshot(new[]
        {
            new RetransmitRingEntry(10, Frame(10)),
            new RetransmitRingEntry(11, Frame(11)),
            new RetransmitRingEntry(12, Frame(12)),
        });

        var snap = buf.TryGet(fromSeq: 11, requestedCount: 2);
        Assert.Null(snap.RejectCode);
        Assert.Equal(2u, snap.ActualCount);
        Assert.Equal(11u, snap.FirstSeq);
    }

    [Fact]
    public void Rehydrate_CapsToCapacity_KeepingMostRecentEntries()
    {
        var buf = new RetransmitBuffer(2);
        buf.RehydrateFromPersistedSnapshot(new[]
        {
            new RetransmitRingEntry(1, Frame(1)),
            new RetransmitRingEntry(2, Frame(2)),
            new RetransmitRingEntry(3, Frame(3)),
            new RetransmitRingEntry(4, Frame(4)),
        });

        Assert.NotNull(buf.TryGet(fromSeq: 1, requestedCount: 1).RejectCode);
        var snap = buf.TryGet(fromSeq: 3, requestedCount: 2);
        Assert.Null(snap.RejectCode);
        Assert.Equal(3u, snap.FirstSeq);
        Assert.Equal(2u, snap.ActualCount);
    }

    [Fact]
    public void Rehydrate_OnNonEmptyRing_Throws()
    {
        var buf = new RetransmitBuffer(4);
        buf.Append(1, Frame(1));
        Assert.Throws<InvalidOperationException>(() =>
            buf.RehydrateFromPersistedSnapshot(new[] { new RetransmitRingEntry(2, Frame(2)) }));
    }
}
