using ClipMetaCore.Write;

namespace ClipMetaScribe.Tests;

[TestClass]
public class NormalizationTests
{
    [TestMethod]
    public void NormalizeTag_Lowercase()
        => Assert.AreEqual("market garden", Normalizer.NormalizeTag("Market Garden"));

    [TestMethod]
    public void NormalizeTag_Trims()
        => Assert.AreEqual("market garden", Normalizer.NormalizeTag("  market garden  "));

    [TestMethod]
    public void NormalizePipeList_Deduplicates()
    {
        string result = Normalizer.NormalizePipeList("headshot|funny|headshot");
        Assert.AreEqual("headshot|funny", result);
    }

    [TestMethod]
    public void NormalizePipeList_Lowercases()
    {
        string result = Normalizer.NormalizePipeList("Market Garden|Funny Moment");
        Assert.AreEqual("market garden|funny moment", result);
    }

    [TestMethod]
    public void NormalizePipeList_Trims()
    {
        string result = Normalizer.NormalizePipeList(" headshot | funny ");
        Assert.AreEqual("headshot|funny", result);
    }

    [TestMethod]
    public void AppendToPipeList_NewItem_Appended()
    {
        string result = Normalizer.AppendToPipeList("headshot|funny", "rocket jump");
        Assert.AreEqual("headshot|funny|rocket jump", result);
    }

    [TestMethod]
    public void AppendToPipeList_Duplicate_NotAdded()
    {
        string result = Normalizer.AppendToPipeList("headshot|funny", "headshot");
        Assert.AreEqual("headshot|funny", result);
    }

    [TestMethod]
    public void NormalizeTimecode_SecondsOnly_ExpandsToHHMMSS()
        => Assert.AreEqual("00:00:45", Normalizer.NormalizeTimecode("45"));

    [TestMethod]
    public void NormalizeTimecode_MMSS_ExpandsToHHMMSS()
        => Assert.AreEqual("00:00:45", Normalizer.NormalizeTimecode("0:45"));

    [TestMethod]
    public void NormalizeTimecode_AlreadyHHMMSS_Unchanged()
        => Assert.AreEqual("00:00:45", Normalizer.NormalizeTimecode("00:00:45"));

    [TestMethod]
    public void NormalizeTimecode_WithHours_Preserved()
        => Assert.AreEqual("01:23:45", Normalizer.NormalizeTimecode("1:23:45"));

    [TestMethod]
    public void NormalizeRating_Valid_Unchanged()
        => Assert.AreEqual("4", Normalizer.NormalizeRating("4"));

    [TestMethod]
    public void NormalizeRating_OutOfRange_Clamped()
        => Assert.AreEqual("5", Normalizer.NormalizeRating("9"));

    [TestMethod]
    public void ApplyToMutation_EmptyValue_TreatedAsDelete()
    {
        var mutation = new MetadataMutation();
        mutation.SetFields["tags"] = "";
        Normalizer.ApplyToMutation(mutation);
        Assert.IsTrue(mutation.DeleteFields.Contains("tags"),
            "Empty set value should move to DeleteFields");
        Assert.IsFalse(mutation.SetFields.ContainsKey("tags"),
            "tags should be removed from SetFields after normalization");
    }
}
