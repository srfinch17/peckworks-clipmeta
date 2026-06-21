using ClipMetaCore.Mp4;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class AccessTimeGuardTests
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
    public void Guard_RestoresLastAccessTime_AfterAnInterveningRead()
    {
        string path = Path.Combine(_tempDir, "f.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        DateTime original = DateTime.UtcNow.AddDays(-3);
        File.SetLastAccessTimeUtc(path, original);

        using (new AccessTimeGuard(path))
        {
            // Simulate a read bumping the access time.
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }

        Assert.AreEqual(
            original, File.GetLastAccessTimeUtc(path),
            "guard must restore the captured access time on dispose");
    }

    [TestMethod]
    public void Guard_MissingFile_DoesNotThrow()
    {
        string path = Path.Combine(_tempDir, "does-not-exist.bin");
        // Construction captures (best-effort) and disposal restores (best-effort); neither throws.
        using (new AccessTimeGuard(path)) { }
        Assert.IsFalse(File.Exists(path), "guard must not create a missing file");
    }

    [TestMethod]
    public void ParseFile_DoesNotChangeLastAccessTime()
    {
        if (!TestClipsLocator.PristineClipsPresent())
        {
            Assert.Inconclusive("No test clips in testclips/pristine — skipped (e.g. CI).");
            return;
        }

        string clip = Path.Combine(_tempDir, "clip.mp4");
        File.Copy(TestClipsLocator.SmallestPristine(), clip);
        DateTime original = DateTime.UtcNow.AddDays(-2);
        File.SetLastAccessTimeUtc(clip, original);

        _ = ClipMetaCore.Mp4.Mp4Parser.ParseFile(clip);

        // Tolerance: filesystem access-time granularity can be coarse; assert within 5 seconds.
        TimeSpan drift = (File.GetLastAccessTimeUtc(clip) - original).Duration();
        Assert.IsTrue(drift < TimeSpan.FromSeconds(5),
            $"reading a clip changed its last-access time by {drift}");
    }
}
