using B3.Exchange.Gateway.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Gateway.Tests.Persistence;

/// <summary>
/// Issue #405: FIXP envelope-state snapshot persister.
/// </summary>
public sealed class FileFixpSessionStatePersisterTests : IDisposable
{
    private readonly string _root;

    public FileFixpSessionStatePersisterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fixp-state-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private FileFixpSessionStatePersister NewPersister()
        => new(_root, NullLogger<FileFixpSessionStatePersister>.Instance);

    private static FixpSessionStateSnapshot Snap(uint sid, ulong ver = 1,
        uint outSeq = 0, uint inSeq = 0, uint firm = 99, long ts = 1_700_000_000_000L)
        => new(sid, ver, outSeq, inSeq, firm, ts);

    [Fact]
    public void Save_then_Load_round_trips_exactly()
    {
        using var p = NewPersister();
        var s = Snap(0xDEADBEEFu, ver: 17, outSeq: 12345, inSeq: 6789, firm: 42, ts: 1_000_000_000L);
        p.Save(in s);
        var loaded = p.Load(s.SessionId);
        Assert.Equal(s, loaded);
    }

    [Fact]
    public void Save_overwrites_previous_snapshot()
    {
        using var p = NewPersister();
        var s1 = Snap(1u, ver: 1, outSeq: 10);
        var s2 = Snap(1u, ver: 2, outSeq: 99);
        p.Save(in s1);
        p.Save(in s2);
        Assert.Equal(s2, p.Load(1u));
    }

    [Fact]
    public void Load_unknown_session_returns_null()
    {
        using var p = NewPersister();
        Assert.Null(p.Load(0x1234u));
    }

    [Fact]
    public void LoadAll_returns_every_persisted_snapshot()
    {
        using (var p = NewPersister())
        {
            for (uint sid = 1; sid <= 5; sid++)
            {
                var s = Snap(sid, ver: sid * 10);
                p.Save(in s);
            }
        }
        using var p2 = NewPersister();
        var all = p2.LoadAll().OrderBy(s => s.SessionId).ToArray();
        Assert.Equal(5, all.Length);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, all.Select(s => s.SessionId));
        Assert.Equal(new ulong[] { 10, 20, 30, 40, 50 }, all.Select(s => s.SessionVerId));
    }

    [Fact]
    public void Remove_deletes_snapshot_and_is_idempotent()
    {
        using var p = NewPersister();
        var s = Snap(7u);
        p.Save(in s);
        Assert.NotNull(p.Load(7u));
        p.Remove(7u);
        Assert.Null(p.Load(7u));
        p.Remove(7u); // idempotent
    }

    [Fact]
    public void Crc_corruption_causes_load_to_skip_silently()
    {
        const uint sid = 0x42u;
        using (var p = NewPersister())
        {
            var s = Snap(sid);
            p.Save(in s);
        }
        var path = Path.Combine(_root, FileFixpSessionStatePersister.StateSubdir,
            $"session-{sid:x8}.state");
        var bytes = File.ReadAllBytes(path);
        // Flip a payload byte to corrupt the CRC.
        bytes[10] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        using var p2 = NewPersister();
        Assert.Null(p2.Load(sid));
        Assert.Empty(p2.LoadAll());
    }

    [Fact]
    public void Stale_tmp_files_are_reaped_on_open()
    {
        Directory.CreateDirectory(Path.Combine(_root, FileFixpSessionStatePersister.StateSubdir));
        var staleTmp = Path.Combine(_root, FileFixpSessionStatePersister.StateSubdir,
            "session-deadbeef.state.tmp");
        File.WriteAllBytes(staleTmp, new byte[] { 1, 2, 3 });

        using (NewPersister()) { }

        Assert.False(File.Exists(staleTmp));
    }

    [Fact]
    public void Wrong_magic_or_length_is_ignored()
    {
        Directory.CreateDirectory(Path.Combine(_root, FileFixpSessionStatePersister.StateSubdir));
        var path = Path.Combine(_root, FileFixpSessionStatePersister.StateSubdir,
            "session-00000001.state");
        // Too short.
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFF });
        using (var p = NewPersister())
            Assert.Null(p.Load(1u));

        // Right length, wrong magic.
        File.WriteAllBytes(path, new byte[44]);
        using (var p = NewPersister())
            Assert.Null(p.Load(1u));
    }
}
