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

    /// <summary>
    /// Touch, then back-date BOTH write time AND creation time well outside the recent-write window,
    /// so the file is a pure access-time fallback candidate (not a gaming-mode "just saved" clip).
    /// The predicate now keys on <see cref="LibraryClip.CreationTimeUtc"/>, so back-dating write time
    /// alone was no longer sufficient, the file's real creation time would still look fresh.
    /// </summary>
    private string TouchStale(string name)
    {
        string path = Touch(name);
        DateTime old = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(path, old);
        File.SetCreationTimeUtc(path, old);
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
        // Stale writes → these are access-time fallback candidates, not gaming-mode fresh saves.
        string older = TouchStale("older.mp4");
        string newer = TouchStale("newer.mp4");
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
    public void Resolve_HighWinnerPresent_SuppressesStaleAccessTimeRows()
    {
        // Spec A §6a (behavior CHANGE from pass-2): once a clip is positively identified by a
        // high-confidence player hit, the leftover access-time guesses are pure noise, the session
        // flagged them as such, so they are dropped from the result.
        string watched = Touch("watched.mp4");
        Touch("bystander.mp4");

        IReadOnlyList<WatchingCandidate> result = Candidates(
            Resolver(new ProcessWindow("mpc-hc64", $"{watched} - MPC-HC")),
            _tempDir, 5, true);

        WatchingCandidate high = result.Single(c => c.Name == "watched.mp4");
        Assert.AreEqual("high", high.Confidence);
        Assert.AreEqual(PlayerTitleSignal.SourceName, high.Source);
        Assert.IsFalse(result.Any(c => c.Source == AccessTimeSignal.SourceName),
            "stale access-time candidates are dropped beneath a high-confidence winner");
    }

    [TestMethod]
    public void Resolve_NoHighWinner_KeepsAccessTimeRows()
    {
        // No player hit and no fresh save → the access-time fallback is all we have, so it must answer.
        TouchStale("a.mp4");
        TouchStale("b.mp4");

        IReadOnlyList<WatchingCandidate> result = Candidates(Resolver(), _tempDir, 5, true);

        Assert.IsTrue(result.All(c => c.Source == AccessTimeSignal.SourceName));
        Assert.IsTrue(result.Count >= 2);
    }

    [TestMethod]
    public void Resolve_PlayerHit_AnyLiveTargetIsTrue()
    {
        string clip = Touch("clip.mp4");

        WatchingResult result = Resolver(new ProcessWindow("mpc-hc64", $"{clip} - MPC-HC"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.IsTrue(result.AnyLiveTarget);
    }

    [TestMethod]
    public void Resolve_LockedClipNoPlayer_AnyLiveTargetIsTrue()
    {
        string clip = Touch("clip.mp4");
        using var hold = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read);

        WatchingResult result = Resolver().Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.IsTrue(result.AnyLiveTarget, "a locked clip is a live target even with no player title");
    }

    [TestMethod]
    public void Resolve_NoPlayerNoLock_AnyLiveTargetIsFalse()
    {
        // Access-time candidates are still returned (a useful recency hint), but nothing is actually
        // open/locked, the caller must not auto-tag. AnyLiveTarget makes that an explicit contract.
        // (A stale write keeps this an access-time-only case; a FRESH save is a live target, see
        // Resolve_NoPlayer_SingleFreshWrite_IsLiveTarget.)
        TouchStale("a.mp4");

        WatchingResult result = Resolver().Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.IsTrue(result.Candidates.Count >= 1);
        Assert.IsFalse(result.AnyLiveTarget);
    }

    [TestMethod]
    public void Resolve_LockedClipSingleUnresolvedPlayer_AttributesThatPlayer()
    {
        // Spec A §6: a locked clip resolved only via access-time has no player from a title hit.
        // With exactly one open player whose title didn't resolve, attribute the lock to it.
        string clip = Touch("clip.mp4");
        using var hold = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read);

        WatchingCandidate c = Resolver(new ProcessWindow("vlc", "Some Embedded Metadata Title"))
            .Resolve(_tempDir, 5, includeAccessFallback: true)
            .Candidates.Single(x => x.Name == "clip.mp4");

        Assert.IsTrue(c.InUse);
        Assert.AreEqual("vlc", c.Player);
    }

    [TestMethod]
    public void Resolve_LockedClipTwoUnresolvedPlayers_LeavesPlayerNull()
    {
        string clip = Touch("clip.mp4");
        using var hold = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read);

        WatchingCandidate c = Resolver(
                new ProcessWindow("vlc", "Title A"),
                new ProcessWindow("mpc-hc64", "Title B"))
            .Resolve(_tempDir, 5, includeAccessFallback: true)
            .Candidates.Single(x => x.Name == "clip.mp4");

        Assert.IsNull(c.Player, "two open players is ambiguous, never guess which holds the lock");
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
        TouchStale("inlibrary.mp4"); // exists, not fresh, no gaming target, pure suppression case

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
        TouchStale("inlibrary.mp4");

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

    // ── Gaming mode: recent-write resolution (Policy A) ──────────────────────────────────

    [TestMethod]
    public void Resolve_NoPlayer_SingleFreshWrite_IsLiveTarget()
    {
        // The user clipped a game moment: one clip just saved, no player open. Policy A makes that
        // single fresh write a high-confidence, auto-taggable live target.
        string clip = Touch("clip.mp4"); // fresh write (default Touch leaves write time ~now)

        WatchingResult result = Resolver().Resolve(_tempDir, 5, includeAccessFallback: true);

        WatchingCandidate top = result.Candidates[0];
        Assert.AreEqual(clip, top.Path);
        Assert.AreEqual(RecentWriteSignal.SourceName, top.Source);
        Assert.AreEqual("high", top.Confidence);
        Assert.IsTrue(result.AnyLiveTarget, "a single just-saved clip is a live target (Policy A)");
    }

    [TestMethod]
    public void Resolve_NoPlayer_MultipleFreshWrites_AllLow_NotLive()
    {
        // Several clips saved at once is ambiguous, surface them but make the model confirm.
        Touch("a.mp4");
        Touch("b.mp4");

        WatchingResult result = Resolver().Resolve(_tempDir, 5, includeAccessFallback: true);

        var fresh = result.Candidates.Where(c => c.Source == RecentWriteSignal.SourceName).ToList();
        Assert.AreEqual(2, fresh.Count);
        Assert.IsTrue(fresh.All(c => c.Confidence == "low"));
        Assert.IsFalse(result.AnyLiveTarget, "multiple fresh saves are ambiguous, not an auto-tag target");
    }

    [TestMethod]
    public void Resolve_PlayerOpen_FreshWriteElsewhere_PlayerWins()
    {
        // A clip is playing AND another was just saved in the background, the played clip is the
        // subject; the background save must not displace it or even appear alongside the high winner.
        string watched = Touch("watched.mp4");
        Touch("autosaved.mp4"); // fresh, but nobody is watching it

        WatchingResult result = Resolver(new ProcessWindow("mpc-hc64", $"{watched} - MPC-HC"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.AreEqual(watched, result.Candidates[0].Path);
        Assert.AreEqual("high", result.Candidates[0].Confidence);
        Assert.IsFalse(result.Candidates.Any(c => c.Source == RecentWriteSignal.SourceName),
            "a background save is dropped beneath the high-confidence player winner");
    }

    [TestMethod]
    public void Resolve_NoPlayer_SingleFreshWrite_FallbackDisabled_ReturnsEmpty()
    {
        // include_access_fallback:false means "only open-player candidates", recent-write is a
        // no-player signal, so it is gated too. (Default is true, so gaming mode works out of the box.)
        Touch("clip.mp4");

        IReadOnlyList<WatchingCandidate> result = Candidates(Resolver(), _tempDir, 5, false);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Resolve_ForeignPlayer_SingleFreshSave_SurvivesSuppression()
    {
        // #1 (P0): a player is open on a file OUTSIDE the library AND one clip was just saved into
        // the library. The foreign lock and an in-library save are independent, the fresh save must
        // surface as the high-confidence gaming target, not be suppressed to zero candidates.
        string saved = Touch("saved.mp4"); // fresh creation time → a recent_write candidate

        WatchingResult result = Resolver(new ProcessWindow("vlc", @"D:\elsewhere\foreign.mp4 - VLC media player"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        WatchingCandidate top = result.Candidates.Single(c => c.Name == "saved.mp4");
        Assert.AreEqual(saved, top.Path);
        Assert.AreEqual(RecentWriteSignal.SourceName, top.Source);
        Assert.AreEqual("high", top.Confidence);
        Assert.IsTrue(result.AnyLiveTarget, "a single fresh save is a live target even with a foreign player open");
        Assert.AreEqual(1, result.Diagnostics.UnresolvedPlayers.Count, "the foreign player is still reported");
    }

    [TestMethod]
    public void Resolve_ForeignPlayer_MultipleFreshSaves_StaySuppressed()
    {
        // Several fresh saves at once is NOT Policy A, ambiguous, so the foreign-player suppression
        // still applies and nothing surfaces (the model must not auto-pick among them).
        Touch("one.mp4");
        Touch("two.mp4");

        WatchingResult result = Resolver(new ProcessWindow("vlc", @"D:\elsewhere\foreign.mp4 - VLC media player"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.AreEqual(0, result.Candidates.Count, "multiple fresh saves stay suppressed under a foreign player");
        Assert.AreEqual(1, result.Diagnostics.UnresolvedPlayers.Count);
    }

    // ── Ledger exclusion: self-written clips must not surface as gaming live targets ─────

    [TestMethod]
    public void Resolve_SingleFreshClip_SelfWritten_IsNotLiveTarget()
    {
        // A clip whose fresh creation time would normally make it a gaming-mode live target
        // must be excluded when the ledger records that ClipMeta itself wrote it (i.e. a
        // tag-write bumped the write time but ClipMeta stamped it, not a user game-save).
        string clip = Path.Combine(_tempDir, "fresh.mp4");
        File.WriteAllBytes(clip, new byte[] { 0, 1, 2 });   // fresh creation time

        var ledger = new SelfActionLedger();
        ledger.MarkWritten(clip);          // ClipMeta wrote it -> not a user save

        var resolver = WatchingResolver.CreateDefault(EmptyProcessWindowSource.Instance, ledger);
        WatchingResult result = resolver.Resolve(_tempDir, limit: 5, includeAccessFallback: true);

        Assert.IsFalse(result.Candidates.Any(c => c.Source == RecentWriteSignal.SourceName),
            "a self-written clip must not surface as a recent_write gaming target");
    }
}
