using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class LockProbeTests
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
    public void IsInUse_FreeFile_False()
    {
        string path = Path.Combine(_tempDir, "free.mp4");
        File.WriteAllBytes(path, Array.Empty<byte>());
        Assert.IsFalse(LockProbe.IsInUse(path));
    }

    [TestMethod]
    public void IsInUse_FileHeldByAnotherHandle_True()
    {
        string path = Path.Combine(_tempDir, "busy.mp4");
        File.WriteAllBytes(path, Array.Empty<byte>());
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.IsTrue(LockProbe.IsInUse(path));
    }

    [TestMethod]
    public void IsInUse_OfflineFile_ReturnsFalseWithoutOpening()
    {
        // An offline/placeholder file must NOT be opened (that would force a cloud download). Prove
        // the short-circuit by holding the file EXCLUSIVELY: if the probe tried to open it, the
        // FileShare.None open would throw and report true. Returning false proves it never opened.
        string path = Path.Combine(_tempDir, "offline.mp4");
        File.WriteAllBytes(path, Array.Empty<byte>());
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.IsFalse(LockProbe.IsInUse(path),
            "offline files must be reported not-in-use without being opened");
    }

    [TestMethod]
    public void IsInUse_MissingFile_False()
    {
        Assert.IsFalse(LockProbe.IsInUse(Path.Combine(_tempDir, "nope.mp4")));
    }

    [TestMethod]
    public void IsInUse_MalformedPath_ReturnsFalseWithoutThrowing()
    {
        // A malformed-format path can make File.GetAttributes throw NotSupportedException; the probe
        // must absorb it (never throw — that would crash resolution) and report not-in-use.
        string malformed = @"::\\not a real path::|*?";
        bool result = false;
        try
        {
            result = LockProbe.IsInUse(malformed);
        }
        catch (Exception ex)
        {
            Assert.Fail($"IsInUse must never throw, but threw {ex.GetType().Name}: {ex.Message}");
        }
        Assert.IsFalse(result);
    }
}
