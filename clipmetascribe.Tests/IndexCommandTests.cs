using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class IndexCommandTests
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
    public void Run_ValidDirectory_CreatesIndexFile()
    {
        PrepareClip("clip.mp4", "game", "TF2");
        using var writer = new StringWriter();

        IndexCommand.Run(_tempDir, writer);

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, ClipMetaIndex.IndexFileName)));
    }

    [TestMethod]
    public void Run_ValidDirectory_ReturnsZero()
    {
        PrepareClip("clip.mp4", "game", "TF2");
        using var writer = new StringWriter();

        int exitCode = IndexCommand.Run(_tempDir, writer);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void Run_ValidDirectory_PrintsFileCount()
    {
        PrepareClip("clip1.mp4", "game", "TF2");
        PrepareClip("clip2.mp4", "game", "CS2");
        using var writer = new StringWriter();

        IndexCommand.Run(_tempDir, writer);

        StringAssert.Contains(writer.ToString(), "2");
    }

    [TestMethod]
    public void Run_EmptyDirectory_CreatesIndexWithZeroEntries()
    {
        using var writer = new StringWriter();

        IndexCommand.Run(_tempDir, writer);

        var data = ClipMetaIndex.ReadFromFile(Path.Combine(_tempDir, ClipMetaIndex.IndexFileName));
        Assert.AreEqual(0, data.Entries.Count);
    }

    // ── Locked file mixed in (v1.0.1 hardening, task B1) ────────────────────
    //
    // "One truncated clip must not brick the library": a locked/unreadable clip must not abort
    // the scan, the good clip must still be indexed, and the output must name the skipped file.

    [TestMethod]
    public void Run_LockedFileMixedIn_IndexesGoodFileAndReturnsZero()
    {
        PrepareClip("good.mp4", "game", "TF2");
        string locked = Path.Combine(_tempDir, "locked.mp4");
        File.WriteAllBytes(locked, new byte[] { 0, 0, 0, 0 });
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var writer = new StringWriter();

        int exitCode = IndexCommand.Run(_tempDir, writer);

        Assert.AreEqual(0, exitCode);
        var data = ClipMetaIndex.ReadFromFile(Path.Combine(_tempDir, ClipMetaIndex.IndexFileName));
        Assert.AreEqual(1, data.Entries.Count);
    }

    [TestMethod]
    public void Run_LockedFileMixedIn_ReportsSkippedPathInOutput()
    {
        PrepareClip("good.mp4", "game", "TF2");
        string locked = Path.Combine(_tempDir, "locked.mp4");
        File.WriteAllBytes(locked, new byte[] { 0, 0, 0, 0 });
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var writer = new StringWriter();

        IndexCommand.Run(_tempDir, writer);

        StringAssert.Contains(writer.ToString(), locked);
    }

    [TestMethod]
    public void Run_DefaultOutput_UsesConsoleOut()
    {
        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            int exitCode = IndexCommand.Run(_tempDir);

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(writer.ToString(), "Indexed");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
