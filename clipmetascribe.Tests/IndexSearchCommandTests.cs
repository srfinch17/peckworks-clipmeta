using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class IndexSearchCommandTests
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

    private void PrepareClipAndBuildIndex(string fileName, string field, string value)
    {
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(_tempDir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
        var data = ClipMetaIndex.Build(_tempDir);
        ClipMetaIndex.WriteToFile(data, Path.Combine(_tempDir, ClipMetaIndex.IndexFileName));
    }

    [TestMethod]
    public void Run_MatchFound_PrintsRelativePath()
    {
        PrepareClipAndBuildIndex("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        IndexSearchCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        StringAssert.Contains(writer.ToString(), "clip.mp4");
    }

    [TestMethod]
    public void Run_MatchFound_PrintsMatchCount()
    {
        PrepareClipAndBuildIndex("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        IndexSearchCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        StringAssert.Contains(writer.ToString(), "1 match(es) found.");
    }

    [TestMethod]
    public void Run_NoMatch_PrintsNoMatchesMessage()
    {
        PrepareClipAndBuildIndex("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        IndexSearchCommand.Run(_tempDir, "game", "Counter-Strike", writer);

        StringAssert.Contains(writer.ToString(), "No matches found.");
    }

    [TestMethod]
    public void Run_NoIndexFile_ReturnsOne()
    {
        // No index built — directory exists but .clipmeta-index doesn't
        using var writer = new StringWriter();

        int exitCode = IndexSearchCommand.Run(_tempDir, "game", "TF2", writer);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public void Run_MatchFound_ReturnsZero()
    {
        PrepareClipAndBuildIndex("clip.mp4", "game", "TF2");
        using var writer = new StringWriter();

        int exitCode = IndexSearchCommand.Run(_tempDir, "game", "TF2", writer);

        Assert.AreEqual(0, exitCode);
    }

    // ── Stale-cache warnings ──────────────────────────────────────────────────

    /// <summary>Writes a hand-built single-entry index whose recorded size is
    /// <paramref name="recordedSize"/> (pass the real byte count for a "fresh" entry, a different
    /// number for a "changed" one). No real clip needed — staleness is judged from size/mtime.</summary>
    private void WriteIndexFor(string fileName, byte[] fileBytes, long recordedSize, string field, string value)
    {
        string clip = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(clip, fileBytes);
        var entry = new IndexEntry(clip, recordedSize,
            new DateTimeOffset(new FileInfo(clip).LastWriteTimeUtc, TimeSpan.Zero),
            new[] { (field, value) });
        var data = new IndexData(_tempDir, DateTimeOffset.UtcNow, new[] { entry });
        ClipMetaIndex.WriteToFile(data, Path.Combine(_tempDir, ClipMetaIndex.IndexFileName));
    }

    [TestMethod]
    public void Run_ResultChangedSinceIndex_MarksItAndWarns()
    {
        WriteIndexFor("clip.mp4", new byte[] { 1, 2, 3, 4 }, recordedSize: 999, "game", "TF2");
        using var writer = new StringWriter();

        int code = IndexSearchCommand.Run(_tempDir, "game", "TF2", writer);
        string output = writer.ToString();

        Assert.AreEqual(0, code, "staleness is advisory — exit code stays 0");
        StringAssert.Contains(output, "[changed since index]");
        StringAssert.Contains(output, "Run --index to refresh");
    }

    [TestMethod]
    public void Run_ResultMissingSinceIndex_MarksMissingAndWarns()
    {
        var entry = new IndexEntry(Path.Combine(_tempDir, "gone.mp4"), 10, DateTimeOffset.UtcNow,
            new[] { ("game", "TF2") });
        var data = new IndexData(_tempDir, DateTimeOffset.UtcNow, new[] { entry });
        ClipMetaIndex.WriteToFile(data, Path.Combine(_tempDir, ClipMetaIndex.IndexFileName));
        using var writer = new StringWriter();

        IndexSearchCommand.Run(_tempDir, "game", "TF2", writer);

        StringAssert.Contains(writer.ToString(), "missing");
        StringAssert.Contains(writer.ToString(), "Run --index to refresh");
    }

    [TestMethod]
    public void Run_FreshResult_NoStaleMarkerOrWarning()
    {
        WriteIndexFor("clip.mp4", new byte[] { 1, 2, 3, 4 }, recordedSize: 4, "game", "TF2");
        using var writer = new StringWriter();

        IndexSearchCommand.Run(_tempDir, "game", "TF2", writer);
        string output = writer.ToString();

        StringAssert.Contains(output, "clip.mp4");
        Assert.IsFalse(output.Contains("changed since index"), "fresh result must not be marked stale");
        Assert.IsFalse(output.Contains("Run --index to refresh"), "fresh result must not warn");
    }

    [TestMethod]
    public void Run_DefaultOutput_UsesConsoleOut()
    {
        PrepareClipAndBuildIndex("clip.mp4", "game", "TF2");
        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            int exitCode = IndexSearchCommand.Run(_tempDir, "game", "TF2");

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(writer.ToString(), "Searching");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
