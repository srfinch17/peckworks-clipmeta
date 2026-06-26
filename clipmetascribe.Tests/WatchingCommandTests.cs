using ClipMetaCore.Watching;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

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

    [TestMethod]
    public void Run_ForeignPlayer_PrintsWarningAndSuppressesCandidates()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "inlibrary.mp4"), Array.Empty<byte>());
        var source = new FakeProcessWindowSource(
            new ProcessWindow("mpc-hc64", @"D:\elsewhere\foreign.mp4 - MPC-HC"));
        using var sw = new StringWriter();

        int code = WatchingCommand.Run(_tempDir, 5, includeAccessFallback: true, output: sw, windowSource: source);

        Assert.AreEqual(0, code);
        string outp = sw.ToString();
        StringAssert.Contains(outp, "WARNING");
        StringAssert.Contains(outp, "mpc-hc64");
        StringAssert.Contains(outp, @"D:\elsewhere");
    }

    [TestMethod]
    public void Run_NothingLive_PrintsRecencyCaution()
    {
        // No player open and the clip isn't locked: candidates are recency guesses only. Both write
        // time AND creation time must be back-dated; the predicate now keys on creation time, so
        // back-dating write time alone would leave a fresh creation time and trigger gaming mode.
        string clip = Path.Combine(_tempDir, "clip.mp4");
        File.WriteAllBytes(clip, Array.Empty<byte>());
        DateTime old = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(clip, old);
        File.SetCreationTimeUtc(clip, old);
        using var sw = new StringWriter();

        int code = WatchingCommand.Run(_tempDir, 5, includeAccessFallback: true, output: sw);

        Assert.AreEqual(0, code);
        StringAssert.Contains(sw.ToString(), "nothing is currently open or locked");
    }

    [TestMethod]
    public void Run_BareNameUnlockedClip_PrintsConfirmNote()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "clip.mp4"), Array.Empty<byte>()); // free / unlocked
        var source = new FakeProcessWindowSource(
            new ProcessWindow("vlc", "clip.mp4 - VLC media player"));
        using var sw = new StringWriter();

        int code = WatchingCommand.Run(_tempDir, 5, includeAccessFallback: true, output: sw, windowSource: source);

        Assert.AreEqual(0, code);
        StringAssert.Contains(sw.ToString(), "note:");
    }
}
