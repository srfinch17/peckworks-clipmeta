using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class WatchSignalsTests
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

    private string Touch(string relativePath)
    {
        string path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private WatchContext Build(params ProcessWindow[] windows) =>
        WatchContext.Build(_tempDir, new FakeProcessWindowSource(windows), MediaPlayers.KnownProcessNames);

    [TestMethod]
    public void PlayerTitle_BareNameInLibrary_UnambiguousHit()
    {
        string clip = Touch("clip.mp4");
        WatchContext ctx = Build(new ProcessWindow("vlc", "clip.mp4 - VLC media player"));

        List<SignalHit> hits = new PlayerTitleSignal().Detect(ctx).ToList();

        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(clip, hits[0].ClipPath);
        Assert.AreEqual(PlayerTitleSignal.SourceName, hits[0].Source);
        Assert.AreEqual("vlc", hits[0].Player);
        Assert.IsFalse(hits[0].Ambiguous);
        Assert.AreEqual(TitleExtractionKind.BareName, hits[0].MatchKind);
    }

    [TestMethod]
    public void PlayerTitle_BareNameNotInLibrary_Dropped()
    {
        Touch("present.mp4");
        WatchContext ctx = Build(new ProcessWindow("vlc", "absent.mp4 - VLC media player"));

        Assert.AreEqual(0, new PlayerTitleSignal().Detect(ctx).Count());
    }

    [TestMethod]
    public void PlayerTitle_NameMatchesMultipleClips_AmbiguousHits()
    {
        Touch("dup.mp4");
        Touch(Path.Combine("sub", "dup.mp4"));
        WatchContext ctx = Build(new ProcessWindow("vlc", "dup.mp4 - VLC media player"));

        List<SignalHit> hits = new PlayerTitleSignal().Detect(ctx).ToList();

        Assert.AreEqual(2, hits.Count);
        Assert.IsTrue(hits.All(h => h.Ambiguous));
    }

    [TestMethod]
    public void PlayerTitle_MultiplePlayers_AllAmbiguous()
    {
        Touch("a.mp4");
        Touch("b.mp4");
        WatchContext ctx = Build(
            new ProcessWindow("vlc", "a.mp4 - VLC media player"),
            new ProcessWindow("mpc-hc64", "b.mp4"));

        List<SignalHit> hits = new PlayerTitleSignal().Detect(ctx).ToList();

        Assert.AreEqual(2, hits.Count);
        Assert.IsTrue(hits.All(h => h.Ambiguous));
    }

    [TestMethod]
    public void AccessTime_OrdersMostRecentFirst_AllAmbiguous()
    {
        string older = Touch("older.mp4");
        string newer = Touch("newer.mp4");
        File.SetLastAccessTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.SetLastAccessTimeUtc(newer, DateTime.UtcNow);
        WatchContext ctx = Build();

        List<SignalHit> hits = new AccessTimeSignal().Detect(ctx).ToList();

        Assert.AreEqual(newer, hits[0].ClipPath);
        Assert.AreEqual(older, hits[1].ClipPath);
        Assert.IsTrue(hits.All(h => h.Ambiguous && h.Player is null));
        Assert.AreEqual(AccessTimeSignal.SourceName, hits[0].Source);
    }
}
