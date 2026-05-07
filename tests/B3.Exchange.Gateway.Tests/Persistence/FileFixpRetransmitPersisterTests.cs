using System.Buffers.Binary;
using B3.Exchange.Gateway.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests.Persistence;

public sealed class FileFixpRetransmitPersisterTests : IDisposable
{
    private readonly string _root;

    public FileFixpRetransmitPersisterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fixp-retx-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private FileFixpRetransmitPersister NewPersister()
        => new(_root, NullLogger<FileFixpRetransmitPersister>.Instance);

    [Fact]
    public void Append_LoadAll_RoundTrips()
    {
        const uint sid = 0xABCD;
        using (var p = NewPersister())
        {
            p.Append(sid, 1, new byte[] { 1, 2, 3 });
            p.Append(sid, 2, new byte[] { 9, 8, 7, 6 });
            p.Append(sid, 3, new byte[] { 0x42 });
        }

        using var p2 = NewPersister();
        var loaded = p2.LoadAll();
        Assert.True(loaded.ContainsKey(sid));
        var entries = loaded[sid];
        Assert.Equal(3, entries.Count);
        Assert.Equal(1u, entries[0].Seq); Assert.Equal(new byte[] { 1, 2, 3 }, entries[0].Frame);
        Assert.Equal(2u, entries[1].Seq); Assert.Equal(new byte[] { 9, 8, 7, 6 }, entries[1].Frame);
        Assert.Equal(3u, entries[2].Seq); Assert.Equal(new byte[] { 0x42 }, entries[2].Frame);
    }

    [Fact]
    public void LoadAll_DropsTornTail()
    {
        const uint sid = 7;
        using (var p = NewPersister())
        {
            p.Append(sid, 10, new byte[] { 0xAA, 0xBB });
            p.Append(sid, 11, new byte[] { 0xCC, 0xDD });
        }
        var path = Path.Combine(_root, FileFixpRetransmitPersister.SessionsSubdir,
            $"session-{sid:x8}.ring");
        var len = new FileInfo(path).Length;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(len - 3);
        }

        using var p2 = NewPersister();
        var entries = p2.LoadAll()[sid];
        Assert.Single(entries);
        Assert.Equal(10u, entries[0].Seq);
    }

    [Fact]
    public void Compact_ReplacesFileAtomically()
    {
        const uint sid = 4242;
        using var p = NewPersister();
        for (uint i = 1; i <= 5; i++) p.Append(sid, i, new byte[] { (byte)i });
        p.Compact(sid, new[]
        {
            new RetransmitRingEntry(3, new byte[] { 3 }),
            new RetransmitRingEntry(4, new byte[] { 4 }),
            new RetransmitRingEntry(5, new byte[] { 5 }),
        });
        p.Append(sid, 6, new byte[] { 6 });
        p.Dispose();

        using var p2 = NewPersister();
        var entries = p2.LoadAll()[sid];
        Assert.Equal(new uint[] { 3, 4, 5, 6 }, entries.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void Remove_IsIdempotent()
    {
        const uint sid = 1;
        using var p = NewPersister();
        p.Append(sid, 1, new byte[] { 1 });
        p.Remove(sid);
        p.Remove(sid);
        Assert.False(p.LoadAll().ContainsKey(sid));
    }

    [Fact]
    public void LoadAll_ReturnsEmpty_WhenDirMissing()
    {
        var alt = Path.Combine(_root, "does-not-exist");
        using var p = new FileFixpRetransmitPersister(alt, NullLogger<FileFixpRetransmitPersister>.Instance);
        Assert.Empty(p.LoadAll());
    }
}
