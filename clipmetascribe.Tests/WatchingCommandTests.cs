using ClipMetaScribe.Commands;

namespace ClipMetaScribe.Tests;

[TestClass]
public class WatchingCommandTests
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

    [TestMethod]
    public void Run_WithClipsAndFallback_ListsCandidatePaths()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "clip.mp4"), Array.Empty<byte>());
        using var sw = new StringWriter();

        int code = WatchingCommand.Run(_tempDir, limit: 5, includeAccessFallback: true, output: sw);

        Assert.AreEqual(0, code);
        StringAssert.Contains(sw.ToString(), "clip.mp4");
    }

    [TestMethod]
    public void Run_EmptyLibrary_ReportsNoCandidates()
    {
        using var sw = new StringWriter();

        int code = WatchingCommand.Run(_tempDir, limit: 5, includeAccessFallback: true, output: sw);

        Assert.AreEqual(0, code);
        StringAssert.Contains(sw.ToString(), "No watched-clip candidates");
    }
}
