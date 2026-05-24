using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class VocabCommandTests
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

    private void PrepareClip(string fileName, string field, string value)
    {
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(_tempDir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
    }

    [TestMethod]
    public void Run_MatchFound_PrintsScanHeader()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "game", writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "Scanning");
        StringAssert.Contains(output, "game");
    }

    [TestMethod]
    public void Run_MatchFound_PrintsValues()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "game", writer);

        StringAssert.Contains(writer.ToString(), "Team Fortress 2");
    }

    [TestMethod]
    public void Run_MatchFound_PrintsClipCount()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "game", writer);

        StringAssert.Contains(writer.ToString(), "clip(s)");
    }

    [TestMethod]
    public void Run_MatchFound_PrintsFooter()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "game", writer);

        StringAssert.Contains(writer.ToString(), "distinct value(s)");
    }

    [TestMethod]
    public void Run_NoMatch_PrintsNoFieldMessage()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "notes", writer);

        StringAssert.Contains(writer.ToString(), "no clips have field 'notes'");
    }

    [TestMethod]
    public void Run_PipeField_ShowsSplitItems()
    {
        PrepareClip("clip.mp4", "tags", "headshot|rocket jump");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "tags", writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "headshot");
        StringAssert.Contains(output, "rocket jump");
    }

    [TestMethod]
    public void Run_WithMatches_ReturnsZero()
    {
        PrepareClip("clip.mp4", "game", "TF2");
        using var writer = new StringWriter();

        int exitCode = VocabCommand.Run(_tempDir, "game", writer);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void Run_EmptyDirectory_ReturnsZero()
    {
        using var writer = new StringWriter();

        int exitCode = VocabCommand.Run(_tempDir, "game", writer);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void Run_DefaultOutput_UsesConsoleOut()
    {
        PrepareClip("clip.mp4", "game", "TF2");
        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            int exitCode = VocabCommand.Run(_tempDir, "game");

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(writer.ToString(), "Scanning");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
