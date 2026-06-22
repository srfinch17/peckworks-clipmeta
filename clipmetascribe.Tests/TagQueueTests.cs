// clipmetascribe.Tests/TagQueueTests.cs
using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Watching;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

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

    // Builds a structurally valid, writable .mp4 inside _dir, seeded with one clipmeta atom so it
    // parses as an existing-ilst clip (mirrors Mp4WriterTests). No pristine corpus needed.
    private string MakeClip(string name, string seedField = "game", string seedValue = "TF2")
    {
        string path = Path.Combine(_dir, name);
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, seedField, seedValue);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [TestMethod]
    public void Drain_UnlockedClip_WritesAndRemoves()
    {
        string clip = MakeClip("a.mp4");
        var m = new MetadataMutation();
        m.AppendFields[ClipMetaSchema.AtomName("tags")] = "headshot";
        TagQueue.Enqueue(_dir, clip, m, "high");

        DrainReport report = TagQueue.Drain(_dir, new Mp4Writer(), NullLogger.Instance, isInUse: _ => false);

        CollectionAssert.AreEqual(new[] { clip }, report.Written.ToList());
        Assert.AreEqual(0, TagQueue.Load(_dir).Entries.Count, "written entry removed from queue");
        var fields = ClipMetaReader.GetUserFields(Mp4Parser.ParseFile(clip));
        Assert.IsTrue(fields.Any(f => f.Field == "tags" && f.Value.Contains("headshot")),
            "the queued tag must have landed in the file");
    }

    [TestMethod]
    public void Drain_LockedClip_LeavesQueued()
    {
        string clip = MakeClip("a.mp4");
        var m = new MetadataMutation();
        m.AppendFields[ClipMetaSchema.AtomName("tags")] = "headshot";
        TagQueue.Enqueue(_dir, clip, m, "high");

        DrainReport report = TagQueue.Drain(_dir, new Mp4Writer(), NullLogger.Instance, isInUse: _ => true);

        CollectionAssert.AreEqual(new[] { clip }, report.StillQueued.ToList());
        Assert.AreEqual(1, TagQueue.Load(_dir).Entries.Count, "locked entry stays queued");
    }

    [TestMethod]
    public void Drain_VanishedClip_DroppedNoCrash()
    {
        string clip = Path.Combine(_dir, "gone.mp4"); // never created
        var m = new MetadataMutation();
        m.AppendFields[ClipMetaSchema.AtomName("tags")] = "headshot";
        TagQueue.Enqueue(_dir, clip, m, "high");

        DrainReport report = TagQueue.Drain(_dir, new Mp4Writer(), NullLogger.Instance, isInUse: _ => false);

        CollectionAssert.AreEqual(new[] { clip }, report.Dropped.ToList());
        Assert.AreEqual(0, TagQueue.Load(_dir).Entries.Count, "vanished entry dropped");
    }

    [TestMethod]
    public void Status_ReportsPendingEntries()
    {
        string clip = Path.Combine(_dir, "a.mp4");
        var m = new MetadataMutation();
        m.AppendFields[ClipMetaSchema.AtomName("tags")] = "headshot";
        m.SetFields[ClipMetaSchema.AtomName("game")] = "TF2";
        TagQueue.Enqueue(_dir, clip, m, "high");

        IReadOnlyList<QueueStatusEntry> status = TagQueue.Status(_dir, isInUse: _ => true);

        Assert.AreEqual(1, status.Count);
        Assert.AreEqual(clip, status[0].ClipPath);
        Assert.IsTrue(status[0].Locked);
        CollectionAssert.AreEquivalent(new[] { "tags", "game" }, status[0].ChangedFields.ToList(),
            "ChangedFields are display names (domain prefix stripped)");
    }
}
