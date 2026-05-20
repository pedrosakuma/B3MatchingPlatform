namespace B3.Exchange.TestSupport;

public static class TempDirs
{
    /// <summary>
    /// Best-effort recursive delete; swallows exceptions (intended for
    /// test cleanup in finally blocks).
    /// </summary>
    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Create a fresh unique temp directory under <see cref="Path.GetTempPath"/>
    /// with the given file-name-style prefix (e.g. "b3-eod-audit-"); returns
    /// its absolute path. Caller is responsible for cleanup via
    /// <see cref="TryDelete"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="prefix"/> is null, rooted, or contains a directory
    /// separator / parent-traversal segment — any of which would let the
    /// returned path escape <see cref="Path.GetTempPath"/> and silently
    /// violate the helper's contract.
    /// </exception>
    public static string Create(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (Path.IsPathRooted(prefix)
            || prefix.Contains(Path.DirectorySeparatorChar)
            || prefix.Contains(Path.AltDirectorySeparatorChar)
            || prefix.Contains(".."))
        {
            throw new ArgumentException(
                "prefix must be a file-name-style fragment (no path separators, no '..', not rooted)",
                nameof(prefix));
        }
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
