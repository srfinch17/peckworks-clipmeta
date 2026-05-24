using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class FindCommandTests
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

    private void PrepareClip(string fileName, string field, string value)
    {
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(_tempDir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Run_MatchFound_PrintsSearchHeader()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        FindCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "Searching");
        StringAssert.Contains(output, "game");
        StringAssert.Contains(output, "Team Fortress 2");
    }

    [TestMethod]
    public void Run_MatchFound_PrintsRelativePath()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        FindCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        // Should show "clip.mp4" (relative path from tempDir), not the full absolute path
        StringAssert.Contains(writer.ToString(), "clip.mp4");
    }

    [TestMethod]
    public void Run_NoMatch_PrintsNoMatchesMessage()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        FindCommand.Run(_tempDir, "game", "Counter-Strike", writer);

        StringAssert.Contains(writer.ToString(), "No matches found.");
    }

    [TestMethod]
    public void Run_OneMatch_PrintsOneMatchCount()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        FindCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        StringAssert.Contains(writer.ToString(), "1 match(es) found.");
    }

    [TestMethod]
    public void Run_MultipleMatches_PrintsMatchCount()
    {
        PrepareClip("clip1.mp4", "game", "Team Fortress 2");
        PrepareClip("clip2.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        FindCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        StringAssert.Contains(writer.ToString(), "2 match(es) found.");
    }

    [TestMethod]
    public void Run_EmptyDirectory_ReturnsZero()
    {
        using var writer = new StringWriter();

        int exitCode = FindCommand.Run(_tempDir, "game", "anything", writer);

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

            int exitCode = FindCommand.Run(_tempDir, "game", "TF2");

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(writer.ToString(), "Searching");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
