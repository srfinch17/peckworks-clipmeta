# Review-Mode Resolver Time-Base Split (pass-7), Design Spec

**Date:** 2026-06-28
**Status:** Draft, pending owner review (authored autonomously from the v1.5.0 dogfood; owner asleep, cleared design decisions)
**Version target:** clipmetamcp **v1.6.0** (behavior change to existing tools; no new tools), see §10 for the v1.0.0-reset interaction
**Predecessor:** `2026-06-27-resolver-advisory-hardening-design.md` (pass-6, v1.5.0)
**Dogfood source:** v1.5.0 live run, `C:\Users\srfin\Videos\ClipmetaTesting` (41→51 clips), review mode

---

## 0. Reconciliation, what the dogfood log got right, wrong, and by-design

Standing discipline: a dogfood transcript is a **symptom log**, not a diagnosis. Every v1.5.0 finding
was reconciled against the real code **before** scoping. The log was unusually rigorous (OS process
dumps proving the ghosts), but **both HIGH findings named the wrong mechanism**, the symptoms are
real, the causes are not where the log placed them.

| Log finding | Verdict | Evidence |
|---|---|---|
| **§4.1 Foreign warning blanks candidates; `anyLiveTarget:true`+`candidates:[]`** | ✅ Real bug, ❌ wrong mechanism | Impossible in live `Resolve` (both fields come from one `WatchingResult`; `anyLiveTarget` is derived FROM the final list at `WatchingResolver.cs:301`). It only happens in **`ResolveReview`**: line **113** strips the `recent_write` candidate, line **133** then reads the *stale pre-strip* `core.AnyLiveTarget`. The log's "foreign branch short-circuits candidate building" is wrong, `ResolveCore` already decouples them (pass-6 Policy A works there). |
| **§4.2 Ghosts: player reported open after close (MPC + VLC, OS-proven)** | ✅ Real bug, ❌ wrong mechanism | Eviction is NOT broken, `ReviewWatcher.PollOnce:101-102` closes vanished players and removes them from `_openByProcess`. The ghost is a **closed segment lingering in the 64-slot ring buffer** (`ReviewWatcher.cs:123-132`) that `ReviewBindingResolver.ComputeHeuristic` binds as `ordered[^1]` (most-recent, open OR closed; `:80,96`), which `ResolveReview` then replays as a **synthetic `ProcessWindow`** (`WatchingResolver.cs:97-99`) → foreign diagnostics over a dead player. "MPC ghost overwritten by VLC ghost" = VLC's segment simply became newest, not slot reuse. |
| **§4.4 `written:[]` while the write lands under `autoFlushed`** | ✅ Real UX bug, ❌ "wrong channel" framing | The write isn't mis-routed, `library_queue_tag` calls `pump?.Wake()` unconditionally (`QueueTools.cs:148`); the **purely event-driven** `QueueDrainPump` drains the *unlocked* clip immediately and books it under `autoFlushed` before the explicit `flush_queue` runs (queue already empty → `written:[]`). = pass-6 deferred **#4**, now reproduced + root-caused. |
| **§4.5 recent_write window "too short/timing-sensitive" (153 fresh mtime, `false`)** | ⚠️ Likely misdiagnosed | Window is **5 min on CreationTime** (`RecentWriteSignal.cs:18,46`), not short. The likelier cause is the **`KnownBaselinePaths` exclusion** (`:44`): a clip already in the index can never be a "fresh save," even with bumped creation time. **Verify in tomorrow's run**, do not change the window blind. |
| **§6 ClipMeta's own writes bump mtime → recent_write self-false-positive** | 🟢 Already guarded (pass-5) | Signal keys on **CreationTime**, not write-time, AND excludes self-written paths via `SelfActionLedger` (`RecentWriteSignal.cs:46,48`). The mtime bump only re-sorts `library_list`, which the log correctly rules a non-defect. |
| **§7 perf (3,700 clips fast; index-read doesn't rescan)** | 🟢 Confirmed-good | `staleClipCount` already tracks deltas (`ReadTools.cs:716`); incremental rebuild is a real but **separate** opportunity. |

**The deep cause of §4.1 + §4.2 is ONE design flaw:** `ResolveReview` conflates two questions that
need **different time-bases**, 

- **"Which clip is the user *describing*?"**, legitimately **historical** (segment history; that is
  the entire point of review mode, bind what played when they spoke).
- **"Is a foreign player open *right now*?"**, must be **live**.

`ResolveReview` derives **both** from one synthetic `windows` list built from `binding.Chosen`, a
possibly-closed, possibly-foreign segment. Split those time-bases and both HIGH findings dissolve.

**Why pass-6's #1 fix + tests didn't catch this:** Policy A was added to `ResolveCore`, and its tests
exercise `ResolveCore` / the pure `ForeignNoticeIsBlocking` with **hand-built candidate lists**
(`LibraryWatchingToolTests.cs:203-213`). None drives the full `ResolveReview` with a foreign-player
**segment** present, so none reproduces the line-113 strip that negates Policy A in the real path, 
which `Program.cs` always runs (a `ReviewWatcher` is always started). Textbook "fix tested in path A,
product uses path B." **The missing integration test is part of this fix, not an afterthought.**

**In scope for pass-7:** §4.1 + §4.2 (one root cause, P0); §4.4 (P1, cheap, same response code).
**Out of scope (documented):** §4.5 (verify first), §6 (already handled), §7 incremental indexing,
the larger "live session-start index as primary identity" rework.

---

## 1. Problem statement

In a live v1.5.0 review-mode dogfood, the **write path was again flawless** (all tags landed,
verified on disk). Resolution broke in three ways, all traceable to two roots:

1. **§4.1 + §4.2 (P0), review mode binds and diagnoses against the wrong time-base.** With any media
   player open (or *recently closed*) on a file outside the library, `library_watching` returned
   `candidates: []` plus a blocking `player_outside_library` warning, even when a fresh in-library
   game-save existed (`anyLiveTarget: true` beside an empty list). The foreign player did not even
   have to be open: a player closed minutes earlier kept producing the warning (the ghost), because
   its dead segment is replayed as a live window. This silently breaks game-mode tagging whenever a
   player is, or recently was, open, and a pure automation caller (no `library_list` fallback) tags
   nothing.

2. **§4.4 (P1), queued-then-flushed tags report `written: []`.** The success surface a consumer
   reads (`written`) is empty because the background pump wins the race and books the write under
   `autoFlushed`. No data loss, but a pipeline checking `written` believes nothing was written.

---

## 2. Scope

**In:**
- **§4.1/§4.2, split the time-bases in `ResolveReview`:** foreign-player diagnostics and access
  suppression come from a **live** poll; the "what you watched" bind comes from **segment history**;
  `anyLiveTarget` and the warning/advisory decision are derived from the **final** candidate list so
  `anyLiveTarget:true`+`candidates:[]` is impossible by construction, and a closed player can never
  raise `player_outside_library`.
- **The Policy-A invariant survives review mode:** a lone unambiguous `recent_write` save is returned
  and taggable whether a foreign player is open, closed, or absent.
- **§4.4, conditional pump wake:** `library_queue_tag` wakes `QueueDrainPump` **only when the clip is
  currently locked**; an unlocked queued tag lands via the foreground drain and appears in `written`.
- New integration tests through `ResolveReview` with a foreign-player segment (the gap above); a
  property test asserting the `anyLiveTarget ⇒ candidates≠∅` invariant.
- Version bump v1.5.0 → v1.6.0 (csproj + manifest), `.mcpb` repack, PITFALLS entries.

**Out:** §4.5 (verify in tomorrow's run before any change), §6 (already guarded), §7 incremental
indexing, the "index-as-primary-identity" rework, any new MCP tool, any CLI surface change, the
v1.0.0 release reset (still the planned *final* step per `project_v1_release_versioning`, see §10).

---

## 3. §4.1 + §4.2, Live diagnostics, historical binding

### 3.1 The principle

`ResolveReview` must answer two questions from two sources:

| Question | Source | Today (broken) | Pass-7 |
|---|---|---|---|
| Is a foreign player open *now*? (diagnostics, access-suppression) | **Live poll** | the chosen segment's synthetic window | live `GetPlayerWindows` |
| Which clip is the user *describing*? (the review bind) | **Segment history** | (same synthetic window) | `binding.Chosen` resolved against the library |
| Is there a fresh save / recent access? (gaming, fallback) | **Library** (window-independent) | computed over the synthetic window's context | computed over the live context |

### 3.2 New `ResolveReview` flow

```
binding   = ReviewBindingResolver.Resolve(segments, lastBoundId, now, …, spokenAt)   // unchanged
live      = _windowSource.GetPlayerWindows(_playerNames)        // reality NOW (closed players absent)
context   = WatchContext.Build(libraryRoot, live, _ledger)
core      = ResolveCore(context, limit, includeAccessFallback)  // == live-mode resolve:
                                                                //   correct foreign diagnostics,
                                                                //   correct suppression,
                                                                //   gaming Policy A + access fallback
candidates = core.Candidates.ToList()
anyLive    = core.AnyLiveTarget
boundId    = null;  confident = false

// Overlay the review bind ONLY when the described clip resolves in-library.
if (binding.Chosen is { } sel
    && LibraryTitleMatcher.FindBestMatch(sel.RawTitle, context.ByFileName.Keys) is { } basename)
{
    // A real "what you watched" bind: it IS the answer, so a background save is noise here.
    // Build/locate the bound clip as a high-confidence player-title candidate (probe its lock),
    // dedup by path against `core`, drop recent_write + access rows, apply the corrected note.
    candidates = [ boundCandidate(high, CorrectedBindNote if binding.CorrectedFrom is not null) ];
    anyLive    = true;
    boundId    = sel.Id;  confident = true;
}
// else: chosen is foreign/closed/unresolved → NO review bind. Leave `core` as-is:
//       gaming Policy A or access fallback stands; foreign (if any LIVE player) demotes to advisory.

// Multi-player cap (pass-6 #2, unchanged): on multiplePlayersActive → anyLive=false, demote highs.
…

// Single source of truth: recompute anyLive from the FINAL list (same rule as ResolveCore:301-304),
// so anyLiveTarget:true + candidates:[] is structurally impossible.
anyLive = FinalListHasLiveTarget(candidates) && anyLive;   // cap can only lower it

return new WatchingResult(
    candidates, core.Diagnostics, anyLive,
    ReviewFlagResolver.Resolve(binding.Flags, context), boundId, confident);
```

Key differences from today:
- **Diagnostics come from `core` built on LIVE windows** → a closed MPC/VLC is simply not present, so
  `UnresolvedPlayers` is empty and no `player_outside_library` warning is raised. **Ghosts gone.**
- **The `recent_write` strip is conditional** on a *resolved in-library* review bind existing. When
  the chosen segment is foreign/closed (the dogfood case), the strip never runs, so Policy A's gaming
  candidate survives and the MCP layer (unchanged §3.2 of pass-6) demotes the foreign notice to the
  non-blocking advisory. **§4.1 fixed.**
- **`anyLiveTarget` is derived from the final candidate list**, never carried stale. **The
  `true`+`[]` contradiction cannot occur.**

### 3.3 What stays exactly as-is (do not regress)

- The previous-stable correction and `CorrectedBindNote` (a clip you watched then advanced past is
  still bindable and writable), now expressed via the overlay, with the same note semantics.
- `spoken_at` exact binding (AC2), `binding.Chosen` already encodes the spoken-at hit; the overlay
  resolves it identically.
- The pass-6 `multiplePlayersActive` cap and `ReviewFlagResolver` cleanup.
- The live (`Resolve`, no-watcher) path is untouched, it was never broken.

### 3.4 Component seam

The overlay needs to (a) resolve `sel.RawTitle` to a library clip and (b) build that clip as a
high-confidence candidate with a real lock probe. `LibraryTitleMatcher.FindBestMatch` +
`context.ByFileName` already give (a); (b) reuses the same `WatchingCandidate` shape + `LockProbe`
the resolver uses. If the bound clip is also live (its player is still open), it already appears in
`core.Candidates`, **dedup by full path**, promoting in place rather than duplicating. Prefer a
small private helper (`OverlayReviewBind`) over inlining, to keep `ResolveReview` readable.

---

## 4. §4.4, Conditional pump wake so `written` is authoritative

### 4.1 The change: `QueueTools.QueueTag`

```csharp
// existing, QueueTools.cs ~148
pump?.Wake();
```

becomes:

```csharp
// Only the background pump should land a LOCKED clip (zero-touch on player close). An UNLOCKED
// queued tag must be left for the foreground drain (this call's pre-enqueue drain missed it; the
// next watched-clip call or an explicit library_flush_queue lands it) so it reports under `written`,
// not `autoFlushed`. The pump is purely event-driven (idle WaitAny), so not waking it = it stays
// asleep and never races the foreground flush.
if (LockProbe.IsInUse(fullPath))
    pump?.Wake();
```

This is sound because `QueueDrainPump.Loop` idles on `WaitHandle.WaitAny` and only drains after a
`Wake` (`QueueDrainPump.cs:82-90`), there is no idle timer to grab the unlocked tag first.

### 4.2 Result

- **Dogfood's exact path** (queue an unlocked clip → `flush_queue`): pump not woken → tag waits →
  `flush_queue`'s `DrainUnderGate` lands it → **`written: [clip]`**, `autoFlushed: []`. Fixed.
- **Locked clip** (player still open): pump woken → drains on lock-clear → `autoFlushed` (correct;
  no foreground call needed). Zero-touch preserved.
- `flush_queue` keeps surfacing `autoFlushed` too, so `written ∪ autoFlushed` remains the complete
  "what landed" view for the locked case. (No response-shape change; this is the documented union.)

### 4.3 Accepted edge

An *unlocked* tag queued as the very last action with **no** subsequent flush in the same session
will not auto-land until the next session (durable queue). This is acceptable: an unlocked clip is
directly writable (`clip_set_fields`) and need not be queued at all; durability still guarantees no
loss. Zero-touch's purpose, the *locked* last clip landing on player-close, is fully preserved.

---

## 5. Components & files

| File | Change |
|---|---|
| `clipmeta.core/Watching/WatchingResolver.cs` | §3.2 rewrite `ResolveReview`: live diagnostics context, conditional `recent_write` strip via `OverlayReviewBind`, final-list `anyLiveTarget` recompute |
| `clipmeta.core/Watching/ReviewBindingResolver.cs` | none expected (binding logic unchanged); confirm `binding.Chosen` may be a closed segment (it can) |
| `clipmetamcp/Tools/QueueTools.cs` | §4.1 conditional `pump?.Wake()` |
| `clipmetamcp/Tools/ReadTools.cs` | none expected, the warning/advisory split already keys off `result.Candidates`, which is now correct |
| `clipmetamcp/clipmetamcp.csproj` + `tools/mcpb-manifest.json` | v1.5.0 → v1.6.0 |
| `docs/PITFALLS.md` | review-mode time-base split; conditional pump wake |
| `dist/clipmeta.mcpb` | repacked (git-ignored, not committed) |

No new MCP tool; no CLI surface change; zero new NuGet packages.

---

## 6. Testing

**Core (`clipmetascribe.Tests` watching suite, MSTest), drive the FULL `ResolveReview`:**
- **§4.1a**, foreign-player **segment present** + single fresh `recent_write` save ⇒ gaming
  candidate surfaces, `Confidence == high`, `AnyLiveTarget == true`, candidate list non-empty. *(The
  exact case pass-6 missed.)*
- **§4.2a (ghost)**, foreign-player segment **closed** (EndedAt set) + single fresh save ⇒ no
  `player_outside_library` in diagnostics, gaming candidate present, `AnyLiveTarget == true`.
- **§4.2b (ghost, no save)**, foreign-player segment closed + nothing fresh ⇒ empty/low candidates,
  `AnyLiveTarget == false`, **no** foreign warning (the closed player is gone).
- **Invariant**, property/assert across `ResolveReview` scenarios: `AnyLiveTarget == true ⇒
  Candidates.Count > 0`.
- **Regression**, in-library clip watched then advanced (previous-stable) still binds high with
  `CorrectedBindNote`; `recent_write` IS dropped when a real in-library bind exists; multi-player cap
  still forces confirm.

**MCP (`clipmetamcp.Tests`, run the FULL project, tool-surface assertion):**
- **§4.1b**, review-mode foreign player + fresh save ⇒ response has `advisory`
  (`player_outside_library_ignored`), **no** blocking `warning`, `candidateCount > 0`,
  `anyLiveTarget: true`.
- **§4.2c**, review-mode foreign player **closed** ⇒ no `warning`, no ghost `unresolvedPlayers`.
- **§4.4a**, `library_queue_tag` (unlocked clip) → `library_flush_queue` ⇒ clip in `written`,
  `autoFlushed` empty.
- **§4.4b**, `library_queue_tag` (locked clip, faked `isInUse`) ⇒ pump path / `autoFlushed`
  unchanged; `written` empty until lock clears.

**Full suite** (`clipmetascribe.Tests`, `clipmetamcp.Tests`, `clipmetaview.Tests`) green before
repack. Per CLAUDE.md, run the FULL `clipmetamcp.Tests` (the tool-surface assertion lives outside the
diff).

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| Switching diagnostics to a live poll changes behavior when a player IS still open | Intended: an open foreign player still appears live and still demotes/blocks per pass-6 rules. Only *closed* players change (they vanish, the fix). Covered by §4.2a/b/c. |
| Overlay duplicates a clip already in `core` (player still open on the bound clip) | Dedup by full path; promote in place. Tested via the previous-stable regression. |
| Overlay resolves the wrong basename | Reuses the proven `LibraryTitleMatcher` (longest-boundary) already trusted by the resolver, no new matching logic. |
| Conditional wake leaves an unlocked last-clip tag unflushed until next session | Accepted (§4.3): durable queue = no loss; unlocked clips are directly writable; zero-touch targets the *locked* case which is preserved. |
| Reading `binding.Chosen` as possibly-closed breaks an assumption elsewhere | `ResolveBindingResolver` already returns closed segments as `Chosen`; only `ResolveReview`'s consumption changes. Confirm no other consumer. |
| §4.5 turns out to be a real window bug after all | Explicitly deferred to verification in tomorrow's run; not changed blind. |

---

## 8. Definition of Done

1. `dotnet build`, 0 warnings, 0 errors, all projects.
2. `dotnet test`, all pass, incl. the new §4.1/§4.2/§4.4 cases + the invariant + real-clip
   integration + media-integrity.
3. Zero NuGet packages added to production projects.
4. v1.5.0 → v1.6.0 in csproj **and** manifest (pack gate enforces equality); `.mcpb` repacked.
5. New gotchas in `docs/PITFALLS.md` (time-base split; conditional pump wake).
6. `anyLiveTarget:true`+`candidates:[]` is impossible by construction (invariant test green);
   a closed player never raises `player_outside_library`; `written` is authoritative for the
   unlocked queue→flush path.

---

## 9. Open questions for the owner (morning review)

1. **Scope of §4.4:** included as conditional-wake (recommended). Alternative was "report the union
   more loudly and leave behavior alone." Comfortable with the behavioral change?
2. **§4.5:** deferred to verification during tomorrow's run (check whether the freshened clips were
   already in the index baseline). Agree, or want a code change now?
3. **Segment-recency guard (optional hardening):** even with this fix, the *binding* can still choose
   a long-closed in-library segment as "what you watched." Harmless (it resolves to a real clip you
   can tag), but possibly surprising. Add a recency bound on bindable closed segments, or leave it?

## 10. Versioning interaction (do not skip)

Per `project_v1_release_versioning`: after watched-clip work is declared **done** and the owner
clears it, the plan is to reset 1.5.0 → **1.0.0** for the first public release (gated on landing-page
launch). This spec defaults to **v1.6.0** to keep the 1.x dogfood line moving and because the reset
is explicitly the *last atomic step* requiring owner sign-off (and a Desktop uninstall-first
reinstall). **If tomorrow's run is clean and the owner calls watched-clip work done, fold this into
the 1.0.0 reset instead of shipping a throwaway 1.6.0.** Owner decides at review time.
