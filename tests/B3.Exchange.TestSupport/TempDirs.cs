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
    /// with the given prefix (e.g. "b3-eod-audit-"); returns its absolute path.
    /// Caller is responsible for cleanup via <see cref="TryDelete"/>.
    /// </summary>
    public static string Create(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
