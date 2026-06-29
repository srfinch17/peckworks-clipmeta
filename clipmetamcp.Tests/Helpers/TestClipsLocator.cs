using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClipMetaMcp.Tests.Helpers;

/// <summary>
/// Locates the solution-level testclips directories (same walk-up pattern as the other test
/// projects: testclips/ is git-ignored and lives at the repo root).
///
/// Graceful-skip for clip-less machines (e.g. CI): when no pristine clips are present the clip
/// accessors call <see cref="Assert.Inconclusive"/>, so any test that needs a clip reports as
/// <b>skipped</b> rather than failed. Tests that never touch a clip still run normally.
/// </summary>
internal static class TestClipsLocator
{
    private static string? TryFindPristine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "testclips", "pristine");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>True when at least one pristine .mp4 is available locally (false on CI).</summary>
    public static bool PristineClipsPresent()
    {
        string? dir = TryFindPristine();
        return dir != null && Directory.EnumerateFiles(dir, "*.mp4").Any();
    }

    public static string FindPristinePath() => TryFindPristine() ?? SkipNoClips();

    /// <summary>
    /// All pristine .mp4 clips, name-sorted for determinism. SKIPS the calling test when no clips
    /// are present. Safe to call from a test body or a <c>[TestInitialize]</c> (both catch the
    /// resulting Inconclusive); do NOT call from <c>[ClassInitialize]</c>, where it would fail the
    /// whole class instead of skipping, guard those with <see cref="PristineClipsPresent"/>.
    /// </summary>
    public static IEnumerable<string> AllPristine()
    {
        if (!PristineClipsPresent())
            SkipNoClips();
        return Directory.EnumerateFiles(FindPristinePath(), "*.mp4")
                        .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The smallest pristine clip, deterministic, and orders of magnitude less I/O than
    /// whatever the filesystem happens to enumerate first (pristine clips range ~70–400 MB and
    /// every PrepareClip is a full copy + write-engine rewrite of the chosen file).
    /// Skips the calling test when no clips are present.
    /// </summary>
    public static string SmallestPristine()
        => AllPristine().OrderBy(path => new FileInfo(path).Length).First();

    private static string SkipNoClips()
    {
        Assert.Inconclusive(
            "No test clips found in testclips/pristine, test skipped. These run locally where " +
            "real .mp4 clips exist; CI runs clip-less by design (graceful skip).");
        return null!; // unreachable: Assert.Inconclusive always throws
    }
}
