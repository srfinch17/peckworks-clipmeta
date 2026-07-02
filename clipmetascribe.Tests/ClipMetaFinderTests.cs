using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaFinderTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string PrepareClipWithFields(string fileName, Dictionary<string, string> fieldValues)
    {
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(_tempDir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        foreach (var (atomName, value) in fieldValues)
            mutation.SetFields[atomName] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
        return dest;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Find_MatchingField_ReturnsFilePath()
    {
        string clip = PrepareClipWithFields("clip.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });

        var results = ClipMetaFinder.Find(_tempDir, "game", "Team Fortress 2").ToList();

        CollectionAssert.Contains(results, clip);
    }

    [TestMethod]
    public void Find_NonMatchingValue_ReturnsEmpty()
    {
        PrepareClipWithFields("clip.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });

        var results = ClipMetaFinder.Find(_tempDir, "game", "Counter-Strike").ToList();

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Find_FieldNameCaseInsensitive_ReturnsFile()
    {
        string clip = PrepareClipWithFields("clip.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });

        // Field name searched as uppercase, should still match
        var results = ClipMetaFinder.Find(_tempDir, "GAME", "Team Fortress 2").ToList();

        CollectionAssert.Contains(results, clip);
    }

    [TestMethod]
    public void Find_ValueCaseInsensitive_ReturnsFile()
    {
        string clip = PrepareClipWithFields("clip.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });

        // Value searched lowercase, substring match, case-insensitive
        var results = ClipMetaFinder.Find(_tempDir, "game", "team fortress").ToList();

        CollectionAssert.Contains(results, clip);
    }

    [TestMethod]
    public void Find_PipeField_MatchesSubstringWithinValue()
    {
        // tags is pipe-separated; "headshot" is a substring of "rocket jump|headshot"
        string clip = PrepareClipWithFields("clip.mp4",
            new() { [ClipMetaSchema.AtomName("tags")] = "rocket jump|headshot" });

        var results = ClipMetaFinder.Find(_tempDir, "tags", "headshot").ToList();

        CollectionAssert.Contains(results, clip);
    }

    [TestMethod]
    public void Find_NoMetadataClip_ReturnsEmpty()
    {
        // Copy a pristine clip (no metadata) directly to tempDir
        string source = TestClipsLocator.AllPristine().First();
        File.Copy(source, Path.Combine(_tempDir, "pristine.mp4"));

        var results = ClipMetaFinder.Find(_tempDir, "game", "anything").ToList();

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Find_MultipleClips_ReturnsOnlyMatching()
    {
        string clip1 = PrepareClipWithFields("clip1.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });
        string clip2 = PrepareClipWithFields("clip2.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Counter-Strike" });

        var results = ClipMetaFinder.Find(_tempDir, "game", "Team Fortress 2").ToList();

        CollectionAssert.Contains(results, clip1);
        CollectionAssert.DoesNotContain(results, clip2);
    }

    [TestMethod]
    public void Find_MatchingFile_YieldedOnce()
    {
        // A file that matches the search criteria must appear exactly once in results.
        string clip = PrepareClipWithFields("clip.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });

        var results = ClipMetaFinder.Find(_tempDir, "game", "Team Fortress 2").ToList();

        Assert.AreEqual(1, results.Count(r => r == clip));
    }

    [TestMethod]
    public void Find_MalformedFile_IsSkipped()
    {
        // A zero-byte .mp4 file must be silently skipped rather than propagating a parse error.
        File.WriteAllBytes(Path.Combine(_tempDir, "malformed.mp4"), Array.Empty<byte>());

        var results = ClipMetaFinder.Find(_tempDir, "game", "anything").ToList();

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Find_RecursiveTrue_FindsInSubdirectory()
    {
        string subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);

        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(subDir, "nested.mp4");
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName("game")] = "Team Fortress 2";
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);

        // recursive = true (default) should find it
        var results = ClipMetaFinder.Find(_tempDir, "game", "Team Fortress 2", recursive: true).ToList();

        CollectionAssert.Contains(results, dest);
    }

    // ── Unreadable file mixed in (v1.0.1 hardening, task B1) ────────────────
    //
    // Mp4Parser is deliberately lenient about a truncated box header (see
    // Mp4ParserTruncatedFileTests): it parses to an empty tree rather than throwing, so it
    // never reaches this scanner's catch blocks. What DOES reach them is a file that is locked
    // by another process (e.g. still being written) when the scan tries to open it, IOException
    // from the FileStream open. Before this fix that IOException was silently swallowed with no
    // record of which file was skipped, "one truncated clip must not brick the library" also
    // means the scan must complete AND say what it skipped.

    [TestMethod]
    public void Find_LockedFileMixedIn_StillFindsGoodMatch()
    {
        string clip = PrepareClipWithFields("good.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });
        string locked = MakeLockedFile("locked.mp4");
        using var handle = OpenExclusive(locked);

        var results = ClipMetaFinder.Find(_tempDir, "game", "Team Fortress 2").ToList();

        CollectionAssert.Contains(results, clip);
    }

    [TestMethod]
    public void Find_LockedFileMixedIn_ReportsSkippedPath()
    {
        PrepareClipWithFields("good.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });
        string locked = MakeLockedFile("locked.mp4");
        using var handle = OpenExclusive(locked);

        var skipped = new List<string>();
        ClipMetaFinder.Find(_tempDir, "game", "Team Fortress 2",
            onFileSkipped: (path, _) => skipped.Add(path)).ToList();

        CollectionAssert.Contains(skipped, locked);
    }

    private string MakeLockedFile(string fileName)
    {
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 0 });
        return path;
    }

    /// <summary>Opens <paramref name="path"/> with an exclusive lock so a concurrent scanner's
    /// own open (<see cref="FileShare.Read"/>) fails with an <see cref="IOException"/> sharing
    /// violation, simulating a clip still being written to by another process.</summary>
    private static FileStream OpenExclusive(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    [TestMethod]
    public void Find_RecursiveFalse_DoesNotFindInSubdirectory()
    {
        string subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);

        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(subDir, "nested.mp4");
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName("game")] = "Team Fortress 2";
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);

        // recursive = false should NOT find the subdirectory clip
        var results = ClipMetaFinder.Find(_tempDir, "game", "Team Fortress 2", recursive: false).ToList();

        CollectionAssert.DoesNotContain(results, dest);
    }
}
