# Review-Mode Watcher Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the watch-and-tag binding race, a continuous, read-only watcher records player-title segments over time so a spoken tag binds to the clip the user was *actually* watching, not whatever the player advanced to by the time the tool runs.

**Architecture:** A Core `ReviewWatcher` background thread polls window titles (~250 ms) into a bounded ring buffer of `TitleSegment`s. A pure `ReviewBindingResolver` applies the "ignore-just-started → previous-stable" rule. `WatchingResolver.ResolveReview` reuses the existing resolution pipeline over the chosen title and promotes the corrected bind. The MCP shell wires lifecycle like `QueueDrainPump` and surfaces inline `review[]` flags. No new MCP tool.

**Tech Stack:** C# / .NET 10, BCL only (`System.Threading` for the loop). MSTest. Existing `clipmeta.core/Watching` types.

## Global Constraints

- **Zero external NuGet in production projects** (`clipmeta.core`, `clipmetamcp`). MSTest only in test projects.
- **Thin-shell rule:** no business logic in `clipmetamcp`; resolution/heuristic logic lives in `clipmeta.core`.
- **MCP/CLI surface change ⇒ run the FULL test project, not a `--filter`** (`ToolsList_ContainsTheFullToolSurface` lives outside any diff).
- **Build gate:** `dotnet build --nologo -v q` must be 0 warnings, 0 errors.
- **Testable seams:** OS/clock dependencies injected (`IProcessWindowSource`, `Func<DateTimeOffset>`); shell builds real deps as trailing optional injectables defaulting to the real impl.
- **XML doc comments on all new public types/methods; named constants, no magic numbers.**
- Spec: `docs/superpowers/specs/2026-06-26-review-mode-watcher-design.md`.

---

## File Structure

**Create (Core):**
- `clipmeta.core/Watching/TitleSegment.cs`, the segment record.
- `clipmeta.core/Watching/ReviewFlag.cs`, review flag record.
- `clipmeta.core/Watching/ReviewBinding.cs`, heuristic output record.
- `clipmeta.core/Watching/ReviewBindingResolver.cs`, pure previous-stable rule + flag derivation.
- `clipmeta.core/Watching/ReviewWatcher.cs`, background title poller.

**Modify (Core):**
- `clipmeta.core/Watching/WatchContext.cs`, add `Build` overload taking supplied windows.
- `clipmeta.core/Watching/WatchingResolver.cs`, extract `ResolveCore`; add `ResolveReview`; not-locked exception; `anyLiveTarget` extension.
- `clipmeta.core/Watching/WatchingResult.cs`, add `Review`, `BoundSegmentId`, `RecommendationConfident` (additive).

**Modify (MCP shell):**
- `clipmetamcp/Tools/ReadTools.cs`, `library_watching` takes the watcher (trailing injectable); calls `ResolveReview`; emits `review[]`; `MarkBound`; description update.
- `clipmetamcp/Program.cs`, construct/Start/Dispose the `ReviewWatcher`.

**Create (tests):**
- `clipmetascribe.Tests/ReviewBindingResolverTests.cs`
- `clipmetascribe.Tests/ReviewWatcherTests.cs`
- `clipmetascribe.Tests/ResolveReviewTests.cs`

**Modify (tests):**
- `clipmetamcp.Tests/LibraryWatchingToolTests.cs`, add `review[]` / watcher-fallback assertions.

**Docs:**
- `docs/PITFALLS.md`, record the poll-at-call-time race and the not-locked-guard exception.

---

## Task 1: Heuristic types + `ReviewBindingResolver` (pure)

**Files:**
- Create: `clipmeta.core/Watching/TitleSegment.cs`, `ReviewFlag.cs`, `ReviewBinding.cs`, `ReviewBindingResolver.cs`
- Test: `clipmetascribe.Tests/ReviewBindingResolverTests.cs`

**Interfaces:**
- Produces:
  - `record TitleSegment(long Id, string ProcessName, string RawTitle, DateTimeOffset StartedAt, DateTimeOffset? EndedAt)`
  - `record ReviewFlag(string Type, IReadOnlyList<string> Clips, double StableSeconds = 0)` with const Type values.
  - `record ReviewBinding(TitleSegment? Chosen, TitleSegment? CorrectedFrom, double StableSeconds, bool AmbiguousMultiPlayer, IReadOnlyList<ReviewFlag> Flags)`
  - `static ReviewBinding ReviewBindingResolver.Resolve(IReadOnlyList<TitleSegment> segments, long lastBoundId, DateTimeOffset now, TimeSpan? stableThreshold = null)`

- [ ] **Step 1: Write the segment + flag + binding records**

Create `clipmeta.core/Watching/TitleSegment.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// One uninterrupted span during which a media player showed a single window title. The watcher
/// records these over time so a tag can bind to the title that was actually playing at the user's
/// dictation moment, not whatever the player advanced to by the time the tool runs.
/// </summary>
/// <param name="Id">Monotonic id assigned when the segment opens (enables cross-call bind tracking).</param>
/// <param name="ProcessName">The player process the title came from.</param>
/// <param name="RawTitle">The raw window title (resolved to a clip later, at call time).</param>
/// <param name="StartedAt">When this title first appeared.</param>
/// <param name="EndedAt">When it changed/closed; null while it is still the current title.</param>
public sealed record TitleSegment(
    long Id, string ProcessName, string RawTitle, DateTimeOffset StartedAt, DateTimeOffset? EndedAt)
{
    /// <summary>How long this segment played, measured to <paramref name="now"/> when still open.</summary>
    public double DurationSeconds(DateTimeOffset now) =>
        Math.Max(0, ((EndedAt ?? now) - StartedAt).TotalSeconds);
}
```

Create `clipmeta.core/Watching/ReviewFlag.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// A non-blocking advisory about a binding the user may want to reconcile later: an auto-correction,
/// a clip tagged twice, a skipped clip, or too many players open to bind safely. Surfaced inline on
/// the library_watching response; never interrupts the run.
/// </summary>
/// <param name="Type">One of the <c>Type*</c> constants.</param>
/// <param name="Clips">Display names/titles the flag refers to (e.g. the bound + corrected-from clip).</param>
/// <param name="StableSeconds">For <see cref="TypeAutoCorrected"/>: how long the bound clip had played.</param>
public sealed record ReviewFlag(string Type, IReadOnlyList<string> Clips, double StableSeconds = 0)
{
    /// <summary>Bound the previous stable clip because the open one had only just started.</summary>
    public const string TypeAutoCorrected = "autoCorrected";

    /// <summary>This resolution targets the same clip the previous one did (player did not advance).</summary>
    public const string TypeSameClipTwice = "sameClipTwice";

    /// <summary>Stable clips played between the last bind and this one were never tagged.</summary>
    public const string TypeSequenceSkip = "sequenceSkip";

    /// <summary>More than one player is active, too ambiguous to bind a clip safely.</summary>
    public const string TypeMultiplePlayersActive = "multiplePlayersActive";
}
```

Create `clipmeta.core/Watching/ReviewBinding.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// The pure heuristic's decision about which recorded title the user is describing. <see cref="Chosen"/>
/// null means "no correction to offer" (cold start, or ambiguous multi-player), the caller falls back
/// to a live poll.
/// </summary>
/// <param name="Chosen">Segment whose title to resolve to a clip, or null.</param>
/// <param name="CorrectedFrom">Set when the previous-stable segment was chosen over a just-started one.</param>
/// <param name="StableSeconds">How long <see cref="Chosen"/> played (0 when null).</param>
/// <param name="AmbiguousMultiPlayer">True when 2+ players were active, refuse correction, warn.</param>
/// <param name="Flags">Review advisories derived from the segment sequence.</param>
public sealed record ReviewBinding(
    TitleSegment? Chosen,
    TitleSegment? CorrectedFrom,
    double StableSeconds,
    bool AmbiguousMultiPlayer,
    IReadOnlyList<ReviewFlag> Flags);
```

- [ ] **Step 2: Write the failing tests**

Create `clipmetascribe.Tests/ReviewBindingResolverTests.cs`:

```csharp
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
}
```

- [ ] **Step 3: Run the tests, verify they FAIL to compile/run**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter ReviewBindingResolverTests`
Expected: FAIL, `ReviewBindingResolver` does not exist.

- [ ] **Step 4: Implement `ReviewBindingResolver`**

Create `clipmeta.core/Watching/ReviewBindingResolver.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// Pure heuristic that decides which recorded title the user is describing. The core rule: if the
/// currently-open clip only JUST started (under <see cref="DefaultStableThreshold"/>), the user has
/// already advanced and is describing the PREVIOUS clip they actually watched, so bind that instead.
/// This is the entire fix for the poll-at-call-time binding race; it depends only on segment timing,
/// never on when the tool happened to be called.
/// </summary>
public static class ReviewBindingResolver
{
    /// <summary>A clip open for less than this is treated as "just advanced to", not the subject.</summary>
    public static readonly TimeSpan DefaultStableThreshold = TimeSpan.FromSeconds(2);

    /// <summary>Applies the previous-stable rule and derives review flags from the segment sequence.</summary>
    /// <param name="segments">Title segments, any order (sorted internally by start time).</param>
    /// <param name="lastBoundId">Id of the segment the previous resolution recommended, or -1.</param>
    /// <param name="now">Current time (injected for testability).</param>
    /// <param name="stableThreshold">Override the just-started threshold (tests).</param>
    public static ReviewBinding Resolve(
        IReadOnlyList<TitleSegment> segments, long lastBoundId, DateTimeOffset now,
        TimeSpan? stableThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        TimeSpan threshold = stableThreshold ?? DefaultStableThreshold;

        if (segments.Count == 0)
            return new ReviewBinding(null, null, 0, false, Array.Empty<ReviewFlag>());

        List<TitleSegment> ordered = segments.OrderBy(s => s.StartedAt).ToList();
        TitleSegment current = ordered[^1];

        // Ambiguity: another player produced a segment within the threshold window of `current`.
        bool multiPlayer = ordered.Any(s =>
            !string.Equals(s.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            (current.StartedAt - s.StartedAt).Duration() <= threshold &&
            s.EndedAt is null);
        if (multiPlayer)
            return new ReviewBinding(
                null, null, 0, true,
                new[] { new ReviewFlag(ReviewFlag.TypeMultiplePlayersActive, NamesOf(ordered.Where(s => s.EndedAt is null))) });

        // Previous-stable correction.
        TitleSegment chosen = current;
        TitleSegment? correctedFrom = null;
        if (current.DurationSeconds(now) < threshold.TotalSeconds && ordered.Count >= 2)
        {
            TitleSegment prior = ordered[^2];
            if (prior.DurationSeconds(now) >= threshold.TotalSeconds)
            {
                chosen = prior;
                correctedFrom = current;
            }
        }

        var flags = new List<ReviewFlag>();
        double stable = chosen.DurationSeconds(now);
        if (correctedFrom is not null)
            flags.Add(new ReviewFlag(
                ReviewFlag.TypeAutoCorrected, new[] { Display(chosen), Display(correctedFrom) }, stable));

        if (chosen.Id == lastBoundId)
            flags.Add(new ReviewFlag(ReviewFlag.TypeSameClipTwice, new[] { Display(chosen) }));

        // Skip: stable, never-bound segments strictly between the last bind and the chosen one.
        if (lastBoundId >= 0)
        {
            List<string> skipped = ordered
                .Where(s => s.Id > lastBoundId && s.Id < chosen.Id && s.DurationSeconds(now) >= threshold.TotalSeconds)
                .Select(Display).ToList();
            if (skipped.Count > 0)
                flags.Add(new ReviewFlag(ReviewFlag.TypeSequenceSkip, skipped));
        }

        return new ReviewBinding(chosen, correctedFrom, stable, false, flags);
    }

    private static IReadOnlyList<string> NamesOf(IEnumerable<TitleSegment> segs) =>
        segs.Select(Display).ToList();

    /// <summary>Best display string for a segment, its raw title (which contains the file name).</summary>
    private static string Display(TitleSegment s) => s.RawTitle;
}
```

- [ ] **Step 5: Run the tests, verify they PASS**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter ReviewBindingResolverTests`
Expected: PASS (6/6).

- [ ] **Step 6: Commit**

```bash
git add clipmeta.core/Watching/TitleSegment.cs clipmeta.core/Watching/ReviewFlag.cs clipmeta.core/Watching/ReviewBinding.cs clipmeta.core/Watching/ReviewBindingResolver.cs clipmetascribe.Tests/ReviewBindingResolverTests.cs
git commit -m "feat(watching): pure ReviewBindingResolver (previous-stable rule) + segment types"
```

---

## Task 2: `ReviewWatcher` background poller

**Files:**
- Create: `clipmeta.core/Watching/ReviewWatcher.cs`
- Test: `clipmetascribe.Tests/ReviewWatcherTests.cs`

**Interfaces:**
- Consumes: `TitleSegment` (Task 1); `IProcessWindowSource`, `ProcessWindow`, `MediaPlayers.KnownProcessNames` (existing).
- Produces: `ReviewWatcher` with `Start()`, `IReadOnlyList<TitleSegment> Snapshot()`, `long LastBoundId`, `void MarkBound(long)`, `void PollOnce()` (internal, for tests), `Dispose()`.

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/ReviewWatcherTests.cs`:

```csharp
using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ReviewWatcherTests
{
    /// <summary>A source whose returned windows can be swapped between polls.</summary>
    private sealed class MutableSource : IProcessWindowSource
    {
        public volatile IReadOnlyList<ProcessWindow> Windows = Array.Empty<ProcessWindow>();
        public bool Throw;
        public IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> names)
        {
            if (Throw) throw new InvalidOperationException("boom");
            return Windows;
        }
    }

    private DateTimeOffset _now;
    private ReviewWatcher Make(MutableSource src) =>
        new(src, () => _now, TimeSpan.FromMilliseconds(10));

    [TestInitialize]
    public void Init() => _now = new DateTimeOffset(2026, 6, 26, 6, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void PollOnce_TitleChange_OpensAndClosesSegments()
    {
        var src = new MutableSource();
        using var w = Make(src);

        src.Windows = new[] { new ProcessWindow("vlc", "_1.mp4 - VLC media player") };
        w.PollOnce();
        _now = _now.AddSeconds(5);
        src.Windows = new[] { new ProcessWindow("vlc", "_2.mp4 - VLC media player") };
        w.PollOnce();

        IReadOnlyList<TitleSegment> segs = w.Snapshot();
        Assert.AreEqual(2, segs.Count);
        Assert.IsNotNull(segs[0].EndedAt, "first segment should be closed when the title changed");
        Assert.IsNull(segs[1].EndedAt, "current segment stays open");
        Assert.IsTrue(segs[1].Id > segs[0].Id, "ids are monotonic");
    }

    [TestMethod]
    public void PollOnce_PlayerVanished_ClosesOpenSegment()
    {
        var src = new MutableSource { Windows = new[] { new ProcessWindow("vlc", "_1.mp4 - VLC media player") } };
        using var w = Make(src);
        w.PollOnce();
        _now = _now.AddSeconds(3);
        src.Windows = Array.Empty<ProcessWindow>();
        w.PollOnce();

        Assert.IsNotNull(w.Snapshot()[0].EndedAt);
    }

    [TestMethod]
    public void PollOnce_SameTitle_DoesNotOpenNewSegment()
    {
        var src = new MutableSource { Windows = new[] { new ProcessWindow("vlc", "_1.mp4 - VLC media player") } };
        using var w = Make(src);
        w.PollOnce();
        w.PollOnce();
        Assert.AreEqual(1, w.Snapshot().Count);
    }

    [TestMethod]
    public void PollOnce_ThrowingSource_IsSwallowed()
    {
        var src = new MutableSource { Throw = true };
        using var w = Make(src);
        w.PollOnce(); // must not throw
        Assert.AreEqual(0, w.Snapshot().Count);
    }

    [TestMethod]
    public void RingBuffer_CapsSegmentCount()
    {
        var src = new MutableSource();
        using var w = new ReviewWatcher(src, () => _now, TimeSpan.FromMilliseconds(10), maxSegments: 3);
        for (int i = 0; i < 6; i++)
        {
            src.Windows = new[] { new ProcessWindow("vlc", $"clip{i}.mp4 - VLC media player") };
            _now = _now.AddSeconds(5);
            w.PollOnce();
        }
        Assert.IsTrue(w.Snapshot().Count <= 3);
    }

    [TestMethod]
    public void MarkBound_RecordsLastBoundId()
    {
        using var w = Make(new MutableSource());
        w.MarkBound(42);
        Assert.AreEqual(42, w.LastBoundId);
    }

    [TestMethod]
    public void Snapshot_IsIsolatedCopy()
    {
        var src = new MutableSource { Windows = new[] { new ProcessWindow("vlc", "_1.mp4 - VLC media player") } };
        using var w = Make(src);
        w.PollOnce();
        IReadOnlyList<TitleSegment> first = w.Snapshot();
        src.Windows = new[] { new ProcessWindow("vlc", "_2.mp4 - VLC media player") };
        _now = _now.AddSeconds(5);
        w.PollOnce();
        Assert.AreEqual(1, first.Count, "an earlier snapshot must not mutate");
    }
}
```

- [ ] **Step 2: Run the tests, verify they FAIL**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter ReviewWatcherTests`
Expected: FAIL, `ReviewWatcher` does not exist.

- [ ] **Step 3: Implement `ReviewWatcher`**

Create `clipmeta.core/Watching/ReviewWatcher.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// Read-only background driver for review-mode tagging. It polls the open media players' window
/// titles on a timer and records a <see cref="TitleSegment"/> each time the active title changes, so
/// <c>library_watching</c> can resolve a tag against what was PLAYING at the user's dictation moment
/// rather than a fresh "what's open now?" snapshot taken a turn later (the binding race). The hot loop
/// only reads titles, no library enumeration, no MP4 work, and never writes a file, so it cannot
/// race any writer. Mirrors the <see cref="QueueDrainPump"/> thread/dispose pattern.
/// </summary>
public sealed class ReviewWatcher : IDisposable
{
    private readonly IProcessWindowSource _windowSource;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _pollInterval;
    private readonly IReadOnlyCollection<string> _playerNames;
    private readonly int _maxSegments;

    private readonly object _gate = new();
    private readonly List<TitleSegment> _segments = new();
    private readonly Dictionary<string, long> _openByProcess = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId = 1;
    private long _lastBoundId = -1;

    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private bool _disposed;

    /// <summary>Creates a watcher over the given OS window source and clock.</summary>
    /// <param name="windowSource">Player-window source (production: <c>ProcessWindowSource.ForCurrentPlatform()</c>).</param>
    /// <param name="clock">Now-provider (injected for tests).</param>
    /// <param name="pollInterval">Time between polls (production: ~250ms).</param>
    /// <param name="playerNames">Recognized players (default <see cref="MediaPlayers.KnownProcessNames"/>).</param>
    /// <param name="maxSegments">Ring-buffer cap; oldest dropped past this.</param>
    public ReviewWatcher(
        IProcessWindowSource windowSource, Func<DateTimeOffset> clock, TimeSpan pollInterval,
        IReadOnlyCollection<string>? playerNames = null, int maxSegments = 64)
    {
        _windowSource = windowSource ?? throw new ArgumentNullException(nameof(windowSource));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _pollInterval = pollInterval;
        _playerNames = playerNames ?? MediaPlayers.KnownProcessNames;
        _maxSegments = Math.Max(2, maxSegments);
    }

    /// <summary>Id of the segment the last confident resolution recommended, or -1.</summary>
    public long LastBoundId { get { lock (_gate) return _lastBoundId; } }

    /// <summary>Records that <paramref name="segmentId"/> was the last recommended bind.</summary>
    public void MarkBound(long segmentId) { lock (_gate) _lastBoundId = segmentId; }

    /// <summary>A consistent copy of the current segment ring buffer.</summary>
    public IReadOnlyList<TitleSegment> Snapshot() { lock (_gate) return _segments.ToList(); }

    /// <summary>Launches the polling loop. Idempotent.</summary>
    public void Start()
    {
        if (_thread is not null || _disposed) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "clipmeta-review-watcher" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            PollOnce();
            if (_cts.Token.WaitHandle.WaitOne(_pollInterval)) return;
        }
    }

    /// <summary>
    /// One poll: open a new segment for any player whose title changed, close the segment of any
    /// player that vanished or changed title. Never throws, a flaky OS read just skips this tick.
    /// Internal so tests drive it deterministically without the timer.
    /// </summary>
    internal void PollOnce()
    {
        IReadOnlyList<ProcessWindow> windows;
        try { windows = _windowSource.GetPlayerWindows(_playerNames); }
        catch { return; }

        DateTimeOffset now = _clock();
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessWindow w in windows)
            {
                seen.Add(w.ProcessName);
                if (_openByProcess.TryGetValue(w.ProcessName, out long openId))
                {
                    TitleSegment open = _segments.First(s => s.Id == openId);
                    if (string.Equals(open.RawTitle, w.WindowTitle, StringComparison.Ordinal))
                        continue; // unchanged
                    CloseSegment(openId, now);
                }
                OpenSegment(w.ProcessName, w.WindowTitle, now);
            }

            // Players that disappeared close their open segment.
            foreach ((string proc, long id) in _openByProcess.Where(kv => !seen.Contains(kv.Key)).ToList())
                CloseSegment(id, now);

            Trim();
        }
    }

    private void OpenSegment(string proc, string title, DateTimeOffset now)
    {
        long id = _nextId++;
        _segments.Add(new TitleSegment(id, proc, title, now, null));
        _openByProcess[proc] = id;
    }

    private void CloseSegment(long id, DateTimeOffset now)
    {
        int idx = _segments.FindIndex(s => s.Id == id);
        if (idx >= 0) _segments[idx] = _segments[idx] with { EndedAt = now };
        string? proc = _openByProcess.FirstOrDefault(kv => kv.Value == id).Key;
        if (proc is not null) _openByProcess.Remove(proc);
    }

    private void Trim()
    {
        while (_segments.Count > _maxSegments)
        {
            // Never drop a segment that is still open (referenced in _openByProcess).
            int removable = _segments.FindIndex(s => s.EndedAt is not null);
            if (removable < 0) break;
            _segments.RemoveAt(removable);
        }
    }

    /// <summary>Stops the loop and joins the thread. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
```

- [ ] **Step 4: Run the tests, verify they PASS**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter ReviewWatcherTests`
Expected: PASS (7/7).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/ReviewWatcher.cs clipmetascribe.Tests/ReviewWatcherTests.cs
git commit -m "feat(watching): ReviewWatcher background title-segment poller"
```

---

## Task 3: `WatchContext` overload + extract `ResolveCore` (pure refactor)

This is a behavior-preserving refactor so `ResolveReview` (Task 4) can run the pipeline over a supplied title. Existing `WatchingResolverTests` must stay green, that *is* the test.

**Files:**
- Modify: `clipmeta.core/Watching/WatchContext.cs`
- Modify: `clipmeta.core/Watching/WatchingResolver.cs`

**Interfaces:**
- Produces:
  - `static WatchContext WatchContext.Build(string libraryRoot, IReadOnlyList<ProcessWindow> playerWindows)`
  - `private WatchingResult WatchingResolver.ResolveCore(WatchContext context, int limit, bool includeAccessFallback)`

- [ ] **Step 1: Add the `WatchContext.Build` overload**

In `clipmeta.core/Watching/WatchContext.cs`, add alongside the existing `Build` (which calls `source.GetPlayerWindows`). Extract the library-enumeration half so both share it:

```csharp
/// <summary>
/// Builds a context over supplied player windows instead of polling a source, used by review-mode
/// resolution, which has already chosen WHICH title to resolve from the watcher's segment history.
/// </summary>
public static WatchContext Build(string libraryRoot, IReadOnlyList<ProcessWindow> playerWindows)
{
    ArgumentNullException.ThrowIfNull(playerWindows);
    (List<LibraryClip> clips, var byName, var byPath) = EnumerateLibrary(libraryRoot);
    return new WatchContext
    {
        LibraryClips = clips,
        ByFileName = byName,
        ByFullPath = byPath,
        PlayerWindows = playerWindows,
    };
}
```

Refactor the existing `Build(libraryRoot, source, playerNames)` to call a shared private
`EnumerateLibrary(libraryRoot)` that returns `(List<LibraryClip>, IReadOnlyDictionary<string, IReadOnlyList<LibraryClip>>, IReadOnlyDictionary<string, LibraryClip>)`, move the enumeration + dictionary-building loop (current lines 29–60) into it verbatim, and have the original `Build` set `PlayerWindows = source.GetPlayerWindows(playerNames)`.

- [ ] **Step 2: Extract `ResolveCore` in `WatchingResolver`**

In `clipmeta.core/Watching/WatchingResolver.cs`, change `Resolve` to build the context then delegate:

```csharp
public WatchingResult Resolve(string libraryRoot, int limit, bool includeAccessFallback)
{
    WatchContext context = WatchContext.Build(libraryRoot, _windowSource, _playerNames);
    return ResolveCore(context, limit, includeAccessFallback);
}

/// <summary>Resolves over an already-built context (live snapshot or a review-chosen title).</summary>
private WatchingResult ResolveCore(WatchContext context, int limit, bool includeAccessFallback)
{
    // ... the entire current body of Resolve from the line after WatchContext.Build to the return ...
}
```

Move the existing body (everything after the old `WatchContext.Build` line through `return new WatchingResult(...)`) into `ResolveCore` unchanged.

- [ ] **Step 3: Build + run the existing watching suite, verify NO behavior change**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors.
Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter WatchingResolverTests`
Expected: PASS (all existing tests green, the refactor changed no behavior).

- [ ] **Step 4: Commit**

```bash
git add clipmeta.core/Watching/WatchContext.cs clipmeta.core/Watching/WatchingResolver.cs
git commit -m "refactor(watching): WatchContext windows-overload + extract ResolveCore"
```

---

## Task 4: `ResolveReview` + `WatchingResult` additions + corrected-bind promotion

**Files:**
- Modify: `clipmeta.core/Watching/WatchingResult.cs`
- Modify: `clipmeta.core/Watching/WatchingResolver.cs`
- Test: `clipmetascribe.Tests/ResolveReviewTests.cs`

**Interfaces:**
- Consumes: `ReviewBindingResolver.Resolve` (Task 1); `TitleSegment` (Task 1); `WatchContext.Build(root, windows)`, `ResolveCore` (Task 3).
- Produces:
  - `record WatchingResult(... existing ..., IReadOnlyList<ReviewFlag>? Review = null, long? BoundSegmentId = null, bool RecommendationConfident = false)`
  - `WatchingResult WatchingResolver.ResolveReview(string libraryRoot, IReadOnlyList<TitleSegment> segments, long lastBoundId, DateTimeOffset now, int limit, bool includeAccessFallback)`

- [ ] **Step 1: Extend `WatchingResult` (additive)**

In `clipmeta.core/Watching/WatchingResult.cs`:

```csharp
public sealed record WatchingResult(
    IReadOnlyList<WatchingCandidate> Candidates,
    WatchDiagnostics Diagnostics,
    bool AnyLiveTarget,
    IReadOnlyList<ReviewFlag>? Review = null,
    long? BoundSegmentId = null,
    bool RecommendationConfident = false);
```

Update the XML doc to describe the three new members (review flags; the segment id the shell should `MarkBound`; whether the top recommendation was a confident single match).

- [ ] **Step 2: Write the failing integration tests**

Create `clipmetascribe.Tests/ResolveReviewTests.cs`:

```csharp
using ClipMetaCore.Watching;

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
```

- [ ] **Step 3: Run the tests, verify they FAIL**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter ResolveReviewTests`
Expected: FAIL, `ResolveReview` does not exist.

- [ ] **Step 4: Implement `ResolveReview`**

In `clipmeta.core/Watching/WatchingResolver.cs`, add:

```csharp
/// <summary>The note attached to a clip bound by the previous-stable correction.</summary>
private const string CorrectedBindNote =
    "bound the clip you were watching; the player has since advanced (so it is now writable)";

/// <summary>
/// Review-mode resolution: pick which recorded title the user is describing (the previous-stable
/// heuristic), resolve it through the normal pipeline, and promote that bind. Falls back to a live
/// cold-start poll when there is no segment history or the situation is ambiguous.
/// </summary>
public WatchingResult ResolveReview(
    string libraryRoot, IReadOnlyList<TitleSegment> segments, long lastBoundId,
    DateTimeOffset now, int limit, bool includeAccessFallback)
{
    ReviewBinding binding = ReviewBindingResolver.Resolve(segments, lastBoundId, now);

    // Which windows to resolve: the single chosen title, else the live windows (cold start /
    // ambiguous) so the existing pipeline produces its normal candidates + diagnostics.
    IReadOnlyList<ProcessWindow> windows = binding.Chosen is { } chosen
        ? new[] { new ProcessWindow(chosen.ProcessName, chosen.RawTitle) }
        : _windowSource.GetPlayerWindows(_playerNames);

    WatchContext context = WatchContext.Build(libraryRoot, windows);
    WatchingResult core = ResolveCore(context, limit, includeAccessFallback);

    List<WatchingCandidate> candidates = core.Candidates.ToList();
    bool confident = false;
    long? boundId = null;

    if (binding.Chosen is { } sel)
    {
        // The chosen title resolves to exactly the candidates the pipeline produced for that window.
        // Promote a single player-title match past the not-locked demotion (it is expected to be
        // unlocked, the user advanced away from it), keeping its true lock state for reporting.
        int idx = candidates.FindIndex(c => c.Source == PlayerTitleSignal.SourceName);
        bool singleMatch = candidates.Count(c => c.Source == PlayerTitleSignal.SourceName) == 1;
        if (idx >= 0 && singleMatch)
        {
            candidates[idx] = candidates[idx] with
            {
                Confidence = HighConfidence,
                Note = binding.CorrectedFrom is null ? candidates[idx].Note : CorrectedBindNote,
            };
            confident = true;
            boundId = sel.Id;
        }
    }

    // A corrected/confident bind is a live target even when unlocked.
    bool anyLive = core.AnyLiveTarget || confident;

    return new WatchingResult(
        candidates, core.Diagnostics, anyLive, binding.Flags, boundId, confident);
}
```

- [ ] **Step 5: Run the tests, verify they PASS**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter ResolveReviewTests`
Expected: PASS (3/3).

- [ ] **Step 6: Run the full Core/scribe watching suite, verify no regressions**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter Watching`
Expected: PASS (`WatchingResolverTests`, `ReviewBindingResolverTests`, `ReviewWatcherTests`, `ResolveReviewTests`).

- [ ] **Step 7: Commit**

```bash
git add clipmeta.core/Watching/WatchingResult.cs clipmeta.core/Watching/WatchingResolver.cs clipmetascribe.Tests/ResolveReviewTests.cs
git commit -m "feat(watching): ResolveReview promotes the previous-stable bind + review flags"
```

---

## Task 5: MCP wiring, `library_watching` uses the watcher; `Program` lifecycle

**Files:**
- Modify: `clipmetamcp/Tools/ReadTools.cs`
- Modify: `clipmetamcp/Program.cs`
- Test: `clipmetamcp.Tests/LibraryWatchingToolTests.cs`

**Interfaces:**
- Consumes: `ReviewWatcher` (Task 2), `ResolveReview` / `WatchingResult.Review|BoundSegmentId|RecommendationConfident` (Task 4).
- Produces: `library_watching` response gains a `review` array; `ReadTools.RegisterAll(registry, sandbox, ReviewWatcher? watcher = null)`.

- [ ] **Step 1: Write the failing tests**

Add to `clipmetamcp.Tests/LibraryWatchingToolTests.cs` (these go through the normal harness, which has no watcher wired, so they assert the **fallback path stays intact** and the response shape still carries `anyLiveTarget`; the review-emission shape is unit-covered in Task 4):

```csharp
[TestMethod]
public void Watching_StillReturnsAnyLiveTarget_WithNoWatcherWired()
{
    JsonObject result = Call(new JsonObject { ["include_access_fallback"] = true }, _lib);
    Assert.IsNull(result["isError"]);
    Assert.IsTrue(Structured(result).ContainsKey("anyLiveTarget"));
}

[TestMethod]
public void Watching_ReviewArray_AbsentWhenNoFlags()
{
    // No watcher, no segments → no review flags → the 'review' key is omitted (additive, opt-in).
    JsonObject result = Call(new JsonObject { ["include_access_fallback"] = true }, _lib);
    Assert.IsNull(Structured(result)["review"]);
}
```

- [ ] **Step 2: Run, verify the second test FAILS only if `review` is wrongly emitted; both compile**

Run: `dotnet test clipmetamcp.Tests --nologo -v q --filter LibraryWatchingToolTests`
Expected: the two new tests PASS once Step 3–4 land (before changes they pass too, since `review` isn't emitted yet, they pin the contract so Step 4 doesn't break it). Proceed.

- [ ] **Step 3: Thread the watcher through `ReadTools`**

In `clipmetamcp/Tools/ReadTools.cs`:

1. Change the registrar signature:

```csharp
public static void RegisterAll(ToolRegistry registry, LibrarySandbox sandbox, ReviewWatcher? watcher = null)
```

2. Capture `watcher` in the `library_watching` handler closure: change `args => Watching(args, sandbox)` to `args => Watching(args, sandbox, watcher)`.

3. Change the `Watching` handler so that, when a watcher is present, it resolves via `ResolveReview`, calls `MarkBound`, and emits `review[]`. Replace the resolver call block (`ReadTools.cs:536-537`) and the response assembly with:

```csharp
private static JsonObject Watching(JsonObject? args, LibrarySandbox sandbox, ReviewWatcher? watcher = null)
{
    string root = sandbox.RequireRoot();

    // ... unchanged opportunistic-drain block (DrainReport drained = ...) ...

    int limit = Math.Clamp(GetOptionalInt(args, "limit", DefaultWatchingLimit), 1, MaxWatchingLimit);
    bool includeAccessFallback = GetOptionalBool(args, "include_access_fallback", defaultValue: true);

    var resolver = WatchingResolver.CreateDefault(ProcessWindowSource.ForCurrentPlatform());
    WatchingResult result = watcher is null
        ? resolver.Resolve(root, limit, includeAccessFallback)
        : resolver.ResolveReview(root, watcher.Snapshot(), watcher.LastBoundId,
                                 DateTimeOffset.UtcNow, limit, includeAccessFallback);

    if (watcher is not null && result.RecommendationConfident && result.BoundSegmentId is { } id)
        watcher.MarkBound(id);

    // ... unchanged candidate-array building ...

    var response = new JsonObject
    {
        ["libraryRoot"] = root,
        ["candidateCount"] = result.Candidates.Count,
        ["anyLiveTarget"] = result.AnyLiveTarget,
        ["candidates"] = array,
    };

    if (result.Review is { Count: > 0 })
    {
        var review = new JsonArray();
        foreach (ReviewFlag f in result.Review)
        {
            var clips = new JsonArray();
            foreach (string c in f.Clips) clips.Add(c);
            var entry = new JsonObject { ["type"] = f.Type, ["clips"] = clips };
            if (f.StableSeconds > 0) entry["stableSeconds"] = Math.Round(f.StableSeconds, 1);
            review.Add(entry);
        }
        response["review"] = review;

        // A multi-player flag also raises the existing inline warning channel.
        if (result.Review.Any(f => f.Type == ReviewFlag.TypeMultiplePlayersActive))
            response["warning"] = new JsonObject
            {
                ["type"] = "multiple_players_active",
                ["message"] = "More than one media player is active, too ambiguous to bind a clip " +
                              "safely. Confirm the exact path with the user before tagging.",
            };
    }

    // ... unchanged wrong-directory warning block (only if not already set), drainedFromQueue, queuePending ...

    return response;
}
```

> Note: keep the existing wrong-directory `warning` block, but guard it with `if (!response.ContainsKey("warning"))` so a multi-player warning isn't overwritten. Keep the `drainedFromQueue` and `queuePending` blocks exactly as they are.

4. Update the `library_watching` **description** string: append a sentence, 
   *"In review mode the recommended top candidate reflects the clip you were watching when you spoke, even if the player has since advanced (it may be unlocked and directly writable). A 'review' array may list non-blocking advisories, autoCorrected, sameClipTwice, sequenceSkip, multiplePlayersActive, to mention to the user and reconcile later; never block the run to ask."*

- [ ] **Step 4: Wire the watcher in `Program.Serve`**

In `clipmetamcp/Program.cs`, after the `QueueDrainPump` block and before `ReadTools.RegisterAll`:

```csharp
ReviewWatcher? reviewWatcher = null;
if (sandbox.Root is { } watchRoot)
{
    reviewWatcher = new ReviewWatcher(
        ProcessWindowSource.ForCurrentPlatform(),
        () => DateTimeOffset.UtcNow,
        pollInterval: TimeSpan.FromMilliseconds(250));
    reviewWatcher.Start();
}

ReadTools.RegisterAll(registry, sandbox, reviewWatcher);
WriteTools.RegisterAll(registry, sandbox);
QueueTools.RegisterAll(registry, sandbox, pump);
```

And in the `finally` that disposes the pump, also dispose the watcher:

```csharp
finally
{
    pump?.Dispose();
    reviewWatcher?.Dispose();
}
```

- [ ] **Step 5: Build + run the FULL MCP test project**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors.
Run: `dotnet test clipmetamcp.Tests --nologo -v q`
Expected: PASS, including `ToolsList_ContainsTheFullToolSurface` (no tool added/removed) and stdout-purity tests with the watcher type referenced.

- [ ] **Step 6: Commit**

```bash
git add clipmetamcp/Tools/ReadTools.cs clipmetamcp/Program.cs clipmetamcp.Tests/LibraryWatchingToolTests.cs
git commit -m "feat(mcp): library_watching resolves via ReviewWatcher; review[] flags; lifecycle wiring"
```

---

## Task 6: PITFALLS + full-suite gate

**Files:**
- Modify: `docs/PITFALLS.md`

- [ ] **Step 1: Record the gotchas**

Append to `docs/PITFALLS.md` a dated entry covering:
1. **Poll-at-call-time binding race**, `library_watching` historically resolved "what's open now" at tool-execution time, a turn after dictation; if the user advanced first, the tag bound to the next clip. Fixed by the continuous `ReviewWatcher` segment log + previous-stable heuristic (resolve against *when each title played*, not call time).
2. **Not-locked-guard exception**, the resolver demotes an unlocked bare-name hit ("may be a same-named file elsewhere"), but a review-mode *corrected* bind is legitimately unlocked (the player advanced away from it); `ResolveReview` keeps a single history-confirmed match high-confidence rather than demoting it. Don't "fix" the guard to also demote corrected binds.

- [ ] **Step 2: Full build + full relevant test projects (the Definition-of-Done gate)**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors, all projects.
Run: `dotnet test clipmetascribe.Tests --nologo -v q`
Expected: PASS (real-clip integration is slow, allow a long timeout).
Run: `dotnet test clipmetamcp.Tests --nologo -v q`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add docs/PITFALLS.md
git commit -m "docs(pitfalls): binding race + not-locked-guard exception"
```

---

## Self-Review (completed by plan author)

- **Spec coverage:** §2 ReviewWatcher → Task 2. §3 ReviewBindingResolver → Task 1. §4 ResolveReview + interactions (not-locked exception, anyLiveTarget, multi-player) → Tasks 3–4. §5 inline surface + lifecycle → Task 5. §7 affected types → all tasks. §8 tests → each task's tests + Task 6 gate. §10 DoD → Task 6. AC1/AC3/AC4/AC5 → Task 1 + Task 4 tests. AC6 (locked deferral) → unchanged queue path, untouched.
- **Placeholder scan:** none, every step has concrete code or an exact edit with code.
- **Type consistency:** `TitleSegment`, `ReviewFlag` (+ `Type*` consts), `ReviewBinding`, `ReviewBindingResolver.Resolve`, `ReviewWatcher` (`Snapshot`/`LastBoundId`/`MarkBound`/`PollOnce`), `WatchContext.Build(root, windows)`, `ResolveCore`, `ResolveReview`, `WatchingResult(... Review, BoundSegmentId, RecommendationConfident)` are used identically across tasks.
- **Deferred (not in this plan, by design):** timestamp ingestion / fire-N-ahead, gaming mode, the §7 secondary-fix batch, each its own future spec.
