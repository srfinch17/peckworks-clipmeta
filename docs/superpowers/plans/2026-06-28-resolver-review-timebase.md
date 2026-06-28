# Review-Mode Resolver Time-Base Split (pass-7) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the review-mode resolver so a foreign media player (open *or* recently closed) can never blank a legitimate in-library candidate, and make `library_flush_queue`'s `written` authoritative — closing the two HIGH findings + §4.4 from the v1.5.0 dogfood.

**Architecture:** `WatchingResolver.ResolveReview` currently derives BOTH the foreign-player diagnostics and the "which clip did you watch" binding from one synthetic window built from a possibly-closed/foreign segment. Split the time-bases: diagnostics + gaming/access come from a **live** player poll; the review bind is resolved separately from the chosen segment (reusing `ResolveCore` over a context that shares the already-enumerated library). `anyLiveTarget` is recomputed from the final candidate list so `anyLiveTarget:true`+`candidates:[]` is impossible by construction. Separately, `library_queue_tag` wakes the background drain pump only for *locked* clips, so an unlocked queued tag lands via the foreground flush and reports under `written`.

**Tech Stack:** C# / .NET 10, MSTest, zero NuGet in production code (BCL only). Big-endian MP4 IO via Core. MCP server is a thin shell over `ClipMetaCore`.

## Global Constraints

- **Zero external NuGet packages** in `clipmeta.core`, `clipmetascribe`, `clipmetamcp` (test projects may use MSTest only). Copy verbatim from CLAUDE.md.
- **CLIs/MCP are thin shells** — no business logic outside Core. The fix lives in `ClipMetaCore.Watching`; `clipmetamcp` only gets test additions + a one-line wake guard.
- **Big-endian everywhere** for MP4 IO (not touched here, but never use raw `BinaryReader`).
- **Build gate:** `dotnet build --nologo -v q` → 0 warnings, 0 errors, all projects.
- **Test gate:** `dotnet test --nologo --no-build -v q` → all pass, incl. real-clip integration + media-integrity. `clipmetascribe.Tests` takes a few minutes — use a long timeout, it is not a hang.
- **Changed an MCP tool registration or CLI surface?** Run the FULL relevant test project, never a `--filter` (surface-wide assertions live outside the diff). This pass changes NO tool surface, but the version-bump task still runs the full `clipmetamcp.Tests`.
- **XML doc comments** on all public types/methods; named constants, no magic numbers.
- **Version target:** clipmetamcp **v1.6.0** (csproj + `tools/mcpb-manifest.json` must match — pack gate enforces equality).
- New gotchas go in `docs/PITFALLS.md`. The `.mcpb` is git-ignored — repack but do not commit it.

**Spec:** `docs/superpowers/specs/2026-06-28-resolver-review-timebase-design.md` (§ references below point there).

---

### Task 1: Split the time-bases in `ResolveReview` (live diagnostics, historical binding)

This is the core fix (spec §3). It folds in two small scaffolding pieces — a `WatchContext.WithPlayerWindows` helper (so the library is enumerated once, not twice) and an extracted `IsLiveTarget` predicate shared with `ResolveCore` — then rewrites `ResolveReview`.

**Files:**
- Modify: `clipmeta.core/Watching/WatchContext.cs` (add `WithPlayerWindows`)
- Modify: `clipmeta.core/Watching/WatchingResolver.cs` (extract `IsLiveTarget`; rewrite `ResolveReview` lines ~88-148; update `ResolveCore` `anyLiveTarget` lines ~301-304)
- Test: `clipmetascribe.Tests/ResolveReviewTests.cs` (add new cases; existing cases must stay green)
- Test: `clipmetascribe.Tests/WatchContextTests.cs` (add `WithPlayerWindows` test)

**Interfaces:**
- Consumes: `WatchContext.Build(string, IReadOnlyList<ProcessWindow>, SelfActionLedger?)`, `WatchContext.{LibraryClips,ByFileName,ByFullPath,PlayerWindows,KnownBaselinePaths,Ledger}`, `LibraryTitleMatcher.FindBestMatch(string?, IEnumerable<string>)`, `ReviewBindingResolver.Resolve(...)`, `ReviewFlagResolver.Resolve(IReadOnlyList<ReviewFlag>, WatchContext)`, `WatchingCandidate` record, `PlayerTitleSignal.SourceName`, `RecentWriteSignal.SourceName`.
- Produces:
  - `WatchContext WithPlayerWindows(IReadOnlyList<ProcessWindow> playerWindows)` — a new context sharing this one's enumerated library/baseline/ledger but with a different window set.
  - `WatchingResolver.IsLiveTarget(WatchingCandidate)` (private static) — the single liveness predicate.
  - Rewritten `ResolveReview(...)` with the same public signature and return type (`WatchingResult`).

- [ ] **Step 1: Write the failing `WithPlayerWindows` test**

In `clipmetascribe.Tests/WatchContextTests.cs`, add:

```csharp
[TestMethod]
public void WithPlayerWindows_ReusesLibrary_SwapsWindows()
{
    string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        File.WriteAllBytes(Path.Combine(dir, "a.mp4"), Array.Empty<byte>());
        WatchContext baseCtx = WatchContext.Build(dir, Array.Empty<ProcessWindow>());
        var win = new[] { new ProcessWindow("vlc", "a.mp4 - VLC media player") };

        WatchContext swapped = baseCtx.WithPlayerWindows(win);

        Assert.AreSame(baseCtx.ByFullPath, swapped.ByFullPath, "library lookups are reused, not re-enumerated");
        Assert.AreSame(baseCtx.ByFileName, swapped.ByFileName);
        CollectionAssert.AreEqual(win, swapped.PlayerWindows.ToList(), "windows are replaced");
        Assert.AreEqual(0, baseCtx.PlayerWindows.Count, "the original is unchanged");
    }
    finally { Directory.Delete(dir, true); }
}
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter WithPlayerWindows_ReusesLibrary_SwapsWindows`
Expected: FAIL — `WatchContext` has no `WithPlayerWindows`.

- [ ] **Step 3: Add `WithPlayerWindows` to `WatchContext`**

In `clipmeta.core/Watching/WatchContext.cs`, after the second `Build` overload (~line 61), add:

```csharp
/// <summary>
/// Returns a context that REUSES this one's already-enumerated library, baseline, and ledger but
/// resolves against a different set of <paramref name="playerWindows"/>. Review-mode resolution uses
/// this to ask the same library two questions with two window sets — "what is open live?" and "which
/// recorded title did the user describe?" — without paying for a second library enumeration.
/// </summary>
/// <param name="playerWindows">The window set the returned context resolves against.</param>
public WatchContext WithPlayerWindows(IReadOnlyList<ProcessWindow> playerWindows)
{
    ArgumentNullException.ThrowIfNull(playerWindows);
    return new WatchContext
    {
        LibraryClips = LibraryClips,
        ByFileName = ByFileName,
        ByFullPath = ByFullPath,
        PlayerWindows = playerWindows,
        KnownBaselinePaths = KnownBaselinePaths,
        Ledger = Ledger,
    };
}
```

- [ ] **Step 4: Run the test to confirm it passes**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter WithPlayerWindows_ReusesLibrary_SwapsWindows`
Expected: PASS.

- [ ] **Step 5: Extract the `IsLiveTarget` predicate in `WatchingResolver`**

In `clipmeta.core/Watching/WatchingResolver.cs`, replace the inline `anyLiveTarget` computation in `ResolveCore` (the block currently at ~lines 298-304):

```csharp
// #3 — a live target is one a player named, one currently locked, OR a confidently just-saved
// clip (gaming mode, Policy A). When false, every candidate is an unverified recency guess and
// the caller must confirm before tagging.
bool anyLiveTarget = finalCandidates.Any(c =>
    c.Source == PlayerTitleSignal.SourceName ||
    c.InUse ||
    (c.Source == RecentWriteSignal.SourceName && c.Confidence == HighConfidence));
```

with a call to a new shared predicate:

```csharp
// #3 — a live target is one a player named, one currently locked, OR a confidently just-saved
// clip (gaming mode, Policy A). When false, every candidate is an unverified recency guess and
// the caller must confirm before tagging. Shared with ResolveReview so the two paths cannot drift.
bool anyLiveTarget = finalCandidates.Any(IsLiveTarget);
```

Then add the predicate near the other private helpers (e.g. just above `SoleUnresolvedPlayer`):

```csharp
/// <summary>
/// Whether a candidate is an actually-live tag target: a player named it, it is currently locked,
/// or it is a high-confidence just-saved clip (gaming mode, Policy A). The single definition of
/// "live", shared by <see cref="ResolveCore"/> and <see cref="ResolveReview"/>.
/// </summary>
private static bool IsLiveTarget(WatchingCandidate c) =>
    c.Source == PlayerTitleSignal.SourceName ||
    c.InUse ||
    (c.Source == RecentWriteSignal.SourceName && c.Confidence == HighConfidence);
```

- [ ] **Step 6: Build to confirm the refactor compiles (no behavior change yet)**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors.

- [ ] **Step 7: Write the failing review-mode behavior tests**

In `clipmetascribe.Tests/ResolveReviewTests.cs`, add a live-seeded resolver helper and the new cases. Add near the existing `Resolver()` helper:

```csharp
// Seeds the LIVE poll (FakeProcessWindowSource) so a test can model players that are open NOW,
// distinct from the segment history. An empty list models "no player open live" (e.g. a ghost:
// a closed player that survives only in segment history).
private static WatchingResolver ResolverWithLive(params ProcessWindow[] live) =>
    WatchingResolver.CreateDefault(new FakeProcessWindowSource(live));

// A title naming a file that is NOT in the test library — an "outside the library" player.
private const string ForeignTitle =
    @"C:\Outside\Team Fortress 2 2026.01.20 - 21.41.04.189.DVR.mp4 - VLC media player";
```

Then the cases:

```csharp
[TestMethod]
public void ResolveReview_ForeignPlayerOpen_FreshSave_SurfacesGamingCandidate()
{
    // §4.1a: a player is open live on a file OUTSIDE the library, and one fresh clip was just saved
    // INTO the library. Policy A must survive review mode: the gaming candidate is the live target.
    string saved = Touch("saved.mp4"); // creation time = now → a fresh in-library save
    var foreignSeg = new[] { new TitleSegment(1, "vlc", ForeignTitle, T0, null) };

    WatchingResult r = ResolverWithLive(new ProcessWindow("vlc", ForeignTitle))
        .ResolveReview(_dir, foreignSeg, lastBoundId: -1, DateTimeOffset.UtcNow, limit: 5, includeAccessFallback: true);

    Assert.AreEqual(1, r.Candidates.Count, "the gaming candidate is returned, not blanked");
    Assert.AreEqual(saved, r.Candidates[0].Path);
    Assert.AreEqual(RecentWriteSignal.SourceName, r.Candidates[0].Source);
    Assert.AreEqual("high", r.Candidates[0].Confidence);
    Assert.IsTrue(r.AnyLiveTarget, "a sole fresh save is a live target even with a foreign player open");
}

[TestMethod]
public void ResolveReview_ForeignPlayerClosed_FreshSave_NoGhostWarning()
{
    // §4.2a (ghost): the foreign player is CLOSED — it survives only as a closed segment in history,
    // with NO live window. It must not be replayed as an open player; the fresh save still surfaces.
    string saved = Touch("saved.mp4");
    var closedForeign = new[] { new TitleSegment(1, "vlc", ForeignTitle, T0, T0.AddSeconds(5)) };

    WatchingResult r = ResolverWithLive() // no live windows → the closed player is gone
        .ResolveReview(_dir, closedForeign, lastBoundId: -1, DateTimeOffset.UtcNow, limit: 5, includeAccessFallback: true);

    Assert.AreEqual(0, r.Diagnostics.UnresolvedPlayers.Count, "a closed player raises no foreign diagnostic (no ghost)");
    Assert.AreEqual(saved, r.Candidates[0].Path);
    Assert.AreEqual(RecentWriteSignal.SourceName, r.Candidates[0].Source);
    Assert.IsTrue(r.AnyLiveTarget);
}

[TestMethod]
public void ResolveReview_ForeignPlayerClosed_NoSave_NoWarningNotLive()
{
    // §4.2b: closed foreign player + nothing fresh. No ghost warning; nothing is a live target.
    string stale = Touch("stale.mp4");
    File.SetCreationTimeUtc(stale, DateTime.UtcNow.AddHours(-3)); // not a fresh save
    var closedForeign = new[] { new TitleSegment(1, "vlc", ForeignTitle, T0, T0.AddSeconds(5)) };

    WatchingResult r = ResolverWithLive()
        .ResolveReview(_dir, closedForeign, lastBoundId: -1, DateTimeOffset.UtcNow, limit: 5, includeAccessFallback: true);

    Assert.AreEqual(0, r.Diagnostics.UnresolvedPlayers.Count, "no ghost foreign diagnostic");
    Assert.IsFalse(r.AnyLiveTarget, "an access-time guess is not a live target");
}

[TestMethod]
public void ResolveReview_Invariant_AnyLiveTargetImpliesCandidatesPresent()
{
    // The structural guarantee: anyLiveTarget true ⇒ at least one candidate. Exercised across the
    // foreign-open, foreign-closed, and cold-start shapes.
    string saved = Touch("saved.mp4");
    foreach (WatchingResult r in new[]
    {
        ResolverWithLive(new ProcessWindow("vlc", ForeignTitle))
            .ResolveReview(_dir, new[] { new TitleSegment(1, "vlc", ForeignTitle, T0, null) },
                           -1, DateTimeOffset.UtcNow, 5, true),
        ResolverWithLive()
            .ResolveReview(_dir, new[] { new TitleSegment(1, "vlc", ForeignTitle, T0, T0.AddSeconds(5)) },
                           -1, DateTimeOffset.UtcNow, 5, true),
    })
    {
        if (r.AnyLiveTarget)
            Assert.IsTrue(r.Candidates.Count > 0, "anyLiveTarget:true must never accompany an empty candidate list");
    }

    GC.KeepAlive(saved);
}
```

- [ ] **Step 8: Run the new tests to confirm they FAIL against current code**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter ResolveReview_ForeignPlayer`
Expected: FAIL — today `ResolveReview` strips the `recent_write` candidate (so candidates are empty) and replays the foreign segment as a synthetic window (so `UnresolvedPlayers` is non-empty / a ghost). This failure is the bug.

- [ ] **Step 9: Rewrite `ResolveReview`**

Replace the body of `ResolveReview` (the method spanning ~lines 88-148, from the line after the XML doc to the closing `}`) with the time-base split. Keep the method signature, the XML doc, and the `CorrectedBindNote` constant unchanged.

```csharp
public WatchingResult ResolveReview(
    string libraryRoot, IReadOnlyList<TitleSegment> segments, long lastBoundId,
    DateTimeOffset now, int limit, bool includeAccessFallback, DateTimeOffset? spokenAt = null)
{
    ReviewBinding binding = ReviewBindingResolver.Resolve(
        segments, lastBoundId, now, stableThreshold: null, spokenAt: spokenAt);

    // TIME-BASE 1 (live reality): diagnostics, wrong-directory suppression, gaming (recent_write)
    // and access fallback all reflect what is open RIGHT NOW. A player closed a turn ago is simply
    // absent here — so it can raise no "do not tag" warning (the ghost) and skew no suppression.
    WatchContext liveContext = WatchContext.Build(libraryRoot, _windowSource.GetPlayerWindows(_playerNames), _ledger);
    WatchingResult core = ResolveCore(liveContext, limit, includeAccessFallback);

    List<WatchingCandidate> candidates = core.Candidates.ToList();
    bool confident = false;
    long? boundId = null;

    // TIME-BASE 2 (history): which recorded title did the user describe? Resolve the chosen segment
    // against the SAME (already-enumerated) library. Only when it resolves to a single in-library
    // player-title candidate is it a real "what you watched" bind — and then it IS the answer, so a
    // background save (recent_write) and recency guesses are noise and drop out. When the chosen
    // segment is foreign/closed/unresolved, there is no bind: the live `core` result stands, so a
    // fresh game-save (Policy A) survives and a live foreign player demotes to an advisory downstream.
    if (binding.Chosen is { } sel)
    {
        WatchContext bindContext = liveContext.WithPlayerWindows(
            new[] { new ProcessWindow(sel.ProcessName, sel.RawTitle) });
        IReadOnlyList<WatchingCandidate> bindCandidates = ResolveCore(bindContext, limit, includeAccessFallback).Candidates;

        List<WatchingCandidate> playerBinds = bindCandidates
            .Where(c => c.Source == PlayerTitleSignal.SourceName)
            .ToList();
        if (playerBinds.Count == 1)
        {
            WatchingCandidate bind = playerBinds[0];
            candidates = new List<WatchingCandidate>
            {
                bind with
                {
                    Confidence = HighConfidence,
                    Note = binding.CorrectedFrom is null ? bind.Note : CorrectedBindNote,
                },
            };
            confident = true;
            boundId = sel.Id;
        }
    }

    // #2 cap: when two or more players are open, the bind is ambiguous — force confirm-first. Nothing
    // is an auto-tag target and no candidate may read high, even a locked one.
    bool multiPlayer = binding.Flags.Any(f => f.Type == ReviewFlag.TypeMultiplePlayersActive);
    if (multiPlayer)
        candidates = candidates
            .Select(c => c.Confidence == HighConfidence ? c with { Confidence = LowConfidence } : c)
            .ToList();

    // Single source of truth: anyLiveTarget is DERIVED from the final list (never carried stale), so
    // anyLiveTarget:true alongside an empty candidate list is impossible. The multi-player cap can
    // only lower it.
    bool anyLive = !multiPlayer && candidates.Any(IsLiveTarget);

    return new WatchingResult(
        candidates, core.Diagnostics, anyLive,
        ReviewFlagResolver.Resolve(binding.Flags, liveContext), boundId, confident);
}
```

Note: the old `core.Candidates.Where(c => c.Source != RecentWriteSignal.SourceName)` strip and the `core.AnyLiveTarget || confident` line are intentionally gone — replaced by the bind-overlay and the derived `anyLive`.

- [ ] **Step 10: Run the new tests to confirm they PASS**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter ResolveReview_ForeignPlayer`
Expected: PASS (all three foreign-player cases).

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter ResolveReview_Invariant`
Expected: PASS.

- [ ] **Step 11: Run the FULL `ResolveReviewTests` + binding/flag suites to confirm no regression**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter "ResolveReview|ReviewBinding|ReviewFlag|WatchingResolver|WatchContext"`
Expected: PASS — all pre-existing review/binding/flag/resolver tests stay green (their `binding.Chosen` resolves in-library, so the overlay reproduces today's bind; cold-start and multi-player paths are behaviorally unchanged).

- [ ] **Step 12: Commit**

```bash
git add clipmeta.core/Watching/WatchContext.cs clipmeta.core/Watching/WatchingResolver.cs clipmetascribe.Tests/ResolveReviewTests.cs clipmetascribe.Tests/WatchContextTests.cs
git commit -m "fix(resolver): split review-mode time-bases — live diagnostics, historical binding

ResolveReview derived both the foreign-player diagnostics and the watched-clip
bind from one synthetic window built from a possibly-closed/foreign segment.
That blanked a valid in-library gaming candidate (anyLiveTarget:true + []) and
replayed closed players as ghosts. Diagnostics/gaming/access now come from a
live poll; the bind is resolved from the chosen segment over the shared library;
anyLiveTarget is derived from the final list. Closes dogfood §4.1 + §4.2.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: §4.4 — wake the drain pump only for locked clips so `written` is authoritative

Spec §4. `library_queue_tag` wakes the purely event-driven `QueueDrainPump` unconditionally, so it drains an *unlocked* clip immediately and books it under `autoFlushed` before an explicit `library_flush_queue` runs (which then reports `written:[]`). Wake only when the clip is locked.

**Why a new harness method:** the existing MCP queue tests (`QueueToolsTests.cs`) all run via `McpHarness.Run`, which wires `pump: null` — so they never exercise the production pump path, and the §4.4 bug (and its fix) is invisible to them. This task adds a `RunWithPump` harness method (mirroring the existing `RunWithJournal` / `RunWithLedger`) that wires a real `QueueDrainPump` + `DrainJournal`, closing that coverage gap. The deterministic assertion is the UNLOCKED case: post-fix the pump is never woken, so the background thread stays idle (its `WaitAny` never fires) and the explicit flush lands the tag in `written`.

**Files:**
- Modify: `clipmetamcp/Tools/QueueTools.cs` (the `pump?.Wake();` line in `QueueTag`, ~line 148)
- Modify: `clipmetamcp.Tests/Helpers/McpHarness.cs` (add `RunWithPump`)
- Test: `clipmetamcp.Tests/QueueToolsTests.cs` (add the unlocked-clip case)

**Interfaces:**
- Consumes: `LockProbe.IsInUse(string)`, `QueueDrainPump(...)` ctor (signature: `string libraryRoot, IMediaWriter writer, IClipMetaLogger logger, Func<string,bool> isInUse, Action<Action> runExclusive, TimeSpan pollInterval, DrainJournal? journal = null`), `WriteGate.Enter/Exit`, `Mp4Writer`, `NullLogger.Instance`, `DrainJournal`, the existing `McpHarness.Run` body pattern.
- Produces: `McpHarness.RunWithPump(string? libraryRoot, QueueDrainPump pump, DrainJournal journal, params string[] requestLines)`.

- [ ] **Step 1: Add `RunWithPump` to the harness**

In `clipmetamcp.Tests/Helpers/McpHarness.cs`, after `RunWithJournal`, add (mirror its body exactly, but pass the pump + journal into `QueueTools.RegisterAll` and the journal into `ReadTools.RegisterAll`):

```csharp
/// <summary>
/// Like <see cref="Run"/>, but wires a real <paramref name="pump"/> + shared <paramref name="journal"/>
/// into the queue tools (and the journal into <see cref="ReadTools"/>), mirroring <c>Program.cs</c>
/// production wiring. Use this to exercise the background-pump path the plain harness never wires
/// (its <c>pump:null</c> hides §4.4). The caller owns the pump's lifecycle (Start/Dispose).
/// </summary>
public static IReadOnlyList<JsonObject> RunWithPump(
    string? libraryRoot, QueueDrainPump pump, DrainJournal journal, params string[] requestLines)
{
    var registry = new ToolRegistry();
    var sandbox = new LibrarySandbox(libraryRoot);
    ReadTools.RegisterAll(registry, sandbox, watcher: null, ledger: null, journal: journal);
    WriteTools.RegisterAll(registry, sandbox);
    QueueTools.RegisterAll(registry, sandbox, pump: pump, journal: journal);

    using var input = new StringReader(string.Concat(requestLines.Select(line => line + "\n")));
    using var output = new StringWriter();
    new McpSession(input, output, registry, NullLogger.Instance).Run();

    return output.ToString()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => (JsonObject)JsonNode.Parse(line)!)
        .ToList();
}
```

Add `using ClipMetaCore.Write;` to the file if `QueueDrainPump`/`Mp4Writer` types aren't already in scope (they live in `ClipMetaCore.Watching` / `ClipMetaCore.Write` — the test references the pump type only as a parameter, so just the `ClipMetaCore.Watching` using already present suffices for `QueueDrainPump`).

- [ ] **Step 2: Write the failing test — unlocked queued tag lands in `written`, pump never woken**

In `clipmetamcp.Tests/QueueToolsTests.cs`, add (the pump uses the real `LockProbe.IsInUse`; an empty temp `.mp4` is unlocked, so post-fix the pump is never woken and stays idle):

```csharp
[TestMethod]
public void QueueTag_UnlockedClip_PumpNotWoken_FlushReportsUnderWritten()
{
    // §4.4: with a REAL pump wired, queueing a tag for an UNLOCKED clip must NOT wake the pump, so
    // the explicit flush lands the write under `written` (not the pump's `autoFlushed`). Pre-fix the
    // pump is woken, drains the unlocked clip immediately, and the flush reports written:[].
    string clip = Path.Combine(_lib, "clip.mp4");
    File.WriteAllBytes(clip, Array.Empty<byte>());

    var journal = new DrainJournal();
    using var pump = new QueueDrainPump(
        _lib, new Mp4Writer(), NullLogger.Instance, LockProbe.IsInUse,
        runExclusive: action => { WriteGate.Enter(); try { action(); } finally { WriteGate.Exit(); } },
        pollInterval: TimeSpan.FromMilliseconds(50), journal: journal);
    pump.Start();

    var enqueue = McpHarness.RunWithPump(_lib, pump, journal,
        McpHarness.InitializeRequest,
        McpHarness.ToolCall(2, "library_queue_tag",
            new JsonObject { ["path"] = clip, ["fields"] = new JsonObject { ["tags"] = "headshot" } }));
    Assert.IsNull(((JsonObject)enqueue[1]["result"]!)["isError"], "enqueue must succeed");

    var flushResponses = McpHarness.RunWithPump(_lib, pump, journal,
        McpHarness.InitializeRequest,
        McpHarness.ToolCall(2, "library_flush_queue", new JsonObject()));
    JsonObject s = Structured((JsonObject)flushResponses[1]["result"]!);

    Assert.AreEqual(1, s["written"]!.AsArray().Count, "an unlocked queued tag lands via the foreground flush → written");
    Assert.AreEqual(0, s["autoFlushed"]!.AsArray().Count, "the pump must not be woken for an unlocked clip");
}
```

Add any missing usings to `QueueToolsTests.cs`: `using ClipMetaCore.Write;` (for `Mp4Writer`, `WriteGate`), `using ClipMetaCore.Logging;` (for `NullLogger`). `QueueDrainPump`, `DrainJournal`, `LockProbe` are in `ClipMetaCore.Watching` (already imported).

- [ ] **Step 3: Run to confirm it fails**

Run: `dotnet test clipmetamcp.Tests --nologo -v q --filter QueueTag_UnlockedClip_PumpNotWoken_FlushReportsUnderWritten`
Expected: FAIL — today the pump is woken for the unlocked clip, drains it under `autoFlushed`, and the flush reports `written:[]` (`autoFlushed` count 1).

- [ ] **Step 4: Guard the wake on lock state**

In `clipmetamcp/Tools/QueueTools.cs`, in `QueueTag`, replace:

```csharp
        // Wake the background pump so it lands THIS tag the instant the player's lock clears — the
        // zero-touch flush for the last clip, where no further watched-clip call will drain it.
        pump?.Wake();
```

with:

```csharp
        // Wake the background pump ONLY for a clip that is currently LOCKED — that is the case the
        // pump exists for (zero-touch landing when the player closes). An UNLOCKED queued tag is left
        // for the foreground drain (the next watched-clip call or an explicit library_flush_queue) so
        // it reports under `written`, not `autoFlushed`. The pump idles on an event, so not waking it
        // means it never races that foreground flush. (dogfood §4.4)
        if (LockProbe.IsInUse(fullPath))
            pump?.Wake();
```

- [ ] **Step 5: Run to confirm it passes**

Run: `dotnet test clipmetamcp.Tests --nologo -v q --filter QueueTag_UnlockedClip_PumpNotWoken_FlushReportsUnderWritten`
Expected: PASS.

- [ ] **Step 6: Run the FULL queue + pump suites for regressions**

Run: `dotnet test clipmetamcp.Tests --nologo -v q --filter "Queue"` then `dotnet test clipmetascribe.Tests --nologo -v q --filter "QueueDrainPump|TagQueue"`
Expected: PASS — the locked-clip zero-touch path (pump still woken when `IsInUse`) is unchanged; the existing `pump:null` queue tests are unaffected.

- [ ] **Step 7: Commit**

```bash
git add clipmetamcp/Tools/QueueTools.cs clipmetamcp.Tests/Helpers/McpHarness.cs clipmetamcp.Tests/QueueToolsTests.cs
git commit -m "fix(queue): wake drain pump only for locked clips so flush \`written\` is authoritative

An unlocked queued tag was drained by the background pump (woken unconditionally)
before the explicit flush ran, so library_flush_queue reported written:[] while
the write appeared under autoFlushed. Wake the pump only when the clip is locked.
Adds RunWithPump to the MCP harness (the suite never wired a real pump). Closes
dogfood §4.4.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: Version bump, PITFALLS, full-suite gate, repack

Spec §5/§8. Bring the bundle to v1.6.0, record the gotchas, prove the whole suite green, repack the `.mcpb` (do not commit it).

**Files:**
- Modify: `clipmetamcp/clipmetamcp.csproj` (version 1.5.0 → 1.6.0)
- Modify: `tools/mcpb-manifest.json` (version 1.5.0 → 1.6.0)
- Modify: `docs/PITFALLS.md` (append two entries)
- Run: `tools/pack-mcpb.ps1` (regenerates `dist/clipmeta.mcpb`, git-ignored)

**Interfaces:** none (release plumbing).

- [ ] **Step 1: Confirm the current version strings**

Run: `grep -ri "1\.5\.0" clipmetamcp/clipmetamcp.csproj tools/mcpb-manifest.json`
Expected: both show `1.5.0`. Note the exact element/key names before editing.

- [ ] **Step 2: Bump both to 1.6.0**

Edit `clipmetamcp/clipmetamcp.csproj`: change the version element value `1.5.0` → `1.6.0`.
Edit `tools/mcpb-manifest.json`: change the `"version"` value `1.5.0` → `1.6.0`.
(The pack script enforces these two match — keep them identical.)

- [ ] **Step 3: Append PITFALLS entries**

In `docs/PITFALLS.md`, append:

```markdown
## Review-mode resolver: diagnose live, bind from history (pass-7)

`WatchingResolver.ResolveReview` must answer two questions from two time-bases. "Is a foreign player
open right now?" (the `player_outside_library` diagnostic + access suppression) MUST come from a LIVE
player poll — never from `binding.Chosen`'s segment, which may be a player that has since CLOSED.
Replaying a closed segment as a synthetic window made closed players "ghost" (warn after exit) and,
combined with the review-mode `recent_write` strip, blanked a valid in-library gaming candidate
(`anyLiveTarget:true` beside `candidates:[]`). "Which clip did the user describe?" (the bind) is the
historical question and is resolved separately from the chosen segment. Derive `anyLiveTarget` from
the FINAL candidate list (shared `IsLiveTarget` predicate) so true-beside-empty is impossible. Pass-6's
Policy A fix lived in `ResolveCore`; its tests never drove `ResolveReview` with a foreign SEGMENT
present, so the regression hid — always test the resolver through `ResolveReview` with seeded segments.

## Queue: wake the drain pump only for locked clips (pass-7)

`library_queue_tag` waking `QueueDrainPump` unconditionally made the (event-driven) pump drain an
UNLOCKED clip and book it under `autoFlushed` before an explicit `library_flush_queue` ran — so
`flush` reported `written:[]` though the write succeeded. Wake the pump only when `LockProbe.IsInUse`
is true; an unlocked tag then lands via the foreground drain and reports under `written`. The pump
idles on an event, so not waking it means no race with the foreground flush.
```

- [ ] **Step 4: Full clean build**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors, all projects.

- [ ] **Step 5: Full test suite (long timeout — this is the real-clip integration run)**

Run: `dotnet test --nologo --no-build -v q`
Expected: ALL pass (view 101+, mcp 131+ incl. the new cases, scribe 489+ incl. the new cases), including real-clip integration + media-integrity. Use a multi-minute timeout; `clipmetascribe.Tests` is slow by design, not hung.

- [ ] **Step 6: Repack the bundle (git-ignored output)**

Run: `pwsh tools/pack-mcpb.ps1`
Expected: regenerates `dist/clipmeta.mcpb` reporting v1.6.0; the pack gate passes because csproj and manifest match. Do NOT `git add` the `.mcpb`.

- [ ] **Step 7: Commit (version + docs only)**

```bash
git add clipmetamcp/clipmetamcp.csproj tools/mcpb-manifest.json docs/PITFALLS.md
git commit -m "chore: bump clipmetamcp to v1.6.0 + record pass-7 pitfalls

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Notes for the executor

- **Out of scope (do NOT implement):** §4.5 recent_write window (verify in the next dogfood first — likely the index-baseline exclusion, not a window bug), §6 (already guarded by CreationTime + SelfActionLedger), incremental indexing, the index-as-primary-identity rework, the v1.0.0 release reset (owner-gated).
- **Documented follow-up (NOT this pass) — MCP suite never exercises review mode.** During planning I found `McpHarness` wires `watcher: null` everywhere, so every `library_watching` MCP test runs the live-`Resolve` path, never `ResolveReview` — and `ReadTools.Watching` hardcodes `ProcessWindowSource.ForCurrentPlatform()` for the live poll, so an *open* foreign player cannot be injected at the MCP boundary. The §4.1/§4.2 regression is therefore fully covered at the **Core** level (Task 1 drives `ResolveReview` directly), which is where the bug lives. A future pass could (a) add a `RunWithWatcher` harness seam + (b) make the watching handler's live window source injectable, then add the foreign-open advisory (`player_outside_library_ignored`) and foreign-closed no-ghost cases end-to-end. Worth a PITFALLS/spec note next time the MCP watching surface is touched. This is the only finding originally slated as an MCP test task that was descoped — not a gap in the fix, a gap in MCP-level *integration* coverage.
- **Do not push or open a PR or merge** as part of execution — the owner reviews first and decides the v1.6.0-vs-fold-into-1.0.0 version question after their next dogfood.
- If any new test cannot be expressed with the existing harness, extend the harness minimally following `ReviewWatcherTests.cs` / the existing MCP harness — never relax an assertion or move logic out of Core to make a test pass.
