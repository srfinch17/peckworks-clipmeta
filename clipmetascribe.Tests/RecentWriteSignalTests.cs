using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Unit tests for <see cref="RecentWriteSignal"/> — the gaming-mode "clip just saved" signal. Timing
/// is driven by an injected clock and explicit write times so the tests are deterministic.
/// </summary>
[TestClass]
public class RecentWriteSignalTests
{
    private string _dir = null!;
    private static readonly DateTime Now = new(2026, 6, 26, 18, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Done() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>
    /// Creates an empty file, sets its write time, and optionally its creation time. When
    /// <paramref name="creationTimeUtc"/> is omitted the OS creation time is left as-is (actual
    /// wall-clock), which is fine for tests that already supply a synthetic <see cref="WatchContext"/>
    /// via <see cref="ContextWith"/>. Tests that resolve via <see cref="Ctx"/> must always supply it.
    /// </summary>
    private string Touch(string name, DateTime writeTimeUtc, DateTime? creationTimeUtc = null)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        File.SetLastWriteTimeUtc(path, writeTimeUtc);
        if (creationTimeUtc.HasValue)
            File.SetCreationTimeUtc(path, creationTimeUtc.Value);
        return path;
    }

    private WatchContext Ctx() => WatchContext.Build(_dir, Array.Empty<ProcessWindow>());

    private RecentWriteSignal Signal(TimeSpan? window = null) =>
        new(clock: () => Now, window: window);

    [TestMethod]
    public void Detect_SingleFreshWrite_OneUnambiguousHit()
    {
        // Creation time (the new predicate key) is set explicitly to match the frozen clock, so the
        // test is correct under the new contract rather than relying on wall-clock coincidence.
        string clip = Touch("clip.mp4", Now.AddSeconds(-3), creationTimeUtc: Now.AddSeconds(-3));

        List<SignalHit> hits = Signal().Detect(Ctx()).ToList();

        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(clip, hits[0].ClipPath);
        Assert.AreEqual(RecentWriteSignal.SourceName, hits[0].Source);
        Assert.IsFalse(hits[0].Ambiguous, "a single just-saved clip is the unambiguous gaming bind");
        Assert.IsNull(hits[0].Player);
    }

    [TestMethod]
    public void Detect_TwoFreshWrites_BothAmbiguous_NewestFirst()
    {
        // Creation times drive both detection and ordering under the new predicate; both must be
        // within the 5-minute window, and the newer creation time should sort first.
        string older = Touch("older.mp4", Now.AddSeconds(-60), creationTimeUtc: Now.AddSeconds(-60));
        string newer = Touch("newer.mp4", Now.AddSeconds(-2), creationTimeUtc: Now.AddSeconds(-2));

        List<SignalHit> hits = Signal().Detect(Ctx()).ToList();

        Assert.AreEqual(2, hits.Count);
        Assert.AreEqual(newer, hits[0].ClipPath, "newest creation first");
        Assert.AreEqual(older, hits[1].ClipPath);
        Assert.IsTrue(hits.All(h => h.Ambiguous), "several saved at once → ambiguous");
    }

    [TestMethod]
    public void Detect_WriteOutsideWindow_NoHit()
    {
        // Creation time (the new predicate key) is set outside the window; write time is irrelevant.
        Touch("stale.mp4", Now.AddMinutes(-30), creationTimeUtc: Now.AddMinutes(-30));

        Assert.AreEqual(0, Signal().Detect(Ctx()).Count(),
            "a clip whose creation time is outside the window is not a 'just saved' signal");
    }

    [TestMethod]
    public void Detect_FutureWriteTime_Ignored()
    {
        // Clock skew could stamp a creation slightly in the future; it must not count as "elapsed".
        // now - c.CreationTimeUtc is negative → the >= TimeSpan.Zero guard rejects it.
        Touch("future.mp4", Now.AddMinutes(5), creationTimeUtc: Now.AddMinutes(5));

        Assert.AreEqual(0, Signal().Detect(Ctx()).Count());
    }

    [TestMethod]
    public void Detect_CustomWindow_Respected()
    {
        // Creation time set to -90s: outside the 30s window but inside the 2-minute window.
        Touch("clip.mp4", Now.AddSeconds(-90), creationTimeUtc: Now.AddSeconds(-90));

        Assert.AreEqual(0, Signal(window: TimeSpan.FromSeconds(30)).Detect(Ctx()).Count(),
            "outside a tight 30s window");
        Assert.AreEqual(1, Signal(window: TimeSpan.FromMinutes(2)).Detect(Ctx()).Count(),
            "inside a 2-minute window");
    }

    // ── Creation-time + baseline + self-ledger predicate (Task 4) ────────────────────────────

    [TestMethod]
    public void NewClip_FreshCreation_OldWriteTime_IsDetected()
    {
        DateTime now = new(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc);
        var clip = new LibraryClip(
            @"C:\lib\new.mp4", "new.mp4",
            LastAccessTimeUtc: now.AddDays(-10),
            LastWriteTimeUtc: now.AddDays(-10),   // copy preserved an OLD mtime
            CreationTimeUtc: now.AddMinutes(-1));  // but it just appeared in the folder
        var ctx = ContextWith(new[] { clip }, baseline: Array.Empty<string>(), ledger: null);

        var hits = new RecentWriteSignal(() => now).Detect(ctx).ToList();

        Assert.AreEqual(1, hits.Count);
        Assert.IsFalse(hits[0].Ambiguous);
    }

    [TestMethod]
    public void KnownBaselinePath_IsExcluded()
    {
        DateTime now = new(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc);
        var clip = new LibraryClip(@"C:\lib\old.mp4", "old.mp4", now, now, now.AddMinutes(-1));
        var ctx = ContextWith(new[] { clip }, baseline: new[] { @"C:\lib\old.mp4" }, ledger: null);

        Assert.AreEqual(0, new RecentWriteSignal(() => now).Detect(ctx).Count());
    }

    [TestMethod]
    public void SelfWrittenPath_IsExcluded()
    {
        DateTime now = new(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc);
        var ledger = new SelfActionLedger(() => new DateTimeOffset(now));
        ledger.MarkWritten(@"C:\lib\tagged.mp4");
        var clip = new LibraryClip(@"C:\lib\tagged.mp4", "tagged.mp4", now, now, now.AddMinutes(-1));
        var ctx = ContextWith(new[] { clip }, baseline: Array.Empty<string>(), ledger: ledger);

        Assert.AreEqual(0, new RecentWriteSignal(() => now).Detect(ctx).Count());
    }

    /// <summary>
    /// Builds a <see cref="WatchContext"/> from a synthetic clip list, empty baseline, and optional
    /// ledger — for unit tests that need full predicate control without touching the filesystem.
    /// </summary>
    private static WatchContext ContextWith(
        IReadOnlyList<LibraryClip> clips, IEnumerable<string> baseline, SelfActionLedger? ledger)
    {
        var byName = clips.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LibraryClip>)g.ToList(), StringComparer.OrdinalIgnoreCase);
        var byPath = clips.ToDictionary(c => c.FullPath, c => c, StringComparer.OrdinalIgnoreCase);
        return new WatchContext
        {
            LibraryClips = clips,
            ByFileName = byName,
            ByFullPath = byPath,
            PlayerWindows = Array.Empty<ProcessWindow>(),
            KnownBaselinePaths = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase),
            Ledger = ledger,
        };
    }
}
