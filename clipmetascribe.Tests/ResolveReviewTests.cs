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

    [TestMethod]
    public void ResolveReview_TwoPlayersOpenFarApart_FlagsMultiPlayer()
    {
        // #2: two players each open a clip, started 20s apart (NOT near-simultaneous). The old rule
        // missed this; the widened rule fires multiplePlayersActive whenever two players are open.
        Touch("a.mp4");
        Touch("b.mp4");
        var segs = new[]
        {
            new TitleSegment(1, "vlc", $"{Path.Combine(_dir, "a.mp4")} - VLC media player", T0, null),
            new TitleSegment(2, "mpc-hc64", $"{Path.Combine(_dir, "b.mp4")} - MPC-HC", T0.AddSeconds(20), null),
        };

        WatchingResult r = Resolver().ResolveReview(_dir, segs, -1, T0.AddSeconds(40), 5, true);

        Assert.IsTrue(r.Review!.Any(f => f.Type == ReviewFlag.TypeMultiplePlayersActive),
            "two open players started far apart must still flag multiplePlayersActive");
    }

    [TestMethod]
    public void ResolveReview_MultiPlayer_CapsConfidenceAndNotLive()
    {
        // #2 cap: when multiplePlayersActive fires, the caller must confirm — anyLiveTarget is false
        // and no candidate is high, even if a clip is locked.
        string a = Touch("a.mp4");
        string b = Touch("b.mp4");
        using var holdB = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read);
        var segs = new[]
        {
            new TitleSegment(1, "vlc", $"{a} - VLC media player", T0, null),
            new TitleSegment(2, "mpc-hc64", $"{b} - MPC-HC", T0.AddSeconds(20), null),
        };

        WatchingResult r = Resolver().ResolveReview(_dir, segs, -1, T0.AddSeconds(40), 5, true);

        Assert.IsFalse(r.AnyLiveTarget, "two open players → not an auto-tag target");
        Assert.IsTrue(r.Candidates.All(c => c.Confidence == "low"),
            "every candidate is demoted to low under multiplePlayersActive");
    }

    // ── AC2: spoken-at exact-timestamp binding ───────────────────────────────────────────

    [TestMethod]
    public void ResolveReview_SpokenAt_BindsHistoricalClip_PromotedHighConfident()
    {
        string one = Touch("_1.mp4");
        string two = Touch("_2.mp4");
        Touch("_3.mp4");
        var segs = new[]
        {
            Seg(1, $"{one} - VLC media player", 0, 10),
            Seg(2, $"{two} - VLC media player", 10, 25),
            Seg(3, $"{Path.Combine(_dir, "_3.mp4")} - VLC media player", 25, null),
        };
        DateTimeOffset spokenAt = T0.AddSeconds(15); // during _2
        DateTimeOffset now = T0.AddSeconds(40);      // player parked on _3

        WatchingResult r = Resolver().ResolveReview(
            _dir, segs, lastBoundId: -1, now, limit: 5, includeAccessFallback: true, spokenAt: spokenAt);

        Assert.AreEqual(two, r.Candidates[0].Path, "binds the clip the user was watching when they spoke");
        Assert.AreEqual("high", r.Candidates[0].Confidence);
        Assert.IsTrue(r.RecommendationConfident);
        Assert.AreEqual(2, r.BoundSegmentId);
        Assert.IsFalse(r.Review?.Any(f => f.Type == ReviewFlag.TypeAutoCorrected) ?? false,
            "an exact-timestamp bind is not an auto-correction");
    }

    [TestMethod]
    public void ResolveReview_FireNAhead_OldestFirst_BindsEachInTurn()
    {
        string one = Touch("_1.mp4");
        string two = Touch("_2.mp4");
        string three = Touch("_3.mp4");
        var segs = new[]
        {
            Seg(1, $"{one} - VLC media player", 0, 10),
            Seg(2, $"{two} - VLC media player", 10, 25),
            Seg(3, $"{three} - VLC media player", 25, null),
        };
        DateTimeOffset now = T0.AddSeconds(40);
        WatchingResolver resolver = Resolver();

        // Three backlogged dictations, oldest first; thread lastBoundId via the prior bind.
        long bound = -1;
        var resolved = new List<string>();
        foreach (double at in new[] { 5.0, 15.0, 30.0 })
        {
            WatchingResult r = resolver.ResolveReview(
                _dir, segs, bound, now, limit: 5, includeAccessFallback: true,
                spokenAt: T0.AddSeconds(at));
            resolved.Add(r.Candidates[0].Path);
            Assert.IsFalse(r.Review?.Any(f => f.Type == ReviewFlag.TypeSequenceSkip) ?? false,
                "oldest-first fire-N-ahead must not trip a spurious sequence-skip");
            bound = r.BoundSegmentId ?? bound;
        }

        CollectionAssert.AreEqual(new[] { one, two, three }, resolved,
            "each call resolves its own clip from its timestamp");
    }
}
