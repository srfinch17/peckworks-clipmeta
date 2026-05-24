using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaVocabTests
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

    private void PrepareClip(string fileName, string field, string value, string? subDir = null)
    {
        string dir = subDir != null ? Path.Combine(_tempDir, subDir) : _tempDir;
        Directory.CreateDirectory(dir);
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(dir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
    }

    [TestMethod]
    public void Enumerate_SingleValue_ReturnsCountAndClipsWithField()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game");

        Assert.AreEqual(1, result.Counts.Count);
        Assert.AreEqual(1, result.Counts["Team Fortress 2"]);
        Assert.AreEqual(1, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_MultipleClipsSameValue_AccumulatesCount()
    {
        PrepareClip("clip1.mp4", "game", "Team Fortress 2");
        PrepareClip("clip2.mp4", "game", "Team Fortress 2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game");

        Assert.AreEqual(1, result.Counts.Count);
        Assert.AreEqual(2, result.Counts["Team Fortress 2"]);
        Assert.AreEqual(2, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_MultipleDistinctValues_ReturnsAll()
    {
        PrepareClip("clip1.mp4", "game", "Team Fortress 2");
        PrepareClip("clip2.mp4", "game", "Counter-Strike 2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game");

        Assert.AreEqual(2, result.Counts.Count);
        Assert.IsTrue(result.Counts.ContainsKey("Team Fortress 2"));
        Assert.IsTrue(result.Counts.ContainsKey("Counter-Strike 2"));
        Assert.AreEqual(2, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_PipeField_SplitsItems()
    {
        PrepareClip("clip.mp4", "tags", "headshot|rocket jump");

        var result = ClipMetaVocab.Enumerate(_tempDir, "tags");

        Assert.AreEqual(2, result.Counts.Count);
        Assert.AreEqual(1, result.Counts["headshot"]);
        Assert.AreEqual(1, result.Counts["rocket jump"]);
        Assert.AreEqual(1, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_PipeField_CombinesCountsAcrossClips()
    {
        PrepareClip("clip1.mp4", "tags", "headshot|rocket jump");
        PrepareClip("clip2.mp4", "tags", "headshot");

        var result = ClipMetaVocab.Enumerate(_tempDir, "tags");

        Assert.AreEqual(2, result.Counts.Count);
        Assert.AreEqual(2, result.Counts["headshot"]);
        Assert.AreEqual(1, result.Counts["rocket jump"]);
        Assert.AreEqual(2, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_FieldNotPresent_ReturnsEmpty()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "notes");

        Assert.AreEqual(0, result.Counts.Count);
        Assert.AreEqual(0, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_CaseInsensitiveValue_Deduplicates()
    {
        PrepareClip("clip1.mp4", "game", "TF2");
        PrepareClip("clip2.mp4", "game", "tf2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game");

        Assert.AreEqual(1, result.Counts.Count);
        Assert.AreEqual(2, result.Counts.Values.Single());
    }

    [TestMethod]
    public void Enumerate_CaseInsensitiveFieldName_Matches()
    {
        PrepareClip("clip.mp4", "game", "TF2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "GAME");

        Assert.AreEqual(1, result.Counts.Count);
        Assert.AreEqual(1, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_EmptyDirectory_ReturnsEmpty()
    {
        var result = ClipMetaVocab.Enumerate(_tempDir, "game");

        Assert.AreEqual(0, result.Counts.Count);
        Assert.AreEqual(0, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_MalformedFile_IsSkipped()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "corrupt.mp4"), Array.Empty<byte>());

        var result = ClipMetaVocab.Enumerate(_tempDir, "game");

        Assert.AreEqual(0, result.Counts.Count);
        Assert.AreEqual(0, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_Recursive_FindsClipsInSubdirectory()
    {
        PrepareClip("clip.mp4", "game", "TF2", subDir: "sub");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game", recursive: true);

        Assert.AreEqual(1, result.Counts.Count);
    }

    [TestMethod]
    public void Enumerate_NonRecursive_IgnoresSubdirectory()
    {
        PrepareClip("clip.mp4", "game", "TF2", subDir: "sub");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game", recursive: false);

        Assert.AreEqual(0, result.Counts.Count);
    }
}
