namespace ClipMetaView.Tests;

/// <summary>Helpers for locating the solution-level testclips directory.</summary>
internal static class TestClips
{
    /// <summary>
    /// Returns all .mp4 files in testclips/pristine/ at the solution root.
    /// </summary>
    public static IEnumerable<string> All()
    {
        string pristinePath = FindPristinePath();
        return Directory.EnumerateFiles(pristinePath, "*.mp4");
    }

    /// <summary>
    /// Walks up from the test assembly's bin folder to find testclips/pristine/.
    /// </summary>
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
        throw new DirectoryNotFoundException(
            "testclips/pristine folder not found. Walk up from: " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Returns the solution-root testclips/scratch/ path, creating it if absent.
    /// </summary>
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
        throw new DirectoryNotFoundException(
            "testclips/scratch folder not found. Walk up from: " + AppContext.BaseDirectory);
    }
}
