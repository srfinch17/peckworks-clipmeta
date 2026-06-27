# Resolver & Advisory Hardening (pass-6) — Design Spec

**Date:** 2026-06-27
**Status:** Approved — ready for implementation plan
**Version target:** clipmetamcp **v1.5.0** (behavior change to existing tools; no new tools)
**Predecessor:** `2026-06-26-resolver-queue-trust-hardening-design.md` (pass-5, v1.4.0)

---

## 0. Reconciliation — what the dogfood log got right, wrong, and by-design

This spec follows the standing discipline: a dogfood transcript is a **symptom log**, not a
diagnosis. Every finding in the v1.4.0 dogfood (`C:\Users\srfin\Videos\ClipmetaTesting`, 41 clips)
was reconciled against the real code **before** scoping. The verdicts:

| Log finding | Verdict | Evidence |
|---|---|---|
| **#1 Foreign-player lock suppresses a valid gaming write** | ✅ Real, diagnosis correct | `WatchingResolver.ResolveCore` line 161 (`suppressAccessFallback`) + 185–186 drop `recent_write` too |
| **#2 `multiplePlayersActive` never fires with 2 players open** | ✅ Real gap (log's "lossy enumeration" framing wrong) | `ReviewBindingResolver.ComputeHeuristic` 83–86 only fires on near-simultaneous starts; `WindowsProcessWindowSource` enumeration is NOT lossy |
| **#6 advisory dedup + garbled VLC titles** | ✅ Real | `ReviewBindingResolver.Display` returns `RawTitle`; duplicate segments not deduped |
| **#7 "the advisory residue is all access-time fallback"** | ⚠️ **Partial misdiagnosis** | `review[]` clips come from `ReviewWatcher` **segment history**, NOT the access-time fallback. `include_access_fallback:false` would NOT clean `sequenceSkip`. Two separate sources conflated. |
| **#3 MPC ghost reference** | 🔁 Deferred — needs live repro | "Frozen `stableSeconds`" is inconsistent with an open segment (its duration would *grow*); can't confirm statically |
| **#4 `written:[]` while success under `autoFlushed`** | 🟢 By-design (pass-5) — not a bug | `drainedFromQueue.written` = synchronous drains; pump auto-flush reports under `autoFlushed`. Reporting-clarity ask, deferred |
| **#5 VLC bare-name high-confidence risk** | 🟢 Narrower than claimed | `WatchingResolver` 226–227 already demotes an unlocked bare-name match to low + `NotLockedNote`; only a *locked* same-named in-library file is exposed |

**In scope for pass-6:** #1 (P0), #2 + #6 (P1).
**Out of scope (deferred, documented):** #3 (live repro first), #4 (reporting clarity), #5 (narrow),
#7's access-fallback-default question, and the log's larger "live session-start index as primary
identity" rework (the `KnownBaselinePaths` + `recent_write` substrate already half-implements it;
#1 is what unblocks it).

---

## 1. Problem statement

During a live v1.4.0 dogfood, three resolution defects surfaced (the **write** path was flawless —
all 17 tags landed):

1. **#1 (P0):** With a media player open on a file *outside* the library, a brand-new game clip saved
   *into* the library was ignored entirely — `candidateCount: 0` + a blocking `player_outside_library`
   warning — because a foreign-player lock and a fresh library write are independent signals that the
   resolver wrongly couples. You cannot tag a foreign file anyway, so a foreign lock must carry zero
   weight against a legitimate in-library gaming target.

2. **#2 (P1):** With two players open, `multiplePlayersActive` never fired, and the heuristic could
   hand back a recency pick at confidence with no signal that a second player existed.

3. **#6 (P1):** The `review[]` advisories listed duplicate entries and, for VLC, raw window-title
   strings / bare `"vlc"` process names instead of resolved library names.

---

## 2. Scope

**In:**
- #1 — a lone unambiguous `recent_write` (Policy A) survives the foreign-player suppression; the
  `player_outside_library` warning demotes to a non-blocking advisory when a gaming target exists.
- #2 — `multiplePlayersActive` fires whenever ≥2 recognized players have an **open** segment
  (regardless of start timing), and `ResolveReview` then caps confidence (no auto-bind).
- #6 — a new pure `ReviewFlagResolver` resolves each advisory's clip strings to library basenames,
  drops unresolvable ones, and dedups; wired into `ResolveReview`.
- Version bump v1.4.0 → v1.5.0 (csproj + manifest), `.mcpb` repack, PITFALLS entries.

**Out:** #3, #4, #5, #7's access-fallback default, the index-as-primary-identity rework, any new MCP
tool, any CLI surface change.

---

## 3. #1 — Gaming candidate beats foreign-player suppression

### 3.1 Core: `WatchingResolver.ResolveCore`

The suppression branch currently drops *every* non-player hit when a foreign player is open:

```csharp
// existing — WatchingResolver.cs ~185
if (!hasPlayer && suppressAccessFallback)
    continue;
```

Add a **single exception** — a lone unambiguous `recent_write` hit (Policy A: exactly one fresh save)
survives:

```csharp
if (!hasPlayer && suppressAccessFallback)
{
    // A single fresh game-save (Policy A) is a valid live target even though a foreign player is
    // open — the foreign lock and an in-library save are independent signals. Several saves at once
    // stay suppressed (ambiguous → not Policy A).
    bool soleFreshSave = hasRecentWrite &&
        hits.Any(h => h.Source == RecentWriteSignal.SourceName && !h.Ambiguous);
    if (!soleFreshSave)
        continue;
}
```

That single save then flows through the **unchanged** scorer: `recentWriteUnambiguous` → high
confidence → `anyLiveTarget = true`. Access-time hits are still suppressed (only `recent_write` gets
the exception), and multiple concurrent saves (each `Ambiguous`) stay suppressed.

### 3.2 MCP: `ReadTools` watching handler — warning → advisory demotion

Today `player_outside_library` is emitted as a blocking `warning` ("Do not tag") whenever
`UnresolvedPlayers.Count > 0`. New rule:

- If the candidate list contains a candidate whose `source == "recent_write"`, the foreign-player
  payload is emitted as a **non-blocking `advisory`** (new response key), NOT as `warning`:
  ```json
  "advisory": {
    "type": "player_outside_library_ignored",
    "message": "A media player is showing a file outside the library, but a fresh in-library save was detected — the gaming candidate below is the live target. The foreign player was ignored.",
    "unresolvedPlayers": [ { "player": ..., "referencedName": ..., "foreignDirectory": ... } ]
  }
  ```
- Otherwise (no `recent_write` candidate present), the existing blocking `warning` of type
  `player_outside_library` is emitted exactly as today.

This keeps `warning` semantically pure ("do not tag") and never places it beside an auto-taggable
high-confidence target.

---

## 4. #2 — `multiplePlayersActive` fires for any two open players + caps confidence

### 4.1 Widen the trigger: `ReviewBindingResolver.ComputeHeuristic`

Replace the near-simultaneous-start condition:

```csharp
// existing — fires only when two OPEN segments start within `threshold` of each other
bool multiPlayer = ordered.Any(s =>
    !string.Equals(s.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase) &&
    (current.StartedAt - s.StartedAt).Duration() <= threshold &&
    s.EndedAt is null);
```

with **"≥2 distinct processes currently have an open segment"**:

```csharp
List<string> openProcesses = ordered
    .Where(s => s.EndedAt is null)
    .Select(s => s.ProcessName)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();
bool multiPlayer = openProcesses.Count > 1;
```

The branch behavior is unchanged: it returns `Chosen = null` + a `multiplePlayersActive` flag whose
`Clips` are the open segments' titles (later cleaned by §5).

### 4.2 Cap confidence: `WatchingResolver.ResolveReview`

A null `Chosen` cold-starts into `ResolveCore`, which can still report `AnyLiveTarget = true` when
both clips are locked (`InUse`). To honor "no auto-tag," after computing `core` and `binding`,
`ResolveReview` detects the `multiplePlayersActive` flag and, when present:

- forces `anyLive = false`, and
- demotes every returned candidate's `Confidence` to `LowConfidence`.

The caller still receives the candidate list and the advisory but must confirm a path before tagging
— never a silent recency pick.

### 4.3 Scope note

This stays a review-mode (segment-based) advisory, consistent with the current architecture. The
pure `Resolve` path (no watcher) is effectively test-only in production: `Program.cs` always starts a
`ReviewWatcher` when a library is configured, so `library_watching` always runs `ResolveReview`.

---

## 5. #6 — Advisory dedup + resolve segment titles to library names

### 5.1 New pure helper: `ReviewFlagResolver` (core)

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// Rewrites review-flag clip strings (raw player-window titles) into clean, deduped library
/// basenames using the same library-aware matcher the resolver uses, so advisories never expose
/// raw titles or duplicate entries. Pure: no IO, library identity comes from the supplied context.
/// </summary>
public static class ReviewFlagResolver
{
    /// <summary>
    /// Returns flags whose <c>Clips</c> are each resolved to a library basename via
    /// <see cref="LibraryTitleMatcher.FindBestMatch"/>, with unresolvable entries dropped and the
    /// remainder deduped (OrdinalIgnoreCase, first-seen order). Flag types, firing, and
    /// <c>StableSeconds</c> are untouched — only the clip payload changes.
    /// </summary>
    public static IReadOnlyList<ReviewFlag> Resolve(
        IReadOnlyList<ReviewFlag> flags, WatchContext context);
}
```

Per clip string: `LibraryTitleMatcher.FindBestMatch(clip, context.ByFileName.Keys)`.
- resolves → the clean basename;
- doesn't resolve (foreign file, bare `"vlc"`) → dropped;
- dedup within each flag by resolved basename (OrdinalIgnoreCase, first-seen order preserved).

### 5.2 Wiring: `ResolveReview`

After `ResolveCore` builds `context` and before returning the `WatchingResult`, pass `binding.Flags`
through `ReviewFlagResolver.Resolve(flags, context)`. `ReadTools` keeps dumping `result.Review`
verbatim — no MCP-layer change for #6.

### 5.3 Edge cases (explicit)

- **`autoCorrected`** carries an ordered pair `[chosen, correctedFrom]`. Both are clips the user
  watched (in-library) and normally resolve; if one fails to resolve, the entry degrades to a single
  name rather than breaking. Accepted.
- A **`multiplePlayersActive`** flag whose open players are all foreign resolves to an empty `Clips`
  list but **still fires** — the advisory is about player *count*, not clip names.

---

## 6. Components & files

| File | Change |
|---|---|
| `clipmeta.core/Watching/WatchingResolver.cs` | §3.1 suppression exception; §4.2 multi-player confidence cap in `ResolveReview`; §5.2 wire `ReviewFlagResolver` |
| `clipmeta.core/Watching/ReviewBindingResolver.cs` | §4.1 widen `multiPlayer` trigger |
| `clipmeta.core/Watching/ReviewFlagResolver.cs` | §5.1 new pure helper |
| `clipmetamcp/Tools/ReadTools.cs` | §3.2 warning → advisory demotion |
| `clipmetamcp/clipmetamcp.csproj` + `tools/mcpb-manifest.json` | v1.4.0 → v1.5.0 |
| `docs/PITFALLS.md` | foreign-suppression exception + segment-title-resolution gotchas |
| `dist/clipmeta.mcpb` | repacked (git-ignored, not committed) |

No new MCP tool; no CLI surface change; zero new NuGet packages.

---

## 7. Testing

**Core (`clipmetascribe.Tests` watching suite, MSTest):**
- #1a — foreign player open + **single** fresh `recent_write` save ⇒ gaming candidate surfaces,
  `Confidence == high`, `AnyLiveTarget == true`.
- #1b — foreign player open + **multiple** fresh saves ⇒ all suppressed (no gaming candidate).
- #1c — foreign player open + access-time-only stale clips (no fresh save) ⇒ still suppressed.
- #2a — two players with open segments started far apart ⇒ `multiplePlayersActive` fires.
- #2b — `ResolveReview` with `multiplePlayersActive` ⇒ `AnyLiveTarget == false`, all candidates low.
- #6a — `ReviewFlagResolver` resolves raw titles to basenames.
- #6b — drops foreign / bare `"vlc"` entries.
- #6c — dedups repeated clip (e.g. `DVR_5` ×5 → one).

**MCP (`clipmetamcp.Tests`, run the FULL project — tool-surface assertion):**
- #1d — `recent_write` candidate present + foreign player ⇒ response has `advisory`
  (`player_outside_library_ignored`), no blocking `warning`.
- #1e — no live target + foreign player ⇒ blocking `warning` (`player_outside_library`) as today.
- #6d — `review[]` clips render clean, deduped library names.

**Full suite** (`clipmetascribe.Tests`, `clipmetamcp.Tests`, `clipmetaview.Tests`) green before repack.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| #1 surfaces an unrelated `recent_write` past suppression | Existing Policy A (single save) + `SelfActionLedger` + creation-time + index baseline gate `recent_write`; no new exposure |
| #1 × #2 interaction: fresh save *and* ≥2 players open | #2's cap wins — even the gaming candidate requires confirmation. Intentional (safest); documented |
| #2 friction: foreign player paused while reviewing a library clip in another player counts as 2 open players → confirm-before-tag | Accepted cost of the simple safe rule (you chose "advise + cap"); a future refinement could cap only when ≥2 resolve in-library |
| §5 resolution mis-maps a title to the wrong basename | Reuses the proven `LibraryTitleMatcher` (longest-boundary match) already trusted by the resolver; no new matching logic |

---

## 9. Definition of Done

1. `dotnet build` — 0 warnings, 0 errors, all projects.
2. `dotnet test` — all pass, including the new #1/#2/#6 cases + real-clip integration + media-integrity.
3. Zero NuGet packages added to production projects.
4. v1.4.0 → v1.5.0 in csproj **and** manifest (pack gate enforces equality); `.mcpb` repacked.
5. New gotchas recorded in `docs/PITFALLS.md`.
6. `warning` remains semantically "do not tag"; the foreign-player demotion uses the separate
   non-blocking `advisory` key.
