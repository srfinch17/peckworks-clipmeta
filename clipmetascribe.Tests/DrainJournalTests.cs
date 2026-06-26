using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class DrainJournalTests
{
    private static DrainedTag Tag(string p) =>
        new(p, new[] { "tags" }, DateTimeOffset.UtcNow);

    [TestMethod]
    public void TakePending_ReturnsRecorded_ThenClears()
    {
        var j = new DrainJournal();
        j.Record(Tag(@"C:\lib\a.mp4"));
        j.Record(Tag(@"C:\lib\b.mp4"));

        var first = j.TakePending();
        CollectionAssert.AreEqual(
            new[] { @"C:\lib\a.mp4", @"C:\lib\b.mp4" }, first.Select(t => t.Path).ToArray());

        Assert.AreEqual(0, j.TakePending().Count); // report-once: cleared
    }

    [TestMethod]
    public void Record_OverCap_DropsOldest()
    {
        var j = new DrainJournal();
        for (int i = 0; i < 60; i++) j.Record(Tag($@"C:\lib\{i}.mp4"));

        var pending = j.TakePending();
        Assert.AreEqual(50, pending.Count);
        Assert.AreEqual(@"C:\lib\10.mp4", pending[0].Path); // first 10 dropped
    }
}
