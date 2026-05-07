using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace B3.Exchange.Persistence;

/// <summary>
/// Issue #290: exclusive single-writer fence for a persistence
/// <c>dataDir</c>. Two host processes pointing at the same directory
/// would silently corrupt each other's WAL + snapshot files
/// (interleaved appends, racing tmp+rename, lost truncations).
///
/// <para>The lock is implemented as a held-open file
/// (<c>{dataDir}/.lock</c>) opened with <see cref="FileShare.None"/>.
/// The OS releases the file handle automatically on process exit, so
/// even an ungraceful shutdown (kill -9, segfault, OOM) does not
/// strand the lock. On graceful shutdown <see cref="Dispose"/>
/// closes the handle and removes the file.</para>
///
/// <para>The first line of the file is the holder's PID + ISO-8601
/// UTC start timestamp + machine name, written for diagnostic
/// purposes — when a second instance fails to acquire, it tries to
/// read the line and surfaces it in the thrown
/// <see cref="DataDirLockedException"/> so the operator can identify
/// the offending process.</para>
/// </summary>
public sealed class DataDirLock : IDisposable
{
    /// <summary>Lock file name within the data directory.</summary>
    public const string LockFileName = ".lock";

    private readonly FileStream _stream;
    private readonly string _path;
    private readonly ILogger<DataDirLock> _logger;
    private int _disposed;

    /// <summary>Absolute path to the held lock file.</summary>
    public string Path => _path;

    private DataDirLock(FileStream stream, string path, ILogger<DataDirLock> logger)
    {
        _stream = stream;
        _path = path;
        _logger = logger;
    }

    /// <summary>
    /// Acquires the exclusive lock on <paramref name="dataDir"/>.
    /// Creates the directory if it does not exist. Throws
    /// <see cref="DataDirLockedException"/> if another process
    /// already holds the lock.
    /// </summary>
    public static DataDirLock Acquire(string dataDir, ILogger<DataDirLock> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        ArgumentNullException.ThrowIfNull(logger);
        Directory.CreateDirectory(dataDir);
        var path = System.IO.Path.Combine(System.IO.Path.GetFullPath(dataDir), LockFileName);

        FileStream stream;
        try
        {
            // FileShare.None on both POSIX and Windows: a second
            // process opening the same path with FileShare.None will
            // fail with IOException (Windows) or, on Linux, .NET
            // emulates exclusive open via flock — either way, the
            // second open throws.
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 4096, FileOptions.None);
        }
        catch (IOException ex)
        {
            // Best-effort read of the existing holder identity.
            string? holder = TryReadHolder(path);
            logger.LogError(ex,
                "datadir {DataDir} lock acquisition failed; holder={Holder}",
                dataDir, holder ?? "<unknown>");
            throw new DataDirLockedException(dataDir, holder, ex);
        }

        try
        {
            stream.SetLength(0);
            int pid = Environment.ProcessId;
            string line = string.Create(CultureInfo.InvariantCulture,
                $"pid={pid} startedUtc={DateTime.UtcNow:O} host={Environment.MachineName}\n");
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            try { stream.Dispose(); } catch { }
            throw;
        }

        logger.LogInformation("datadir {DataDir} lock acquired (pid={Pid})",
            dataDir, Environment.ProcessId);
        return new DataDirLock(stream, path, logger);
    }

    private static string? TryReadHolder(string path)
    {
        try
        {
            // Re-open with FileShare.ReadWrite so we can read while
            // the holder still has it open exclusively. On most
            // platforms FileShare.None on the holder will block this,
            // in which case we just return null.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadLine();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stream.Dispose(); } catch { }
        // Best-effort delete; if it fails the next process will just
        // overwrite the contents on acquire.
        try { File.Delete(_path); } catch { }
        _logger.LogInformation("datadir lock released ({Path})", _path);
    }
}

/// <summary>
/// Thrown by <see cref="DataDirLock.Acquire"/> when the directory
/// is already locked by another process. The
/// <see cref="HolderInfo"/> property carries the
/// <c>pid=… startedUtc=… host=…</c> line written by the holder, or
/// <c>null</c> if it could not be read.
/// </summary>
public sealed class DataDirLockedException : InvalidOperationException
{
    public string DataDir { get; }
    public string? HolderInfo { get; }

    public DataDirLockedException(string dataDir, string? holderInfo, Exception innerException)
        : base(BuildMessage(dataDir, holderInfo), innerException)
    {
        DataDir = dataDir;
        HolderInfo = holderInfo;
    }

    private static string BuildMessage(string dataDir, string? holderInfo)
        => holderInfo is null
            ? $"data directory '{dataDir}' is already locked by another process (could not read holder)"
            : $"data directory '{dataDir}' is already locked by another process ({holderInfo})";
}
