using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class PlayerTitleParserTests
{
    [TestMethod]
    public void Extract_MpcFullPathTitle_ReturnsFullPath()
    {
        TitleExtraction? r = PlayerTitleParser.Extract(@"C:\clips\2026.06.20\clip001.mp4 - MPC-HC");
        Assert.IsNotNull(r);
        Assert.AreEqual(TitleExtractionKind.FullPath, r.Value.Kind);
        Assert.AreEqual(@"C:\clips\2026.06.20\clip001.mp4", r.Value.Value);
    }

    [TestMethod]
    public void Extract_VlcBareNameTitle_ReturnsBareName()
    {
        TitleExtraction? r = PlayerTitleParser.Extract("clip001.mp4 - VLC media player");
        Assert.IsNotNull(r);
        Assert.AreEqual(TitleExtractionKind.BareName, r.Value.Kind);
        Assert.AreEqual("clip001.mp4", r.Value.Value);
    }

    [TestMethod]
    public void Extract_TitleWithoutMp4_ReturnsNull()
    {
        // VLC showing an embedded metadata title, or a stopped player.
        Assert.IsNull(PlayerTitleParser.Extract("My Awesome Montage - VLC media player"));
    }

    [TestMethod]
    public void Extract_NullOrEmpty_ReturnsNull()
    {
        Assert.IsNull(PlayerTitleParser.Extract(null));
        Assert.IsNull(PlayerTitleParser.Extract("   "));
    }

    [TestMethod]
    public void Extract_FullPathPreferredOverBareName()
    {
        // A title containing a full path must yield the full path, not just the filename tail.
        TitleExtraction? r = PlayerTitleParser.Extract(@"Now playing C:\a\b\clip.mp4");
        Assert.IsNotNull(r);
        Assert.AreEqual(TitleExtractionKind.FullPath, r.Value.Kind);
        Assert.AreEqual(@"C:\a\b\clip.mp4", r.Value.Value);
    }
}
