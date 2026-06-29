using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ReviewBindingResolverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 26, 6, 0, 0, TimeSpan.Zero);
    private const long NoBind = -1;

    private static TitleSegment Seg(long id, string title, double startOffsetSec, double? endOffsetSec, string proc = "vlc") =>
        new(id, proc, title,
            T0.AddSeconds(startOffsetSec),
            endOffsetSec is { } e ? T0.AddSeconds(e) : null);

    [TestMethod]
    public void Resolve_CurrentJustStarted_BindsPreviousStable()
    {
        // Run-2 dict4 replay: _4 played 30s then _5 opened 0.1s ago. The user is describing _4.
        var segs = new[]
        {
            Seg(1, "_4.mp4 - VLC media player", startOffsetSec: 0, endOffsetSec: 30),
            Seg(2, "_5.mp4 - VLC media player", startOffsetSec: 30, endOffsetSec: null),
        };
        DateTimeOffset now = T0.AddSeconds(30.1); // _5 has been open 0.1s

        ReviewBinding b = ReviewBindingResolver.Resolve(segs, NoBind, now);

        Assert.AreEqual(1, b.Chosen!.Id);
        Assert.AreEqual(2, b.CorrectedFrom!.Id);
        Assert.IsTrue(b.Flags.Any(f => f.Type == ReviewFlag.TypeAutoCorrected));
    }

    [TestMethod]
    public void Resolve_CurrentStable_BindsCurrent_NoCorrection()
    {
        var segs = new[] { Seg(1, "_3.mp4 - VLC media player", 0, null) };
        DateTimeOffset now = T0.AddSeconds(10); // open 10s, well past threshold

        ReviewBinding b = ReviewBindingResolver.Resolve(segs, NoBind, now);

        Assert.AreEqual(1, b.Chosen!.Id);
        Assert.IsNull(b.CorrectedFrom);
        Assert.IsFalse(b.Flags.Any(f => f.Type == ReviewFlag.TypeAutoCorrected));
    }

    [TestMethod]
    public void Resolve_EmptySegments_ChosenNull()
    {
        ReviewBinding b = ReviewBindingResolver.Resolve(Array.Empty<TitleSegment>(), NoBind, T0);
        Assert.IsNull(b.Chosen);
    }

    [TestMethod]
    public void Resolve_TwoPlayersActive_AmbiguousNoChoice()
    {
        var segs = new[]
        {
            Seg(1, "a.mp4 - VLC media player", 0, null, proc: "vlc"),
            Seg(2, "b.mp4 - MPC-HC", 0.2, null, proc: "mpc-hc64"),
        };
        ReviewBinding b = ReviewBindingResolver.Resolve(segs, NoBind, T0.AddSeconds(5));

        Assert.IsTrue(b.AmbiguousMultiPlayer);
        Assert.IsNull(b.Chosen);
        Assert.IsTrue(b.Flags.Any(f => f.Type == ReviewFlag.TypeMultiplePlayersActive));
    }

    [TestMethod]
    public void Resolve_SameClipAsLastBind_FlagsSameClipTwice()
    {
        var segs = new[] { Seg(7, "_3.mp4 - VLC media player", 0, null) };
        ReviewBinding b = ReviewBindingResolver.Resolve(segs, lastBoundId: 7, now: T0.AddSeconds(10));

        Assert.AreEqual(7, b.Chosen!.Id);
        Assert.IsTrue(b.Flags.Any(f => f.Type == ReviewFlag.TypeSameClipTwice));
    }

    [TestMethod]
    public void Resolve_StableSegmentSkippedSinceLastBind_FlagsSequenceSkip()
    {
        // Last bind was id 1; id 2 played stably but was never bound; now binding id 3.
        var segs = new[]
        {
            Seg(1, "_1.mp4 - VLC media player", 0, 10),
            Seg(2, "_2.mp4 - VLC media player", 10, 25),   // stable, never bound
            Seg(3, "_3.mp4 - VLC media player", 25, null),
        };
        ReviewBinding b = ReviewBindingResolver.Resolve(segs, lastBoundId: 1, now: T0.AddSeconds(35));

        Assert.AreEqual(3, b.Chosen!.Id);
        ReviewFlag skip = b.Flags.Single(f => f.Type == ReviewFlag.TypeSequenceSkip);
        Assert.IsTrue(skip.Clips.Any(c => c.Contains("_2")));
    }

    // ── AC2: spoken-at exact-timestamp binding ───────────────────────────────────────────

    private static TitleSegment[] ThreeClipHistory() => new[]
    {
        Seg(1, "_1.mp4 - VLC media player", 0, 10),
        Seg(2, "_2.mp4 - VLC media player", 10, 25),
        Seg(3, "_3.mp4 - VLC media player", 25, null), // open
    };

    [TestMethod]
    public void Resolve_SpokenAtInPastSegment_BindsThatSegment_NotCurrent()
    {
        // The user spoke 15s in, during _2, even though the player is now parked on _3.
        DateTimeOffset spokenAt = T0.AddSeconds(15);
        DateTimeOffset now = T0.AddSeconds(40);

        ReviewBinding b = ReviewBindingResolver.Resolve(
            ThreeClipHistory(), NoBind, now, stableThreshold: null, spokenAt: spokenAt);

        Assert.AreEqual(2, b.Chosen!.Id, "binds the segment covering the spoken instant");
        Assert.IsNull(b.CorrectedFrom, "an exact-timestamp hit is not a previous-stable correction");
        Assert.IsFalse(b.Flags.Any(f => f.Type == ReviewFlag.TypeAutoCorrected));
        Assert.IsFalse(b.Flags.Any(f => f.Type == ReviewFlag.TypeTimestampUnmatched));
    }

    [TestMethod]
    public void Resolve_SpokenAtInOpenSegment_BindsCurrent()
    {
        DateTimeOffset spokenAt = T0.AddSeconds(30); // inside the open _3 window
        DateTimeOffset now = T0.AddSeconds(40);

        ReviewBinding b = ReviewBindingResolver.Resolve(
            ThreeClipHistory(), NoBind, now, stableThreshold: null, spokenAt: spokenAt);

        Assert.AreEqual(3, b.Chosen!.Id);
        Assert.IsNull(b.CorrectedFrom);
    }

    [TestMethod]
    public void Resolve_SpokenAtOutsideHistory_FallsBackToHeuristic_FlagsTimestampUnmatched()
    {
        DateTimeOffset spokenAt = T0.AddSeconds(-30); // before the earliest segment
        DateTimeOffset now = T0.AddSeconds(40);

        ReviewBinding b = ReviewBindingResolver.Resolve(
            ThreeClipHistory(), NoBind, now, stableThreshold: null, spokenAt: spokenAt);

        Assert.AreEqual(3, b.Chosen!.Id, "no exact match → heuristic binds the current stable clip");
        Assert.IsTrue(b.Flags.Any(f => f.Type == ReviewFlag.TypeTimestampUnmatched),
            "the caller must learn the exact lookup missed and a guess was used");
    }

    [TestMethod]
    public void Resolve_SpokenAtCoveredByTwoPlayers_Ambiguous()
    {
        var segs = new[]
        {
            Seg(1, "a.mp4 - VLC media player", 0, null, proc: "vlc"),
            Seg(2, "b.mp4 - MPC-HC", 0, null, proc: "mpc-hc64"),
        };
        DateTimeOffset spokenAt = T0.AddSeconds(3); // both players open and covering it

        ReviewBinding b = ReviewBindingResolver.Resolve(
            segs, NoBind, T0.AddSeconds(5), stableThreshold: null, spokenAt: spokenAt);

        Assert.IsTrue(b.AmbiguousMultiPlayer);
        Assert.IsNull(b.Chosen);
        Assert.IsTrue(b.Flags.Any(f => f.Type == ReviewFlag.TypeMultiplePlayersActive));
    }

    [TestMethod]
    public void Resolve_SpokenAtAbsent_NoTimestampUnmatchedFlag()
    {
        ReviewBinding b = ReviewBindingResolver.Resolve(ThreeClipHistory(), NoBind, T0.AddSeconds(40));
        Assert.IsFalse(b.Flags.Any(f => f.Type == ReviewFlag.TypeTimestampUnmatched),
            "omitting spoken_at never attempts an exact lookup, so never flags a miss");
    }
}
