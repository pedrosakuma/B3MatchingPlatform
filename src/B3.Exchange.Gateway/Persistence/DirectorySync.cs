using System.Runtime.InteropServices;

namespace B3.Exchange.Gateway.Persistence;

/// <summary>
/// Issue #405 — best-effort cross-platform directory fsync. Required
/// for crash-durability: on POSIX filesystems, an <c>fsync()</c> on a
/// file does NOT propagate the directory entry (rename, create, unlink)
/// to disk; only an <c>fsync()</c> on the parent directory does. Without
/// this, a power-loss between a successful <see cref="System.IO.File.Move(string,string,bool)"/>
/// (or new-segment-file create) and the next OS metadata flush can lose
/// the directory entry — even though the file's contents were flushed —
/// meaning a peer can observe an acked SessionVerID on the wire that
/// the next boot does not see.
///
/// Windows: NTFS journals directory metadata as part of regular file
/// flush, so a separate directory handle fsync is unnecessary; this
/// helper is a no-op there.
/// macOS: same POSIX fsync-on-dir semantics as Linux.
/// </summary>
internal static class DirectorySync
{
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    // O_RDONLY = 0 on Linux/macOS (POSIX).
    private const int O_RDONLY = 0;

    /// <summary>
    /// Best-effort fsync of <paramref name="directoryPath"/>. On Windows
    /// returns immediately (NTFS handles directory metadata as part of
    /// file flush). On POSIX, opens the directory read-only and calls
    /// <c>fsync(2)</c>; any failure (path missing, permission, etc.) is
    /// swallowed — callers that care must layer their own check on top.
    /// The helper is intentionally non-throwing so callers can place it
    /// in finally-blocks without disturbing the primary code path.
    /// </summary>
    public static void Fsync(string directoryPath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }
        int fd = -1;
        try
        {
            fd = open(directoryPath, O_RDONLY);
            if (fd < 0)
            {
                return;
            }
            _ = fsync(fd);
        }
        catch
        {
            // P/Invoke surface throws only on DllNotFoundException etc.;
            // treat as best-effort.
        }
        finally
        {
            if (fd >= 0)
            {
                try { _ = close(fd); } catch { }
            }
        }
    }
}
