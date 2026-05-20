namespace B3.Exchange.TestSupport;

public static class TestPaths
{
    /// <summary>
    /// Locate a repo-relative file (e.g. "config/instruments-eqt.json") by
    /// walking up from <see cref="AppContext.BaseDirectory"/> up to 8 levels.
    /// Throws <see cref="FileNotFoundException"/> when not found.
    /// </summary>
    public static string ResolveRepoFile(string relPath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, relPath);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"could not locate {relPath} from {AppContext.BaseDirectory}");
    }
}
