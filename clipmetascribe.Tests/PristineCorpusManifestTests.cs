using System.Text.RegularExpressions;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Keeps <c>testclips/PRISTINE-MANIFEST.md</c> in lockstep with the actual
/// <c>testclips/pristine/</c> folder, so the documented corpus can't silently drift from what is
/// on disk, a clip added without a manifest row, or a row left behind after a clip is removed,
/// fails here. Graceful-skips on clip-less machines (CI), like the other real-clip tests.
/// </summary>
[TestClass]
public class PristineCorpusManifestTests
{
    [TestMethod]
    public void Manifest_ListsExactlyTheClipsOnDisk()
    {
        if (!TestClipsLocator.PristineClipsPresent())
            Assert.Inconclusive("No pristine clips present, manifest-drift check skipped (CI runs clip-less).");

        string pristineDir = TestClipsLocator.FindPristinePath();
        string manifestPath = Path.Combine(Directory.GetParent(pristineDir)!.FullName, "PRISTINE-MANIFEST.md");
        Assert.IsTrue(File.Exists(manifestPath), $"PRISTINE-MANIFEST.md not found at {manifestPath}");

        var onDisk = Directory.EnumerateFiles(pristineDir, "*.mp4")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var inManifest = ParseManifestClipNames(File.ReadAllLines(manifestPath));

        var missingRows = onDisk.Except(inManifest, StringComparer.OrdinalIgnoreCase).Order().ToList();
        var phantomRows = inManifest.Except(onDisk, StringComparer.OrdinalIgnoreCase).Order().ToList();

        Assert.IsTrue(missingRows.Count == 0 && phantomRows.Count == 0,
            "PRISTINE-MANIFEST.md is out of sync with testclips/pristine/.\n" +
            $"  clips on disk with NO manifest row: {Describe(missingRows)}\n" +
            $"  manifest rows with NO file on disk: {Describe(phantomRows)}");
    }

    /// <summary>A backticked <c>*.mp4</c> filename in the first cell of a markdown table row,
    /// e.g. <c>| `Stargaze.mp4` | 3.6 MB | … |</c>. Filenames may contain spaces (DVR clips), so
    /// the capture is everything up to the closing backtick.</summary>
    private static readonly Regex ClipRow = new(@"^\|\s*`([^`]+\.mp4)`", RegexOptions.Compiled);

    private static HashSet<string> ParseManifestClipNames(IEnumerable<string> lines)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            var m = ClipRow.Match(line);
            if (m.Success) names.Add(m.Groups[1].Value);
        }
        return names;
    }

    private static string Describe(IReadOnlyCollection<string?> names)
        => names.Count == 0 ? "(none)" : string.Join(", ", names);
}
