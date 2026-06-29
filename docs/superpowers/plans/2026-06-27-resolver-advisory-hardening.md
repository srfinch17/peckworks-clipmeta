# Resolver & Advisory Hardening (pass-6) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the v1.4.0 dogfood's three resolution defects, a foreign player no longer hides a fresh gaming save (#1), `multiplePlayersActive` fires whenever two players are open and caps confidence (#2), and review advisories show clean, deduped library names (#6).

**Architecture:** Three small, isolated changes. #1 adds a one-clause exception to the foreign-player suppression in `WatchingResolver.ResolveCore` plus a pure warning/advisory decision helper in `ReadTools`. #2 widens a boolean in `ReviewBindingResolver.ComputeHeuristic` and adds a confidence cap in `WatchingResolver.ResolveReview`. #6 adds a pure `ReviewFlagResolver` (reusing `LibraryTitleMatcher`) wired into `ResolveReview`. No new MCP tools, no CLI changes.

**Tech Stack:** C# / .NET 10, BCL only in production; MSTest in test projects.

**Spec:** `docs/superpowers/specs/2026-06-27-resolver-advisory-hardening-design.md`

## Global Constraints

- **Zero external NuGet packages** in production projects (`clipmeta.core`, `clipmetamcp`). BCL/SDK only; MSTest only in test projects.
- **CLIs/MCP are thin shells**, no business logic in `ReadTools`; clip-identity logic lives in `clipmeta.core`.
- **Big-endian MP4 IO** rule is untouched here (no parser/writer changes).
- **`warning`** stays semantically "do not tag"; the #1 foreign-player demotion uses a **separate non-blocking `advisory` key** (type `player_outside_library_ignored`).
- **Policy A:** only a *lone* unambiguous `recent_write` (exactly one fresh save) is an auto-taggable live target; several at once stay low/confirm.
- **Version:** bump `clipmetamcp` **1.4.0 → 1.5.0** in BOTH `clipmetamcp/clipmetamcp.csproj` (`AssemblyVersion` + `InformationalVersion`) and `tools/mcpb-manifest.json` (pack gate fails if they disagree).
- **`.mcpb` repack is DEFERRED** to after the final whole-branch review (so the shipped bundle carries any fix-wave changes), exactly as pass-5 did. Do NOT commit `dist/clipmeta.mcpb` (git-ignored).
- **Run the FULL `clipmetamcp.Tests` project** for any change touching the MCP tool surface (`Phase2ReadToolsTests.ToolsList_ContainsTheFullToolSurface` asserts the exact tool set).
- Build must be **0 warnings, 0 errors**; all tests green including real-clip integration.
- XML doc comments on all new public types/methods; named constants, no magic numbers.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `clipmeta.core/Watching/WatchingResolver.cs` | Scoring/ranking pipeline | #1 suppression exception (`ResolveCore`); #2 confidence cap (`ResolveReview`); #6 wire `ReviewFlagResolver` (`ResolveReview`) |
| `clipmeta.core/Watching/ReviewBindingResolver.cs` | Pure review-bind heuristic | #2 widen multi-player trigger |
| `clipmeta.core/Watching/ReviewFlagResolver.cs` | **NEW** pure flag-clip resolver | #6 resolve titles → basenames, drop unresolvable, dedup |
| `clipmetamcp/Tools/ReadTools.cs` | `library_watching` JSON shaping | #1 warning vs advisory decision + emission |
| `clipmetascribe.Tests/WatchingResolverTests.cs` | Core resolver tests | #1 cases + fix two fixtures |
| `clipmetascribe.Tests/ResolveReviewTests.cs` | Review-resolution tests | #2 cap cases |
| `clipmetascribe.Tests/ReviewFlagResolverTests.cs` | **NEW** | #6 helper tests |
| `clipmetamcp.Tests/LibraryWatchingToolTests.cs` | MCP watching tests | #1 helper test |
| `clipmetamcp/clipmetamcp.csproj`, `tools/mcpb-manifest.json` | Version | 1.4.0 → 1.5.0 |
| `docs/PITFALLS.md` | Gotcha log | 2 new entries |

---

## Task 1: #1, Gaming candidate survives foreign-player suppression (core)

**Files:**
- Modify: `clipmeta.core/Watching/WatchingResolver.cs` (the `if (!hasPlayer && suppressAccessFallback) continue;` branch, ~line 185)
- Modify: `clipmetascribe.Tests/WatchingResolverTests.cs` (add new tests; fix two existing fixtures)

**Interfaces:**
- Consumes: `RecentWriteSignal.SourceName` (`"recent_write"`), `SignalHit.Ambiguous`, existing `hasRecentWrite` local in `ResolveCore`.
- Produces: no signature change. Behavior change, a single unambiguous `recent_write` hit now survives the foreign-player suppression and scores high / `AnyLiveTarget == true`.

**Background:** Today, when a player is open on a file outside the library, `suppressAccessFallback` is true and the loop drops *every* non-player hit, including the just-saved gaming clip. The fix: keep a *lone* unambiguous `recent_write` hit (Policy A). `RecentWriteSignal` already marks its hit `Ambiguous` when more than one fresh clip exists, so a single fresh save is exactly `!Ambiguous`.

- [ ] **Step 1: Write the failing tests**

Add to `clipmetascribe.Tests/WatchingResolverTests.cs` (after the existing gaming-mode tests, ~line 463):

```csharp
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
```

Also **fix two existing fixtures** whose `inlibrary.mp4` is created fresh and would now surface as a gaming candidate (their intent is pure access-fallback suppression with *no* fresh save, back-date so that intent holds). In `Resolve_PlayerOnForeignFile_NoResolution_WarnsAndSuppressesFallback` change:

```csharp
        Touch("inlibrary.mp4"); // exists, but nobody is playing it
```
to:
```csharp
        TouchStale("inlibrary.mp4"); // exists, not fresh, no gaming target, pure suppression case
```

And in `Resolve_BareNameForeignFile_HasNoForeignDirectory` change:
```csharp
        Touch("inlibrary.mp4");
```
to:
```csharp
        TouchStale("inlibrary.mp4");
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter "FullyQualifiedName~Resolve_ForeignPlayer"`
Expected: FAIL, `Resolve_ForeignPlayer_SingleFreshSave_SurvivesSuppression` finds 0 candidates (current suppression drops the save).

- [ ] **Step 3: Add the suppression exception**

In `clipmeta.core/Watching/WatchingResolver.cs`, replace the suppression branch (~line 184-186):

```csharp
            // The wrong-directory suppression (a player on a foreign file) means the user is NOT
            // gaming, so it suppresses every non-player guess, recent-write included.
            if (!hasPlayer && suppressAccessFallback)
                continue;
```

with:

```csharp
            // The wrong-directory suppression (a player on a foreign file) means the user is NOT
            // gaming, so it suppresses access-time guesses. EXCEPTION (#1, Policy A): a single
            // unambiguous just-saved clip is a legitimate in-library gaming target and the foreign
            // lock (on a file you cannot tag anyway) must not hide it. Several fresh saves at once
            // are ambiguous and stay suppressed.
            if (!hasPlayer && suppressAccessFallback)
            {
                bool soleFreshSave = hasRecentWrite &&
                    hits.Any(h => h.Source == RecentWriteSignal.SourceName && !h.Ambiguous);
                if (!soleFreshSave)
                    continue;
            }
```

- [ ] **Step 4: Run the watching suite to verify pass + no regressions**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter "FullyQualifiedName~WatchingResolver"`
Expected: PASS, all `WatchingResolverTests` green, including the two new tests and the two re-based fixtures.

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/WatchingResolver.cs clipmetascribe.Tests/WatchingResolverTests.cs
git commit -m "fix(watching): a single fresh save survives foreign-player suppression (#1)"
```

---

## Task 2: #1, `player_outside_library` demotes to non-blocking advisory (MCP)

**Files:**
- Modify: `clipmetamcp/Tools/ReadTools.cs` (add a pure decision helper; change the warning-emission block, ~line 652-669)
- Modify: `clipmetamcp.Tests/LibraryWatchingToolTests.cs` (add a unit test for the helper)

**Interfaces:**
- Consumes: `WatchingCandidate` (positional record: `Path, Name, Source, Player, LastAccessTimeUtc, SecondsSinceAccess, InUse, Confidence, Note`), `RecentWriteSignal.SourceName`.
- Produces: `internal static bool ReadTools.ForeignNoticeIsBlocking(IReadOnlyList<WatchingCandidate> candidates)` → false when any candidate is a `recent_write` (a gaming target exists), true otherwise.

**Background:** The MCP harness wires no `ReviewWatcher` and the handler calls `ProcessWindowSource.ForCurrentPlatform()` directly, so a foreign player cannot be simulated end-to-end in tests. The decision (blocking warning vs non-blocking advisory) is therefore extracted as a pure helper and unit-tested directly; the emission wiring rides the existing no-player tests.

- [ ] **Step 1: Write the failing test**

Add to `clipmetamcp.Tests/LibraryWatchingToolTests.cs`:

```csharp
[TestMethod]
public void ForeignNotice_Blocking_WhenNoGamingTarget()
{
    // No recent_write candidate → the foreign player is a genuine "do not tag" warning.
    var candidates = new[]
    {
        new WatchingCandidate("a.mp4", "a.mp4", AccessTimeSignal.SourceName, null,
            DateTime.UtcNow, 1.0, false, "low"),
    };
    Assert.IsTrue(ClipMetaMcp.Tools.ReadTools.ForeignNoticeIsBlocking(candidates));
}

[TestMethod]
public void ForeignNotice_NonBlocking_WhenGamingTargetPresent()
{
    // A recent_write (gaming) candidate is present → the foreign player is demoted to advisory.
    var candidates = new[]
    {
        new WatchingCandidate("saved.mp4", "saved.mp4", RecentWriteSignal.SourceName, null,
            DateTime.UtcNow, 0.0, false, "high"),
    };
    Assert.IsFalse(ClipMetaMcp.Tools.ReadTools.ForeignNoticeIsBlocking(candidates));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test clipmetamcp.Tests --nologo -v q --filter "FullyQualifiedName~ForeignNotice"`
Expected: FAIL, `ReadTools.ForeignNoticeIsBlocking` does not exist (compile error).

- [ ] **Step 3: Add the helper and rewire the emission**

In `clipmetamcp/Tools/ReadTools.cs`, add the helper (near the other private helpers in the class):

```csharp
/// <summary>
/// Whether an open foreign player should be reported as a BLOCKING "do not tag" warning. False
/// when the candidate list already contains a gaming target (a <c>recent_write</c> candidate): a
/// foreign lock is on a file you cannot tag anyway, so it must not block a valid in-library save, 
/// it demotes to a non-blocking advisory instead (#1).
/// </summary>
internal static bool ForeignNoticeIsBlocking(IReadOnlyList<WatchingCandidate> candidates) =>
    !candidates.Any(c => c.Source == RecentWriteSignal.SourceName);
```

Replace the foreign-player warning block (~line 652-669):

```csharp
        if (response["warning"] is null && result.Diagnostics.UnresolvedPlayers.Count > 0)
        {
            var players = new JsonArray();
            foreach (UnresolvedPlayer up in result.Diagnostics.UnresolvedPlayers)
                players.Add(new JsonObject
                {
                    ["player"] = up.Player,
                    ["referencedName"] = up.ReferencedName,
                    ["foreignDirectory"] = up.ForeignDirectory,
                });
            response["warning"] = new JsonObject
            {
                ["type"] = "player_outside_library",
                ["message"] = "A media player is showing a file that is not in the configured clips " +
                              "library. The user may be playing from the wrong folder. Do not tag.",
                ["unresolvedPlayers"] = players,
            };
        }
```

with:

```csharp
        if (response["warning"] is null && result.Diagnostics.UnresolvedPlayers.Count > 0)
        {
            var players = new JsonArray();
            foreach (UnresolvedPlayer up in result.Diagnostics.UnresolvedPlayers)
                players.Add(new JsonObject
                {
                    ["player"] = up.Player,
                    ["referencedName"] = up.ReferencedName,
                    ["foreignDirectory"] = up.ForeignDirectory,
                });

            if (ForeignNoticeIsBlocking(result.Candidates))
                response["warning"] = new JsonObject
                {
                    ["type"] = "player_outside_library",
                    ["message"] = "A media player is showing a file that is not in the configured clips " +
                                  "library. The user may be playing from the wrong folder. Do not tag.",
                    ["unresolvedPlayers"] = players,
                };
            else
                // #1: a fresh in-library save was detected, the gaming candidate is the live target,
                // so the foreign player is informational only (never "do not tag").
                response["advisory"] = new JsonObject
                {
                    ["type"] = "player_outside_library_ignored",
                    ["message"] = "A media player is showing a file outside the library, but a fresh " +
                                  "in-library save was detected, the gaming candidate below is the live " +
                                  "target. The foreign player was ignored.",
                    ["unresolvedPlayers"] = players,
                };
        }
```

- [ ] **Step 4: Run the FULL MCP test project**

Run: `dotnet test clipmetamcp.Tests --nologo -v q`
Expected: PASS, the two new helper tests plus the full existing suite (incl. `ToolsList_ContainsTheFullToolSurface` and `Watching_NormalCall_HasNoWarning`, which stays green: no foreign player → neither key emitted).

- [ ] **Step 5: Commit**

```bash
git add clipmetamcp/Tools/ReadTools.cs clipmetamcp.Tests/LibraryWatchingToolTests.cs
git commit -m "feat(mcp): demote player_outside_library to non-blocking advisory when a gaming target exists (#1)"
```

---

## Task 3: #2, `multiplePlayersActive` fires for any two open players + caps confidence

**Files:**
- Modify: `clipmeta.core/Watching/ReviewBindingResolver.cs` (`ComputeHeuristic`, the `multiPlayer` detection, ~line 82-95)
- Modify: `clipmeta.core/Watching/WatchingResolver.cs` (`ResolveReview`, before the final `return`, ~line 132-137)
- Modify: `clipmetascribe.Tests/ResolveReviewTests.cs` (add cap cases)

**Interfaces:**
- Consumes: `ReviewFlag.TypeMultiplePlayersActive`, `WatchingResolver.LowConfidence` / `HighConfidence`, `WatchingCandidate` `with`-expression.
- Produces: when `binding.Flags` contains `multiplePlayersActive`, `ResolveReview` returns `AnyLiveTarget == false` and every candidate at `LowConfidence`.

**Background:** The heuristic only flags multiple players when two open segments *start* within ~2s of each other; two players opened seconds apart never trip it (the dogfood case). Widen to "≥2 distinct processes currently have an open segment." Separately, a null `Chosen` cold-starts into `ResolveCore`, which can still report `AnyLiveTarget` via a lock; cap that so the caller confirms.

- [ ] **Step 1: Write the failing tests**

Add to `clipmetascribe.Tests/ResolveReviewTests.cs`:

```csharp
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
    // #2 cap: when multiplePlayersActive fires, the caller must confirm, anyLiveTarget is false
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
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter "FullyQualifiedName~ResolveReview_TwoPlayersOpenFarApart|FullyQualifiedName~ResolveReview_MultiPlayer_CapsConfidence"`
Expected: FAIL, `TwoPlayersOpenFarApart` sees no flag (old timing rule); `CapsConfidence` sees `AnyLiveTarget == true` (locked clip).

- [ ] **Step 3: Widen the multi-player trigger**

In `clipmeta.core/Watching/ReviewBindingResolver.cs`, replace the `multiPlayer` detection in `ComputeHeuristic` (~line 82-95):

```csharp
        // Ambiguity: another player produced an OPEN segment within the threshold window of `current`.
        bool multiPlayer = ordered.Any(s =>
            !string.Equals(s.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            (current.StartedAt - s.StartedAt).Duration() <= threshold &&
            s.EndedAt is null);
        if (multiPlayer)
            return new ReviewBinding(
                null, null, 0, true,
                new[]
                {
                    new ReviewFlag(
                        ReviewFlag.TypeMultiplePlayersActive,
                        NamesOf(ordered.Where(s => s.EndedAt is null))),
                });
```

with:

```csharp
        // Ambiguity (#2): two or more distinct players currently have an OPEN segment. Any such
        // overlap is too ambiguous to bind, independent of when each started (the old near-
        // simultaneous-start rule missed players opened seconds apart, the common case).
        List<TitleSegment> openSegments = ordered.Where(s => s.EndedAt is null).ToList();
        int openPlayers = openSegments
            .Select(s => s.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (openPlayers > 1)
            return new ReviewBinding(
                null, null, 0, true,
                new[] { new ReviewFlag(ReviewFlag.TypeMultiplePlayersActive, NamesOf(openSegments)) });
```

- [ ] **Step 4: Add the confidence cap in `ResolveReview`**

In `clipmeta.core/Watching/WatchingResolver.cs`, in `ResolveReview`, replace the tail (~line 132-137):

```csharp
        // A corrected/confident bind is a live target even when unlocked.
        bool anyLive = core.AnyLiveTarget || confident;

        return new WatchingResult(
            candidates, core.Diagnostics, anyLive, binding.Flags, boundId, confident);
```

with:

```csharp
        // A corrected/confident bind is a live target even when unlocked.
        bool anyLive = core.AnyLiveTarget || confident;

        // #2 cap: when two or more players are open, the bind is ambiguous, force confirm-first.
        // Nothing is an auto-tag target and no candidate may read high, even a locked one.
        if (binding.Flags.Any(f => f.Type == ReviewFlag.TypeMultiplePlayersActive))
        {
            anyLive = false;
            candidates = candidates
                .Select(c => c.Confidence == HighConfidence ? c with { Confidence = LowConfidence } : c)
                .ToList();
        }

        return new WatchingResult(
            candidates, core.Diagnostics, anyLive, binding.Flags, boundId, confident);
```

- [ ] **Step 5: Run the review suite to verify pass + no regressions**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter "FullyQualifiedName~ResolveReview|FullyQualifiedName~ReviewBindingResolver"`
Expected: PASS, the two new tests plus existing `ResolveReviewTests` / `ReviewBindingResolverTests` (the existing near-simultaneous `ResolveReview_MultiPlayer_NoCorrection_FlagAndWarn` still fires the flag under the widened rule).

- [ ] **Step 6: Commit**

```bash
git add clipmeta.core/Watching/ReviewBindingResolver.cs clipmeta.core/Watching/WatchingResolver.cs clipmetascribe.Tests/ResolveReviewTests.cs
git commit -m "fix(watching): multiplePlayersActive fires for any two open players and caps confidence (#2)"
```

---

## Task 4: #6, `ReviewFlagResolver` cleans advisory clip names + wire into `ResolveReview`

**Files:**
- Create: `clipmeta.core/Watching/ReviewFlagResolver.cs`
- Create: `clipmetascribe.Tests/ReviewFlagResolverTests.cs`
- Modify: `clipmeta.core/Watching/WatchingResolver.cs` (`ResolveReview`, pass `binding.Flags` through the resolver before returning)

**Interfaces:**
- Consumes: `ReviewFlag` (`Type, Clips, StableSeconds`), `WatchContext.ByFileName` (`IReadOnlyDictionary<string, IReadOnlyList<LibraryClip>>`), `LibraryTitleMatcher.FindBestMatch(string?, IEnumerable<string>) → string?`.
- Produces: `public static IReadOnlyList<ReviewFlag> ReviewFlagResolver.Resolve(IReadOnlyList<ReviewFlag> flags, WatchContext context)`, same flags with each `Clips` entry resolved to a library basename, unresolvable entries dropped, remainder deduped (OrdinalIgnoreCase, first-seen order); `Type` and `StableSeconds` unchanged.

**Background:** `ReviewFlag.Clips` currently carries raw window titles (`TitleSegment.RawTitle`), clean for MPC (full path), garbled for VLC (bare name or `"vlc"`), and duplicated when a clip replays. Resolve each to its library basename using the same matcher the resolver trusts; drop foreign/unrecognized; dedup.

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/ReviewFlagResolverTests.cs`:

```csharp
using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ReviewFlagResolverTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Done() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private void Touch(string name) => File.WriteAllBytes(Path.Combine(_dir, name), Array.Empty<byte>());

    private WatchContext Context() =>
        WatchContext.Build(_dir, Array.Empty<ProcessWindow>());

    [TestMethod]
    public void Resolve_RawTitle_BecomesLibraryBasename()
    {
        Touch("_3.mp4");
        var flags = new[]
        {
            new ReviewFlag(ReviewFlag.TypeSequenceSkip, new[] { "00:12 / 01:00 - _3.mp4 - MPC-HC" }),
        };

        IReadOnlyList<ReviewFlag> resolved = ReviewFlagResolver.Resolve(flags, Context());

        CollectionAssert.AreEqual(new[] { "_3.mp4" }, resolved[0].Clips.ToList());
    }

    [TestMethod]
    public void Resolve_UnresolvableEntries_AreDropped()
    {
        Touch("_3.mp4");
        var flags = new[]
        {
            new ReviewFlag(ReviewFlag.TypeMultiplePlayersActive, new[] { "vlc", "_3.mp4 - VLC media player" }),
        };

        IReadOnlyList<ReviewFlag> resolved = ReviewFlagResolver.Resolve(flags, Context());

        CollectionAssert.AreEqual(new[] { "_3.mp4" }, resolved[0].Clips.ToList(),
            "the bare 'vlc' token resolves to no library clip and is dropped");
    }

    [TestMethod]
    public void Resolve_DuplicateClip_IsDeduped()
    {
        Touch("DVR_5.mp4");
        var flags = new[]
        {
            new ReviewFlag(ReviewFlag.TypeSequenceSkip, new[]
            {
                "DVR_5.mp4 - VLC media player",
                "DVR_5.mp4 - VLC media player",
                "DVR_5.mp4 - VLC media player",
            }),
        };

        IReadOnlyList<ReviewFlag> resolved = ReviewFlagResolver.Resolve(flags, Context());

        Assert.AreEqual(1, resolved[0].Clips.Count, "repeated clip collapses to one entry");
        Assert.AreEqual("DVR_5.mp4", resolved[0].Clips[0]);
    }

    [TestMethod]
    public void Resolve_PreservesTypeAndStableSeconds()
    {
        Touch("_3.mp4");
        var flags = new[]
        {
            new ReviewFlag(ReviewFlag.TypeAutoCorrected, new[] { "_3.mp4 - MPC-HC" }, StableSeconds: 4.2),
        };

        ReviewFlag resolved = ReviewFlagResolver.Resolve(flags, Context())[0];

        Assert.AreEqual(ReviewFlag.TypeAutoCorrected, resolved.Type);
        Assert.AreEqual(4.2, resolved.StableSeconds, 0.001);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter "FullyQualifiedName~ReviewFlagResolver"`
Expected: FAIL, `ReviewFlagResolver` does not exist (compile error).

- [ ] **Step 3: Create the resolver**

Create `clipmeta.core/Watching/ReviewFlagResolver.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// Rewrites review-flag clip strings (raw player-window titles) into clean, deduped library
/// basenames using the same library-aware matcher the resolver uses, so advisories never expose
/// raw titles, OSD/timecode text, or duplicate entries. Pure: no IO, library identity comes from
/// the supplied <see cref="WatchContext"/>. Flag <see cref="ReviewFlag.Type"/> and
/// <see cref="ReviewFlag.StableSeconds"/> are untouched; only the clip payload changes.
/// </summary>
public static class ReviewFlagResolver
{
    /// <summary>
    /// Returns flags whose <c>Clips</c> are each resolved to a library basename via
    /// <see cref="LibraryTitleMatcher.FindBestMatch"/>, with unresolvable entries (foreign files,
    /// bare process names) dropped and the remainder deduped (OrdinalIgnoreCase, first-seen order).
    /// </summary>
    public static IReadOnlyList<ReviewFlag> Resolve(
        IReadOnlyList<ReviewFlag> flags, WatchContext context)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(context);

        var result = new List<ReviewFlag>(flags.Count);
        foreach (ReviewFlag flag in flags)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clips = new List<string>();
            foreach (string raw in flag.Clips)
            {
                string? name = LibraryTitleMatcher.FindBestMatch(raw, context.ByFileName.Keys);
                if (name is not null && seen.Add(name))
                    clips.Add(name);
            }
            result.Add(flag with { Clips = clips });
        }
        return result;
    }
}
```

- [ ] **Step 4: Run the resolver tests to verify pass**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter "FullyQualifiedName~ReviewFlagResolver"`
Expected: PASS, all four `ReviewFlagResolverTests` green.

- [ ] **Step 5: Wire into `ResolveReview` + add an integration test**

In `clipmeta.core/Watching/WatchingResolver.cs`, in `ResolveReview`, change the final `return` to resolve the flags through the new helper (this builds on the Task 3 cap; the `context` is already in scope from `ResolveCore`):

```csharp
        return new WatchingResult(
            candidates, core.Diagnostics, anyLive,
            ReviewFlagResolver.Resolve(binding.Flags, context), boundId, confident);
```

Add an integration test to `clipmetascribe.Tests/ResolveReviewTests.cs`:

```csharp
[TestMethod]
public void ResolveReview_MultiPlayerFlag_ClipsAreResolvedLibraryNames()
{
    // #6 end-to-end: the multiplePlayersActive advisory lists clean library basenames, not raw
    // VLC titles. (a.mp4 via VLC bare name, b.mp4 via MPC full path → both resolve.)
    Touch("a.mp4");
    Touch("b.mp4");
    var segs = new[]
    {
        new TitleSegment(1, "vlc", "a.mp4 - VLC media player", T0, null),
        new TitleSegment(2, "mpc-hc64", $"{Path.Combine(_dir, "b.mp4")} - MPC-HC", T0.AddSeconds(20), null),
    };

    WatchingResult r = Resolver().ResolveReview(_dir, segs, -1, T0.AddSeconds(40), 5, true);

    ReviewFlag flag = r.Review!.Single(f => f.Type == ReviewFlag.TypeMultiplePlayersActive);
    CollectionAssert.AreEquivalent(new[] { "a.mp4", "b.mp4" }, flag.Clips.ToList(),
        "advisory clips are resolved library basenames, deduped, no raw titles");
}
```

- [ ] **Step 6: Run the full watching + review suites**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter "FullyQualifiedName~Watching|FullyQualifiedName~Review|FullyQualifiedName~ResolveReview"`
Expected: PASS, all watching/review tests green, including the new integration test.

- [ ] **Step 7: Commit**

```bash
git add clipmeta.core/Watching/ReviewFlagResolver.cs clipmeta.core/Watching/WatchingResolver.cs clipmetascribe.Tests/ReviewFlagResolverTests.cs clipmetascribe.Tests/ResolveReviewTests.cs
git commit -m "feat(watching): resolve + dedup review advisory clip names (#6)"
```

---

## Task 5: Version bump to v1.5.0 + PITFALLS entries

**Files:**
- Modify: `clipmetamcp/clipmetamcp.csproj` (lines 8-9)
- Modify: `tools/mcpb-manifest.json` (line 5)
- Modify: `docs/PITFALLS.md` (two new entries at the top of "Field-discovered")

**Interfaces:** none (metadata + docs only).

**Background:** Behavior change to existing tools, no new tools → minor bump. The pack gate requires csproj and manifest to match. `.mcpb` repack is deferred to after the final whole-branch review (pass-5 discipline), this task does NOT repack.

- [ ] **Step 1: Bump the csproj version**

In `clipmetamcp/clipmetamcp.csproj`, change:
```xml
    <AssemblyVersion>1.4.0</AssemblyVersion>
    <InformationalVersion>1.4.0</InformationalVersion>
```
to:
```xml
    <AssemblyVersion>1.5.0</AssemblyVersion>
    <InformationalVersion>1.5.0</InformationalVersion>
```

- [ ] **Step 2: Bump the manifest version**

In `tools/mcpb-manifest.json`, change:
```json
  "version": "1.4.0",
```
to:
```json
  "version": "1.5.0",
```

- [ ] **Step 3: Add PITFALLS entries**

In `docs/PITFALLS.md`, insert at the top of the "Field-discovered" section (immediately after line 9 `## Field-discovered (append here as we go)`):

```markdown
## 2026-06-27, A foreign-player lock must not suppress a fresh in-library save
**Symptom:** With a media player paused on a file OUTSIDE the library, a brand-new game clip saved
INTO the library returned `candidateCount: 0` and a blocking `player_outside_library` warning, the
fresh save was invisible.
**Cause:** `WatchingResolver.ResolveCore`'s `suppressAccessFallback` branch (a player on a foreign
file ⇒ "user isn't gaming") dropped EVERY non-player hit, including the just-saved `recent_write`
gaming candidate. A foreign lock and an in-library save are independent signals, you cannot tag a
foreign file anyway.
**Fix:** A single unambiguous `recent_write` hit (Policy A) survives the suppression; several fresh
saves at once stay suppressed. At the MCP layer, `player_outside_library` demotes to a non-blocking
`advisory` (`player_outside_library_ignored`) whenever a gaming candidate is present, so `warning`
stays semantically "do not tag."
**Lesson:** Two independent suppression conditions that happen to co-occur in one branch (foreign
player + no gaming) will silently couple. When a new signal (gaming `recent_write`) is added, audit
every existing branch that drops "non-player" hits, the new signal is non-player too.

## 2026-06-27, Review advisories must resolve segment titles to library names
**Symptom:** `review[]` advisories listed duplicate entries and, for VLC, raw window-title strings
and the bare process name `"vlc"` instead of clip names; `sequenceSkip` repeated `DVR_5` five times.
**Cause:** `ReviewFlag.Clips` carried `TitleSegment.RawTitle` verbatim via `Display(s)`. MPC titles
are full paths (look clean); VLC titles are the bare filename or `"vlc"` (look garbled); a replayed
clip creates multiple segments with the same title (no dedup). The advisory builder and the
candidate resolver are DIFFERENT sources, `include_access_fallback:false` cleans the candidate list
but NOT the segment-derived advisories (a misdiagnosis to avoid).
**Fix:** `ReviewFlagResolver.Resolve` maps each clip string through `LibraryTitleMatcher`, drops
unresolvable entries, and dedups, wired into `ResolveReview` after the context is built.
**Lesson:** A "residue in the advisory" symptom can have two distinct sources (segment history vs
access-time fallback). Confirm WHICH list the strings come from before designing the fix.
```

- [ ] **Step 4: Verify the build (version consistency)**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors. (The pack gate is exercised at repack time, deferred, but csproj and manifest now both read 1.5.0.)

- [ ] **Step 5: Commit**

```bash
git add clipmetamcp/clipmetamcp.csproj tools/mcpb-manifest.json docs/PITFALLS.md
git commit -m "chore: bump clipmetamcp to v1.5.0 + record pass-6 pitfalls"
```

---

## Post-task (controller, after final whole-branch review)

These are NOT plan tasks, they happen after the final review + any fix wave, per pass-5 discipline:

1. **Full suite green:** `dotnet build --nologo -v q` then `dotnet test --nologo --no-build -v q` (view + mcp + scribe; scribe takes a few minutes, long timeout).
2. **Repack the `.mcpb`:** `tools/pack-mcpb.ps1` (version gate must pass: csproj == manifest == 1.5.0). `dist/clipmeta.mcpb` is git-ignored, do NOT commit it.
3. **Finish the branch** via superpowers:finishing-a-development-branch (push + PR; the owner merges).

---

## Self-Review

**1. Spec coverage:**
- §3.1 #1 core suppression exception → Task 1. ✅
- §3.2 #1 MCP warning→advisory → Task 2. ✅
- §4.1 #2 widen trigger → Task 3 Step 3. ✅
- §4.2 #2 confidence cap → Task 3 Step 4. ✅
- §5.1 `ReviewFlagResolver` → Task 4 Step 3. ✅
- §5.2 wire into `ResolveReview` → Task 4 Step 5. ✅
- §5.3 edge cases (autoCorrected pair degrade; empty multiPlayer clips still fire) → covered by `Resolve_DuplicateClip`/`Resolve_UnresolvableEntries` behavior + the cap firing on count not clips. ✅
- §6 version bump + PITFALLS → Task 5. ✅
- §7 every listed test case has a step. ✅

**2. Placeholder scan:** No TBD/TODO; every code step shows full code; every command has expected output. ✅

**3. Type consistency:** `ForeignNoticeIsBlocking(IReadOnlyList<WatchingCandidate>)`, `ReviewFlagResolver.Resolve(IReadOnlyList<ReviewFlag>, WatchContext)`, `WatchingCandidate` 9-arg positional ctor, `ReviewFlag(Type, Clips, StableSeconds)`, `LibraryTitleMatcher.FindBestMatch(string?, IEnumerable<string>)`, `WatchContext.Build(root, IReadOnlyList<ProcessWindow>)`, all match the real signatures. ✅

**Note for executor (pre-flight):** Task 1 intentionally edits two existing test fixtures (`Resolve_PlayerOnForeignFile_NoResolution_WarnsAndSuppressesFallback`, `Resolve_BareNameForeignFile_HasNoForeignDirectory`) from `Touch` to `TouchStale`. This is a deliberate, spec-aligned behavior change (a fresh in-library clip is now a gaming candidate even under a foreign player), NOT a masked regression, call it out to the task reviewer so it is adjudicated as correct-new-behavior.
