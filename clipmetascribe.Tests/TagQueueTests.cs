// clipmetascribe.Tests/TagQueueTests.cs
using ClipMetaCore.Watching;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Tests;

[TestClass]
public class TagQueueTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cmqueue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static QueuedTag Tag(string path, string field, string value)
    {
        var m = new MetadataMutation();
        m.AppendFields[field] = value;
        return new QueuedTag(path, QueuedMutation.From(m), DateTimeOffset.UtcNow, "high");
    }

    [TestMethod]
    public void Load_MissingFile_ReturnsEmptyQueue_NeverThrows()
    {
        TagQueueData data = TagQueue.Load(_dir);
        Assert.AreEqual(0, data.Entries.Count);
    }

    [TestMethod]
    public void Save_ThenLoad_RoundTrips()
    {
        var data = new TagQueueData(1, new[] { Tag(Path.Combine(_dir, "a.mp4"), "tags", "headshot") });
        TagQueue.Save(data, _dir);

        TagQueueData reloaded = TagQueue.Load(_dir);

        Assert.AreEqual(1, reloaded.Entries.Count);
        Assert.AreEqual(Path.Combine(_dir, "a.mp4"), reloaded.Entries[0].ClipPath);
        Assert.AreEqual("headshot", reloaded.Entries[0].Mutation.AppendFields["tags"]);
        Assert.AreEqual("high", reloaded.Entries[0].Confidence);
    }

    [TestMethod]
    public void Load_CorruptFile_ReturnsEmptyQueue_NeverThrows()
    {
        File.WriteAllText(Path.Combine(_dir, TagQueue.QueueFileName), "{ this is not valid json ]");
        TagQueueData data = TagQueue.Load(_dir);
        Assert.AreEqual(0, data.Entries.Count);
    }

    [TestMethod]
    public void Enqueue_NewClip_AddsOneEntry()
    {
        string clip = Path.Combine(_dir, "a.mp4");
        var m = new MetadataMutation(); m.AppendFields["tags"] = "headshot";
        TagQueue.Enqueue(_dir, clip, m, "high");

        TagQueueData data = TagQueue.Load(_dir);
        Assert.AreEqual(1, data.Entries.Count);
        Assert.AreEqual("headshot", data.Entries[0].Mutation.AppendFields["tags"]);
    }

    [TestMethod]
    public void Enqueue_SameClipTwice_MergesIntoOneEntry()
    {
        string clip = Path.Combine(_dir, "a.mp4");
        var m1 = new MetadataMutation(); m1.AppendFields["tags"] = "headshot";
        var m2 = new MetadataMutation(); m2.AppendFields["tags"] = "airshot"; m2.SetFields["game"] = "TF2";
        TagQueue.Enqueue(_dir, clip, m1, "high");
        TagQueue.Enqueue(_dir, clip, m2, "high");

        TagQueueData data = TagQueue.Load(_dir);
        Assert.AreEqual(1, data.Entries.Count, "same clip must merge, not duplicate");
        // append accumulated both values (pipe-joined), set captured the new field
        StringAssert.Contains(data.Entries[0].Mutation.AppendFields["tags"], "headshot");
        StringAssert.Contains(data.Entries[0].Mutation.AppendFields["tags"], "airshot");
        Assert.AreEqual("TF2", data.Entries[0].Mutation.SetFields["game"]);
    }
}
