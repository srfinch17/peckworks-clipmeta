using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClipMetaView.Tests;

/// <summary>
/// Helpers for locating the solution-level testclips directory.
///
/// Graceful-skip for clip-less machines (e.g. CI — clips are git-ignored): when no pristine
/// clips are present, the clip accessors call <see cref="Assert.Inconclusive"/> so the calling
/// test reports as <b>skipped</b>, not failed. DynamicData sources can't catch Inconclusive
/// (they run during data expansion), so they use <see cref="ClipRows"/> + <see cref="SkipIfMissing"/>.
/// </summary>
internal static class TestClips
{
    private static string? TryFind(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "testclips", name);
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>True when at least one pristine .mp4 is available locally (false on CI).</summary>
    public static bool PristineClipsPresent()
    {
        string? dir = TryFind("pristine");
        return dir != null && Directory.EnumerateFiles(dir, "*.mp4").Any();
    }

    /// <summary>All pristine .mp4 files. SKIPS the calling test when none are present.</summary>
    public static IEnumerable<string> All()
    {
        if (!PristineClipsPresent())
            SkipNoClips();
        return Directory.EnumerateFiles(FindPristinePath(), "*.mp4");
    }

    public static string FindPristinePath() => TryFind("pristine") ?? SkipNoClips();

    public static string FindScratchPath() => TryFind("scratch") ?? SkipNoClips();

    /// <summary>
    /// Raw, never-skipping enumeration for DynamicData sources (empty when no clips present).
    /// </summary>
    public static IReadOnlyList<string> AllRaw()
    {
        string? dir = TryFind("pristine");
        return dir == null ? Array.Empty<string>() : Directory.EnumerateFiles(dir, "*.mp4").ToList();
    }

    /// <summary>
    /// DynamicData row source: one row per clip, or a single skip-sentinel row when none exist.
    /// The consuming <c>[DataTestMethod]</c> must call <see cref="SkipIfMissing"/> first.
    /// </summary>
    public static IEnumerable<object[]> ClipRows()
    {
        var clips = AllRaw();
        if (clips.Count == 0)
        {
            yield return new object[] { null! }; // sentinel → SkipIfMissing turns this into a skip
            yield break;
        }
        foreach (string clip in clips)
            yield return new object[] { clip };
    }

    /// <summary>Skips the test when a DynamicData clip path is the no-clips sentinel.</summary>
    public static void SkipIfMissing(string? clipPath)
    {
        if (string.IsNullOrEmpty(clipPath))
            SkipNoClips();
    }

    private static string SkipNoClips()
    {
        Assert.Inconclusive(
            "No test clips found in testclips/pristine — test skipped. These run locally where " +
            "real .mp4 clips exist; CI runs clip-less by design (graceful skip).");
        return null!; // unreachable: Assert.Inconclusive always throws
    }
}
