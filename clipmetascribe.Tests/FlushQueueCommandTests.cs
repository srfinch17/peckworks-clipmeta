// clipmetascribe.Tests/FlushQueueCommandTests.cs
using ClipMetaCore.Watching;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;

namespace ClipMetaScribe.Tests;

[TestClass]
public class FlushQueueCommandTests
{
    private string _dir = null!;
    [TestInitialize] public void Init()
    { _dir = Path.Combine(Path.GetTempPath(), "cmflush-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }
    [TestCleanup] public void Cleanup()
    { try { Directory.Delete(_dir, true); } catch { } }

    [TestMethod]
    public void Run_EmptyQueue_ReportsNothingPending()
    {
        var sw = new StringWriter();
        int code = FlushQueueCommand.Run(_dir, sw, new Mp4Writer(), _ => false);
        Assert.AreEqual(0, code);
        StringAssert.Contains(sw.ToString().ToLowerInvariant(), "no tags queued");
    }

    [TestMethod]
    public void Run_LockedEntry_ReportsStillQueued()
    {
        var m = new MetadataMutation(); m.AppendFields["tags"] = "headshot";
        TagQueue.Enqueue(_dir, Path.Combine(_dir, "a.mp4"), m, "high");
        var sw = new StringWriter();
        int code = FlushQueueCommand.Run(_dir, sw, new Mp4Writer(), _ => true);
        Assert.AreEqual(0, code);
        StringAssert.Contains(sw.ToString().ToLowerInvariant(), "still");
    }
}
