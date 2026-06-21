using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class WatchingResolverTests
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

    private string Touch(string name)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private WatchingResolver Resolver(params ProcessWindow[] windows) =>
        WatchingResolver.CreateDefault(new FakeProcessWindowSource(windows));

    private static IReadOnlyList<WatchingCandidate> Candidates(WatchingResolver resolver, string dir, int limit, bool fallback) =>
        resolver.Resolve(dir, limit, fallback).Candidates;

    [TestMethod]
    public void Resolve_SingleUnambiguousPlayerHit_IsHighAndFirst()
    {
        string clip = Touch("clip.mp4");
        Touch("other.mp4");

        IReadOnlyList<WatchingCandidate> result =
            Candidates(Resolver(new ProcessWindow("vlc", "clip.mp4 - VLC media player")), _tempDir, 5, true);

        Assert.AreEqual(clip, result[0].Path);
        Assert.AreEqual("high", result[0].Confidence);
        Assert.AreEqual(PlayerTitleSignal.SourceName, result[0].Source);
        Assert.AreEqual("vlc", result[0].Player);
    }

    [TestMethod]
    public void Resolve_NoPlayer_FallsBackToMostRecentAccessAsLow()
    {
        string older = Touch("older.mp4");
        string newer = Touch("newer.mp4");
        File.SetLastAccessTimeUtc(older, DateTime.UtcNow.AddHours(-3));
        File.SetLastAccessTimeUtc(newer, DateTime.UtcNow);

        IReadOnlyList<WatchingCandidate> result =
            Candidates(Resolver(), _tempDir, 5, true);

        Assert.AreEqual(newer, result[0].Path);
        Assert.IsTrue(result.All(c => c.Confidence == "low"));
        Assert.AreEqual(AccessTimeSignal.SourceName, result[0].Source);
    }

    [TestMethod]
    public void Resolve_NoPlayer_AndFallbackDisabled_ReturnsEmpty()
    {
        Touch("a.mp4");

        IReadOnlyList<WatchingCandidate> result =
            Candidates(Resolver(), _tempDir, 5, false);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Resolve_MultiplePlayers_AllLow()
    {
        Touch("a.mp4");
        Touch("b.mp4");

        IReadOnlyList<WatchingCandidate> result = Candidates(
            Resolver(
                new ProcessWindow("vlc", "a.mp4 - VLC media player"),
                new ProcessWindow("mpc-hc64", "b.mp4")),
            _tempDir, 5, false);

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.All(c => c.Confidence == "low"));
    }

    [TestMethod]
    public void Resolve_EmptyLibrary_ReturnsEmpty()
    {
        Assert.AreEqual(0, Candidates(Resolver(), _tempDir, 5, true).Count);
    }

    [TestMethod]
    public void Resolve_LockedFile_ReportsInUseTrue()
    {
        string busy = Touch("busy.mp4");
        using var hold = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read);

        WatchingCandidate candidate = Candidates(
                Resolver(new ProcessWindow("vlc", "busy.mp4 - VLC media player")), _tempDir, 5, true)
            .Single(c => c.Path == busy);

        Assert.IsTrue(candidate.InUse);
    }

    [TestMethod]
    public void Resolve_FreeFile_ReportsInUseFalse()
    {
        string free = Touch("free.mp4");

        WatchingCandidate candidate = Candidates(
                Resolver(new ProcessWindow("vlc", "free.mp4 - VLC media player")), _tempDir, 5, true)
            .Single(c => c.Path == free);

        Assert.IsFalse(candidate.InUse);
    }

    [TestMethod]
    public void Resolve_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
            Touch($"clip{i}.mp4");

        IReadOnlyList<WatchingCandidate> result =
            Candidates(Resolver(), _tempDir, 3, true);

        Assert.AreEqual(3, result.Count);
    }

    [TestMethod]
    public void Resolve_PlayerHitWithFallback_AccessOnlyClipsAppearAsLowRows()
    {
        string watched = Touch("watched.mp4");
        string bystander = Touch("bystander.mp4");

        IReadOnlyList<WatchingCandidate> result =
            Candidates(Resolver(new ProcessWindow("vlc", "watched.mp4 - VLC media player")), _tempDir, 10, true);

        WatchingCandidate watchedCandidate = result.Single(c => c.Name == "watched.mp4");
        Assert.AreEqual("high", watchedCandidate.Confidence);
        Assert.AreEqual(PlayerTitleSignal.SourceName, watchedCandidate.Source);

        WatchingCandidate bystanderCandidate = result.Single(c => c.Name == "bystander.mp4");
        Assert.AreEqual("low", bystanderCandidate.Confidence);
        Assert.AreEqual(AccessTimeSignal.SourceName, bystanderCandidate.Source);
    }
}
