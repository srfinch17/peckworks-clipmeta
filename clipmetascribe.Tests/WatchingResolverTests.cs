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

        IReadOnlyList<WatchingCandidate> result = Candidates(
            Resolver(new ProcessWindow("mpc-hc64", $"{clip} - MPC-HC")),
            _tempDir, 5, true);

        Assert.AreEqual(clip, result[0].Path);
        Assert.AreEqual("high", result[0].Confidence);
        Assert.AreEqual(PlayerTitleSignal.SourceName, result[0].Source);
        Assert.AreEqual("mpc-hc64", result[0].Player);
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
        Touch("bystander.mp4");

        IReadOnlyList<WatchingCandidate> result = Candidates(
            Resolver(new ProcessWindow("mpc-hc64", $"{watched} - MPC-HC")),
            _tempDir, 5, true);

        WatchingCandidate high = result.Single(c => c.Name == "watched.mp4");
        Assert.AreEqual("high", high.Confidence);
        Assert.AreEqual(PlayerTitleSignal.SourceName, high.Source);

        WatchingCandidate low = result.Single(c => c.Name == "bystander.mp4");
        Assert.AreEqual("low", low.Confidence);
        Assert.AreEqual(AccessTimeSignal.SourceName, low.Source);
    }

    [TestMethod]
    public void Resolve_BareNameLocked_IsHigh()
    {
        string clip = Touch("clip.mp4");
        using var hold = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read);

        WatchingCandidate c = Candidates(
            Resolver(new ProcessWindow("vlc", "clip.mp4 - VLC media player")), _tempDir, 5, true)
            .Single(x => x.Name == "clip.mp4");

        Assert.AreEqual("high", c.Confidence);
        Assert.IsNull(c.Note);
    }

    [TestMethod]
    public void Resolve_BareNameNotLocked_DemotedToLowWithNote()
    {
        Touch("clip.mp4"); // free

        WatchingCandidate c = Candidates(
            Resolver(new ProcessWindow("vlc", "clip.mp4 - VLC media player")), _tempDir, 5, true)
            .Single(x => x.Name == "clip.mp4");

        Assert.AreEqual("low", c.Confidence);
        Assert.IsNotNull(c.Note); // confirm-before-tagging caveat
    }

    [TestMethod]
    public void Resolve_FullPathNotLocked_StaysHigh()
    {
        string clip = Touch("clip.mp4"); // free, but full-path match can't collide

        WatchingCandidate c = Candidates(
            Resolver(new ProcessWindow("mpc-hc64", $"{clip} - MPC-HC")), _tempDir, 5, true)
            .Single(x => x.Name == "clip.mp4");

        Assert.AreEqual("high", c.Confidence);
        Assert.IsNull(c.Note);
    }

    [TestMethod]
    public void Resolve_PlayerOnForeignFile_NoResolution_WarnsAndSuppressesFallback()
    {
        Touch("inlibrary.mp4"); // exists, but nobody is playing it

        WatchingResult result = Resolver(new ProcessWindow("mpc-hc64", @"D:\elsewhere\foreign.mp4 - MPC-HC"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.AreEqual(0, result.Candidates.Count, "access-time guesses must be suppressed");
        Assert.AreEqual(1, result.Diagnostics.UnresolvedPlayers.Count);
        UnresolvedPlayer up = result.Diagnostics.UnresolvedPlayers[0];
        Assert.AreEqual("mpc-hc64", up.Player);
        Assert.AreEqual(@"D:\elsewhere", up.ForeignDirectory);
    }

    [TestMethod]
    public void Resolve_BareNameForeignFile_HasNoForeignDirectory()
    {
        Touch("inlibrary.mp4");

        WatchingResult result = Resolver(new ProcessWindow("vlc", "foreign.mp4 - VLC media player"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.AreEqual(1, result.Diagnostics.UnresolvedPlayers.Count);
        Assert.IsNull(result.Diagnostics.UnresolvedPlayers[0].ForeignDirectory);
        Assert.AreEqual(0, result.Candidates.Count);
    }

    [TestMethod]
    public void Resolve_MixedResolvedAndForeign_KeepsCandidateAndReportsForeign()
    {
        string watched = Touch("watched.mp4");

        WatchingResult result = Resolver(
                new ProcessWindow("mpc-hc64", $"{watched} - MPC-HC"),
                new ProcessWindow("vlc", "foreign.mp4 - VLC media player"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.IsTrue(result.Candidates.Any(c => c.Name == "watched.mp4"), "resolved candidate must remain");
        Assert.AreEqual(1, result.Diagnostics.UnresolvedPlayers.Count, "foreign player still reported");
    }

    [TestMethod]
    public void Resolve_PlayerWithNoFilenameInTitle_StaysQuiet()
    {
        Touch("clip.mp4");

        WatchingResult result = Resolver(new ProcessWindow("vlc", "Some Metadata Title - VLC media player"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.AreEqual(0, result.Diagnostics.UnresolvedPlayers.Count, "no .mp4 in title is not a wrong-dir signal");
        Assert.IsTrue(result.Candidates.Count >= 1, "normal access-time fallback still answers");
    }

    [TestMethod]
    public void Resolve_MultipleResolvingPlayers_AllLow()
    {
        // Row 6: two players each resolve to a library clip → ambiguous → all low (never auto-tag).
        // b.mp4 is even locked, to show multi-player ambiguity dominates the bare-name lock rule.
        string a = Touch("a.mp4");
        string b = Touch("b.mp4");
        using var holdB = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read);

        IReadOnlyList<WatchingCandidate> result = Candidates(
            Resolver(
                new ProcessWindow("mpc-hc64", $"{a} - MPC-HC"),
                new ProcessWindow("vlc", "b.mp4 - VLC media player")),
            _tempDir, 5, true);

        Assert.AreEqual("low", result.Single(c => c.Name == "a.mp4").Confidence);
        Assert.AreEqual("low", result.Single(c => c.Name == "b.mp4").Confidence);
    }

    [TestMethod]
    public void Resolve_BareNameMatchesMultipleClips_AllLow()
    {
        // The canonical same-name-in-two-folders collision: a bare name resolving to >1 library
        // clip is ambiguous and must never be high.
        Touch("dup.mp4");
        string subdir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subdir);
        File.WriteAllBytes(Path.Combine(subdir, "dup.mp4"), Array.Empty<byte>());

        IReadOnlyList<WatchingCandidate> result = Candidates(
            Resolver(new ProcessWindow("vlc", "dup.mp4 - VLC media player")),
            _tempDir, 5, true);

        var dups = result.Where(c => c.Name == "dup.mp4").ToList();
        Assert.AreEqual(2, dups.Count);
        Assert.IsTrue(dups.All(c => c.Confidence == "low"));
    }

    [TestMethod]
    public void Resolve_SameForeignFileInTwoPlayers_DeduplicatesWarning()
    {
        Touch("inlibrary.mp4");

        WatchingResult result = Resolver(
                new ProcessWindow("vlc", "foreign.mp4 - VLC media player"),
                new ProcessWindow("vlc", "foreign.mp4 - VLC media player"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.AreEqual(1, result.Diagnostics.UnresolvedPlayers.Count,
            "identical foreign players must collapse to one warning entry");
    }
}
