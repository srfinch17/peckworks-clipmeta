using ClipMetaCore.Read;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaSearchTests
{
    private static IndexEntry MakeEntry(string path, params (string Field, string Value)[] fields)
        => new IndexEntry(path, 0, DateTimeOffset.UtcNow, fields.ToList());

    private static IndexData MakeIndex(params IndexEntry[] entries)
        => new IndexData(@"C:\clips", DateTimeOffset.UtcNow, entries.ToList());

    [TestMethod]
    public void Find_MatchingField_ReturnsEntry()
    {
        var index = MakeIndex(MakeEntry("clip.mp4", ("game", "Team Fortress 2")));

        var results = ClipMetaSearch.Find(index, "game", "Team Fortress 2");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("clip.mp4", results[0].FilePath);
    }

    [TestMethod]
    public void Find_NoMatch_ReturnsEmpty()
    {
        var index = MakeIndex(MakeEntry("clip.mp4", ("game", "Team Fortress 2")));

        var results = ClipMetaSearch.Find(index, "game", "Counter-Strike");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Find_CaseInsensitive_Matches()
    {
        var index = MakeIndex(MakeEntry("clip.mp4", ("game", "Team Fortress 2")));

        var results = ClipMetaSearch.Find(index, "GAME", "team fortress");

        Assert.AreEqual(1, results.Count);
    }

    [TestMethod]
    public void Find_SubstringMatch_Matches()
    {
        var index = MakeIndex(MakeEntry("clip.mp4", ("game", "Team Fortress 2")));

        var results = ClipMetaSearch.Find(index, "game", "Fortress");

        Assert.AreEqual(1, results.Count);
    }

    [TestMethod]
    public void Find_EmptyIndex_ReturnsEmpty()
    {
        var index = MakeIndex();

        var results = ClipMetaSearch.Find(index, "game", "TF2");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Find_MultipleEntries_ReturnsOnlyMatches()
    {
        var index = MakeIndex(
            MakeEntry("clip1.mp4", ("game", "Team Fortress 2")),
            MakeEntry("clip2.mp4", ("game", "Counter-Strike 2")));

        var results = ClipMetaSearch.Find(index, "game", "Team Fortress");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("clip1.mp4", results[0].FilePath);
    }

    [TestMethod]
    public void Find_EntryWithNoMatchingField_NotIncluded()
    {
        var index = MakeIndex(MakeEntry("clip.mp4", ("notes", "some notes")));

        var results = ClipMetaSearch.Find(index, "game", "TF2");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Find_EmptyValue_ReturnsAllEntriesWithField()
    {
        var index = MakeIndex(
            MakeEntry("clip1.mp4", ("game", "Team Fortress 2")),
            MakeEntry("clip2.mp4", ("notes", "some notes")));

        var results = ClipMetaSearch.Find(index, "game", "");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("clip1.mp4", results[0].FilePath);
    }
}
