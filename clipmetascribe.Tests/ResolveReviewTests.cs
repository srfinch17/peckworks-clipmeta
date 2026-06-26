using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ResolveReviewTests
{
    private string _dir = null!;
    private static readonly DateTimeOffset T0 = new(2026, 6, 26, 6, 0, 0, TimeSpan.Zero);

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Done() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string Touch(string name)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, Array.Empty<byte>());
        return p;
    }

    // No live windows needed: review path resolves from supplied segments. Empty source = cold poll.
    private WatchingResolver Resolver() =>
        WatchingResolver.CreateDefault(new FakeProcessWindowSource());

    private static TitleSegment Seg(long id, string title, double start, double? end) =>
        new(id, "vlc", title, T0.AddSeconds(start), end is { } e ? T0.AddSeconds(e) : null);

    [TestMethod]
    public void ResolveReview_JustStarted_BindsPreviousStable_PromotedHighUnlocked()
    {
        string four = Touch("_4.mp4");
        Touch("_5.mp4");
        var segs = new[]
        {
            Seg(1, $"{four} - VLC media player", 0, 30),                 // full path → unambiguous
            Seg(2, $"{Path.Combine(_dir, "_5.mp4")} - VLC media player", 30, null),
        };
        DateTimeOffset now = T0.AddSeconds(30.1);

        WatchingResult r = Resolver().ResolveReview(_dir, segs, lastBoundId: -1, now, limit: 5, includeAccessFallback: true);

        Assert.AreEqual(four, r.Candidates[0].Path, "binds _4, the previously stable clip");
        Assert.AreEqual("high", r.Candidates[0].Confidence);
        Assert.IsFalse(r.Candidates[0].InUse, "the corrected clip is unlocked (player advanced) but stays high");
        Assert.IsTrue(r.AnyLiveTarget, "a corrected bind is a live target");
        Assert.IsTrue(r.Review!.Any(f => f.Type == ReviewFlag.TypeAutoCorrected));
        Assert.AreEqual(1, r.BoundSegmentId);
        Assert.IsTrue(r.RecommendationConfident);
    }

    [TestMethod]
    public void ResolveReview_EmptySegments_FallsBackToColdPoll()
    {
        string older = Touch("older.mp4");
        string newer = Touch("newer.mp4");
        File.SetLastAccessTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.SetLastAccessTimeUtc(newer, DateTime.UtcNow);

        WatchingResult r = Resolver().ResolveReview(
            _dir, Array.Empty<TitleSegment>(), lastBoundId: -1, T0, limit: 5, includeAccessFallback: true);

        Assert.IsTrue(r.Candidates.Count >= 1, "cold start yields the access-time fallback");
        Assert.IsFalse(r.AnyLiveTarget, "nothing live with no player open");
    }

    [TestMethod]
    public void ResolveReview_MultiPlayer_NoCorrection_FlagAndWarn()
    {
        Touch("a.mp4");
        Touch("b.mp4");
        var segs = new[]
        {
            new TitleSegment(1, "vlc", $"{Path.Combine(_dir, "a.mp4")} - VLC media player", T0, null),
            new TitleSegment(2, "mpc-hc64", $"{Path.Combine(_dir, "b.mp4")} - MPC-HC", T0.AddSeconds(0.2), null),
        };
        WatchingResult r = Resolver().ResolveReview(_dir, segs, -1, T0.AddSeconds(5), 5, true);

        Assert.IsTrue(r.Review!.Any(f => f.Type == ReviewFlag.TypeMultiplePlayersActive));
        Assert.IsFalse(r.RecommendationConfident, "no confident single bind when two players are active");
    }
}
