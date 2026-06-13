namespace ClipMetaScribe.Tests.Helpers;

/// <summary>Locates the solution-level testclips directories for the scribe test project.</summary>
internal static class TestClipsLocator
{
    public static string FindPristinePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "testclips", "pristine");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("testclips/pristine not found from " + AppContext.BaseDirectory);
    }

    public static string FindScratchPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "testclips", "scratch");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("testclips/scratch not found from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// All pristine .mp4 clips, ordered deterministically by file name. Sorting matters:
    /// <c>Directory.EnumerateFiles</c> order is filesystem-defined and NOT guaranteed stable
    /// between runs, so the many <c>AllPristine().First()</c> callers would otherwise pick a
    /// different clip run-to-run — an order-dependent flake. A fixed order makes "the first
    /// clip" reproducible.
    /// </summary>
    public static IEnumerable<string> AllPristine()
        => Directory.EnumerateFiles(FindPristinePath(), "*.mp4")
                    .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
}
