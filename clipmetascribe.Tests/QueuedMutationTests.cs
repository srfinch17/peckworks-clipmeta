// clipmetascribe.Tests/QueuedMutationTests.cs
using ClipMetaCore.Watching;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Tests;

[TestClass]
public class QueuedMutationTests
{
    [TestMethod]
    public void From_DropsTransientFlags_AndCapturesDurableState()
    {
        var m = new MetadataMutation { DryRun = true, BackupPath = "x.bak", ClearAll = false };
        m.SetFields["game"] = "TF2";
        m.AppendFields["tags"] = "headshot";
        m.DeleteFields.Add("notes");

        QueuedMutation q = QueuedMutation.From(m);

        Assert.AreEqual("TF2", q.SetFields["game"]);
        Assert.AreEqual("headshot", q.AppendFields["tags"]);
        CollectionAssert.AreEquivalent(new[] { "notes" }, q.DeleteFields.ToList());
        Assert.IsFalse(q.ClearAll);
    }

    [TestMethod]
    public void ToMutation_RoundTrips_AndClearsTransientFlags()
    {
        var original = new MetadataMutation { ClearAll = true };
        original.SetFields["game"] = "TF2";

        MetadataMutation rebuilt = QueuedMutation.From(original).ToMutation();

        Assert.AreEqual("TF2", rebuilt.SetFields["game"]);
        Assert.IsTrue(rebuilt.ClearAll);
        Assert.IsFalse(rebuilt.DryRun);
        Assert.IsNull(rebuilt.BackupPath);
    }
}
