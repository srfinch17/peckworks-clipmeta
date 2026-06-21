using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClipMetaScribe.Tests.Helpers;

/// <summary>
/// Locates the solution-level testclips directories for the scribe test project.
///
/// Graceful-skip for clip-less machines (e.g. CI — clips are git-ignored): when no pristine
/// clips are present, the clip accessors call <see cref="Assert.Inconclusive"/> so the calling
/// test reports as <b>skipped</b>, not failed. The skip lives here so it covers every caller
/// automatically — a test that never touches a clip (the synthetic <c>MinimalMp4Builder</c>
/// fixtures) still runs. DynamicData sources are the one exception: they're enumerated outside a
/// test body where Inconclusive can't be caught, so they use <see cref="PristineClipRows"/>
/// (which emits a skip-sentinel row) plus <see cref="SkipIfMissing"/> at the top of the method.
/// </summary>
internal static class TestClipsLocator
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

    public static string FindPristinePath() => TryFind("pristine") ?? SkipNoClips();

    public static string FindScratchPath() => TryFind("scratch") ?? SkipNoClips();

    /// <summary>
    /// The smallest pristine clip by file size — fast to copy, and orders of magnitude less I/O
    /// than whatever the filesystem happens to enumerate first. Skips the calling test when no
    /// clips are present.
    /// </summary>
    public static string SmallestPristine()
        => AllPristine().OrderBy(path => new FileInfo(path).Length).First();

    /// <summary>
    /// All pristine .mp4 clips, name-sorted for determinism (filesystem enumeration order is not
    /// guaranteed stable, so the many <c>AllPristine().First()</c> callers must not depend on it).
    /// SKIPS the calling test when no clips are present.
    /// </summary>
    public static IEnumerable<string> AllPristine()
    {
        if (!PristineClipsPresent())
            SkipNoClips();
        return Directory.EnumerateFiles(FindPristinePath(), "*.mp4")
                        .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Raw, never-skipping pristine enumeration (name-sorted) for use by DynamicData sources,
    /// which run during data expansion where <see cref="Assert.Inconclusive"/> can't be caught.
    /// Returns empty when no clips are present.
    /// </summary>
    public static IReadOnlyList<string> EnumeratePristineRaw()
    {
        string? dir = TryFind("pristine");
        return dir == null
            ? Array.Empty<string>()
            : Directory.EnumerateFiles(dir, "*.mp4")
                       .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                       .ToList();
    }

    /// <summary>
    /// DynamicData row source: one row per pristine clip, or a single skip-sentinel row
    /// (<c>{ null }</c>) when none exist. The consuming <c>[DataTestMethod]</c> must call
    /// <see cref="SkipIfMissing"/> with its path argument as its first line.
    /// </summary>
    public static IEnumerable<object[]> PristineClipRows()
    {
        var clips = EnumeratePristineRaw();
        if (clips.Count == 0)
        {
            yield return new object[] { null! }; // sentinel → SkipIfMissing turns this into a skip
            yield break;
        }
        foreach (string clip in clips)
            yield return new object[] { clip };
    }

    /// <summary>Skips the test when a DynamicData clip path is the no-clips sentinel.</summary>
    public static void SkipIfMissing(string? pristinePath)
    {
        if (string.IsNullOrEmpty(pristinePath))
            SkipNoClips();
    }

    private static string SkipNoClips()
    {
        Assert.Inconclusive(
            "No test clips found in testclips/pristine — integration test skipped. These run " +
            "locally where real .mp4 clips exist; CI runs clip-less by design (graceful skip).");
        return null!; // unreachable: Assert.Inconclusive always throws
    }
}
