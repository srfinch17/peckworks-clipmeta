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
