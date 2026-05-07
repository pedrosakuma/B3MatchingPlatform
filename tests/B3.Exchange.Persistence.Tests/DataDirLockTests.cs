using B3.Exchange.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace B3.Exchange.Persistence.Tests;

public sealed class DataDirLockTests : IDisposable
{
    private readonly string _root;

    public DataDirLockTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "datadir-lock-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Acquire_CreatesLockFile()
    {
        using var lk = DataDirLock.Acquire(_root, NullLogger<DataDirLock>.Instance);
        var path = Path.Combine(_root, DataDirLock.LockFileName);
        Assert.True(File.Exists(path));
        Assert.Equal(path, lk.Path);
    }

    [Fact]
    public void Dispose_RemovesLockFile()
    {
        var lk = DataDirLock.Acquire(_root, NullLogger<DataDirLock>.Instance);
        var path = Path.Combine(_root, DataDirLock.LockFileName);
        Assert.True(File.Exists(path));
        lk.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Acquire_SecondInstance_Throws()
    {
        using var first = DataDirLock.Acquire(_root, NullLogger<DataDirLock>.Instance);
        var ex = Assert.Throws<DataDirLockedException>(() =>
            DataDirLock.Acquire(_root, NullLogger<DataDirLock>.Instance));
        Assert.Equal(Path.GetFullPath(_root), ex.DataDir);
        Assert.Contains("locked by another process", ex.Message);
    }

    [Fact]
    public void Acquire_AfterDispose_Succeeds()
    {
        var first = DataDirLock.Acquire(_root, NullLogger<DataDirLock>.Instance);
        first.Dispose();
        // Second acquisition must succeed and not throw.
        using var second = DataDirLock.Acquire(_root, NullLogger<DataDirLock>.Instance);
        Assert.True(File.Exists(Path.Combine(_root, DataDirLock.LockFileName)));
    }

    [Fact]
    public void Acquire_CreatesMissingDirectory()
    {
        var nested = Path.Combine(_root, "nested", "sub");
        using var lk = DataDirLock.Acquire(nested, NullLogger<DataDirLock>.Instance);
        Assert.True(Directory.Exists(nested));
        Assert.True(File.Exists(Path.Combine(nested, DataDirLock.LockFileName)));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var lk = DataDirLock.Acquire(_root, NullLogger<DataDirLock>.Instance);
        lk.Dispose();
        lk.Dispose(); // no-throw
    }
}
