namespace ClipMetaMcp.Tests.Helpers;

/// <summary>
/// Locates the solution-level testclips directories (same walk-up pattern as the other test
/// projects: testclips/ is git-ignored and lives at the repo root).
/// </summary>
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

    /// <summary>
    /// All pristine .mp4 clips, ordered deterministically by file name. Sorting matters:
    /// <c>Directory.EnumerateFiles</c> order is filesystem-defined and NOT guaranteed stable
    /// between runs, so tests doing <c>AllPristine().First()</c> would otherwise pick a
    /// different clip run-to-run — an order-dependent flake. A fixed order makes "the first
    /// clip" reproducible.
    /// </summary>
    public static IEnumerable<string> AllPristine()
        => Directory.EnumerateFiles(FindPristinePath(), "*.mp4")
                    .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The smallest pristine clip — deterministic, and orders of magnitude less I/O than
    /// whatever the filesystem happens to enumerate first (pristine clips range ~70–400 MB and
    /// every PrepareClip is a full copy + write-engine rewrite of the chosen file).
    /// </summary>
    public static string SmallestPristine()
        => AllPristine().OrderBy(path => new FileInfo(path).Length).First();
}
