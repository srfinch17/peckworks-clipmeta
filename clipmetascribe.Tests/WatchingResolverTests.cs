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

    [TestMethod]
    public void Resolve_SingleUnambiguousPlayerHit_IsHighAndFirst()
    {
        string clip = Touch("clip.mp4");
        Touch("other.mp4");

        IReadOnlyList<WatchingCandidate> result =
            Resolver(new ProcessWindow("vlc", "clip.mp4 - VLC media player"))
                .Resolve(_tempDir, limit: 5, includeAccessFallback: true);

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
            Resolver().Resolve(_tempDir, limit: 5, includeAccessFallback: true);

        Assert.AreEqual(newer, result[0].Path);
        Assert.IsTrue(result.All(c => c.Confidence == "low"));
        Assert.AreEqual(AccessTimeSignal.SourceName, result[0].Source);
    }

    [TestMethod]
    public void Resolve_NoPlayer_AndFallbackDisabled_ReturnsEmpty()
    {
        Touch("a.mp4");

        IReadOnlyList<WatchingCandidate> result =
            Resolver().Resolve(_tempDir, limit: 5, includeAccessFallback: false);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Resolve_MultiplePlayers_AllLow()
    {
        Touch("a.mp4");
        Touch("b.mp4");

        IReadOnlyList<WatchingCandidate> result = Resolver(
                new ProcessWindow("vlc", "a.mp4 - VLC media player"),
                new ProcessWindow("mpc-hc64", "b.mp4"))
            .Resolve(_tempDir, limit: 5, includeAccessFallback: false);

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.All(c => c.Confidence == "low"));
    }

    [TestMethod]
    public void Resolve_EmptyLibrary_ReturnsEmpty()
    {
        Assert.AreEqual(0, Resolver().Resolve(_tempDir, limit: 5, includeAccessFallback: true).Count);
    }

    [TestMethod]
    public void Resolve_LockedFile_ReportsInUseTrue()
    {
        string busy = Touch("busy.mp4");
        using var hold = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read);

        WatchingCandidate candidate = Resolver(new ProcessWindow("vlc", "busy.mp4 - VLC media player"))
            .Resolve(_tempDir, limit: 5, includeAccessFallback: true)
            .Single(c => c.Path == busy);

        Assert.IsTrue(candidate.InUse);
    }

    [TestMethod]
    public void Resolve_FreeFile_ReportsInUseFalse()
    {
        string free = Touch("free.mp4");

        WatchingCandidate candidate = Resolver(new ProcessWindow("vlc", "free.mp4 - VLC media player"))
            .Resolve(_tempDir, limit: 5, includeAccessFallback: true)
            .Single(c => c.Path == free);

        Assert.IsFalse(candidate.InUse);
    }

    [TestMethod]
    public void Resolve_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
            Touch($"clip{i}.mp4");

        IReadOnlyList<WatchingCandidate> result =
            Resolver().Resolve(_tempDir, limit: 3, includeAccessFallback: true);

        Assert.AreEqual(3, result.Count);
    }
}
