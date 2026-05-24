namespace ClipMetaScribe.Tests.Helpers;

/// <summary>
/// Manages scratch copies of pristine test clips for write tests.
/// Each write test must work on a scratch copy so pristine clips are never modified.
/// </summary>
internal static class ScratchClips
{
    /// <summary>
    /// Copies a pristine clip to testclips/scratch/ with a unique name and returns the scratch path.
    /// Uses a random suffix so concurrent tests do not collide on the same scratch file.
    /// </summary>
    public static string Prepare(string pristineFilePath)
    {
        string scratchDir = TestClipsLocator.FindScratchPath();
        string baseName = Path.GetFileNameWithoutExtension(pristineFilePath);
        string ext = Path.GetExtension(pristineFilePath);
        string unique = $"{baseName}_{Guid.NewGuid():N}{ext}";
        string scratchPath = Path.Combine(scratchDir, unique);
        File.Copy(pristineFilePath, scratchPath, overwrite: false);
        return scratchPath;
    }

    /// <summary>Returns scratch paths for all pristine clips (copies all).</summary>
    public static IEnumerable<string> PrepareAll()
        => TestClipsLocator.AllPristine().Select(Prepare);

    /// <summary>Returns all .mp4 files currently in the scratch directory.</summary>
    public static IEnumerable<string> AllScratch()
        => Directory.EnumerateFiles(TestClipsLocator.FindScratchPath(), "*.mp4");
}
