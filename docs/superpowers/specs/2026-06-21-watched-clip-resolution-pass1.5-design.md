# Watched-Clip Resolution — Pass 1.5 (Wrong-Directory Honesty) — Design Spec
**Date:** 2026-06-21
**Status:** Approved for planning (brainstorm complete)
**Builds on:** `2026-06-21-watched-clip-resolution-design.md` (pass 1, merged PR #27)
**Author:** Peckworks Lab

---

## Problem Statement

Pass 1 resolves which library clip an open player is showing — but it is *silently optimistic* in two ways that can cause the worst outcome of the whole feature: **tagging the wrong clip.**

1. **VLC same-name collision (the disaster case).** VLC reports only the bare filename. If your library has `clip001.mp4` and you are actually watching a *different* `clip001.mp4` from another folder, pass 1 matches the library copy, calls it a single unambiguous hit → **`high` confidence → the agent may tag it without confirming.** A confident wrong tag.

2. **Silent wrong-directory guessing.** When a player is open on a file that isn't in the configured library, pass 1 discards the unresolved title (`PlayerTitleSignal.cs:26`) and falls through to the access-time fallback — handing back some recently-touched library clip with **no signal** that the thing you're actually watching isn't in this folder. The access-time guess becomes a plausible wrong answer presented at peak confusion.

The user's intent (verbatim): **always assume the played file lives in the configured clip directory; make a good-faith attempt to find it there; never go searching other directories; but if it can't be found, alert the user that they may be playing from the wrong folder.** Pass 1 does the first three; it does not do the alert, and it over-trusts bare-name matches.

This spec closes both gaps. It is an **honesty fix**, not new surface area — the resolver stops silently guessing when it has positive evidence it shouldn't.

---

## Scope

**In scope:**
- **Collision guard:** bare-name (VLC-style) player matches earn `high` only when the library file is confirmed locked; otherwise demote to `low` with a confirm note. Full-path (MPC-style) matches are exact and unaffected.
- **Cloud-safe lock probe:** detect offline/placeholder files via attributes (no file open) so probing `inUse` can never force a Dropbox/OneDrive download.
- **Wrong-directory warning:** capture player titles that name an `.mp4` not in the library; surface a warning (player name, and the foreign folder for full-path titles). **No directory searching** — the foreign path comes only from the title string itself.
- **Fallback suppression:** when player(s) are open on unresolved foreign files and *nothing* resolved to the library, suppress access-time candidates and lead with the warning.
- Both surfaces (MCP `library_watching`, CLI `--watching`) carry the warning and the per-candidate confirm note.

**Out of scope (unchanged from pass 1 / deferred):**
- Pass-2 deferred-tag queue and the session-payload workflow.
- Any directory searching, MRU/recents reading, player web-interface polling, or new signals (those are the pass-1 §11 roadmap).
- Configurability/learning of the lock-trust policy (revisit after real dogfooding).

---

## 1. Behavior — the canonical state table

Pass-1.5 logic engages **only when a player is open**. No player open ⇒ identical to pass 1.

| # | Player situation | Result |
|---|---|---|
| 1 | No player open | Access-time fallback, exactly as pass 1 |
| 2 | Player open, title has **no** `.mp4` (metadata-title display / idle) | Quiet — normal fallback, no warning, not treated as a wrong-directory signal |
| 3 | **Full-path** (MPC) match in library | `high` (exact path — lock state irrelevant) |
| 4 | **Bare-name** (VLC) match, file **locked** (`inUse=true`) | `high` |
| 5 | **Bare-name** match, **not locked** (`inUse=false`) or **offline** | `low` + note: *"not currently locked — may be a same-named file elsewhere; confirm before tagging"* |
| 6 | Multiple players each resolve to a library clip | all `low` (pass-1 multi-player ambiguity), each still under the row-4/5 bare-name lock rule |
| 7 | Player(s) named an `.mp4` **not in library**, and **no** player resolved | **Warning** (player name + foreign folder for full-path titles) + **access-time candidates suppressed** |
| 8 | Mixed: ≥1 player resolves **and** ≥1 names a foreign file | The resolved candidate(s) stand under their normal rules; the foreign player(s) are an **informational note**, not a suppressor |

Notes:
- "Locked" = the `inUse` lock probe (pass-1 `ProbeInUse`) returns true. "Offline" = the library file carries the offline/placeholder attribute and is therefore not probed.
- Row 2 is distinct from row 7: a title with *no* `.mp4` is **not** evidence of a foreign file (it could be a library clip whose title bar shows metadata), so it never warns and never suppresses.
- Row 5 always **demotes, never drops** — a correct-but-paused clip stays in the results, it just needs confirmation.

---

## 2. Collision guard (rows 3–5)

The bare-name vs full-path distinction is the crux: a full-path match *is* the library file (no possible collision); a bare-name match could be a coincidental same-name file played from elsewhere.

- **Match kind is tracked per hit.** `SignalHit` gains `MatchKind` (`TitleExtractionKind?` — `FullPath` / `BareName`, null for non-player signals). `PlayerTitleSignal` sets it from the extraction.
- **Confidence rule (replaces pass-1's "unambiguous ⇒ high"):**
  - Full-path, unambiguous player hit → `high`.
  - Bare-name, unambiguous player hit, library file **locked** → `high`.
  - Bare-name, unambiguous player hit, **not locked or offline** → `low` + confirm note.
  - Anything ambiguous (multi-player, multi-match name) or access-only → `low` (as pass 1).
- **Cloud-safe probe.** `ProbeInUse` first checks `FileInfo.Attributes` for `FileAttributes.Offline` (and reparse-point placeholder semantics, consistent with `LibrarySandbox.ResolveRealPath`'s cloud-aware handling). If offline/placeholder, it returns "not confirmed locked" **without opening the file** — no hydration. Only non-offline files are opened with `FileShare.None`.
- **Probe ordering.** Player-hit candidates are probed **before** confidence is finalized (there are at most a handful — one per open player), because their confidence now depends on the lock. Access-time candidates are still probed only **after** the `Take(limit)` cap — the "never lock-probe the whole library" guarantee from pass 1 is preserved.

---

## 3. Wrong-directory warning + fallback suppression (rows 7–8)

- **Capture unresolved players.** A player title that extracts an `.mp4` which resolves to **zero** library clips is an *unresolved player*. (A title that extracts nothing — row 2 — is not unresolved; it is ignored.)
- **`PlayerTitleResolution` helper** is the single source of truth: given a `WatchContext`, it returns one entry per player whose title named an `.mp4`: `{ Window, Kind, ReferencedValue, Matches }`. `PlayerTitleSignal` uses it to emit hits (where `Matches` is non-empty); the resolver uses it to derive unresolved players (`Matches` empty). The helper runs over the tiny set of open player windows — calling it from both places is negligible and removes duplicated parse/resolve logic.
- **Warning content.** For each unresolved player: the process name, the referenced name, and — **only when the title was a full path** — the foreign directory (`Path.GetDirectoryName` of the title's path). Bare-name unresolved players contribute the name but no directory (we genuinely don't know where it is, and will not look).
- **Suppression rule (row 7 vs 8).** If there is **at least one resolved player hit**, candidates are produced normally and unresolved players ride along as diagnostics (row 8). If there are **no resolved player hits** but **≥1 unresolved player**, the access-time candidates are **suppressed** (result `Candidates` is empty) and the warning leads (row 7). This overrides `include_access_fallback` in that specific state — positive evidence of a foreign file outranks a recency guess.

---

## 4. Architecture changes (follows the pass-1 seams; all callers are ours)

New/changed types in `clipmeta.core/Watching/`:

- **`PlayerTitleResolution`** (new, static helper): `IReadOnlyList<PlayerMatch> For(WatchContext context)`; `PlayerMatch` = `{ ProcessWindow Window, TitleExtractionKind Kind, string ReferencedValue, IReadOnlyList<LibraryClip> Matches }` — one per window whose title named an `.mp4`.
- **`SignalHit`** (modified): add `TitleExtractionKind? MatchKind` (null for access-time hits).
- **`UnresolvedPlayer`** (new record): `{ string Player, string ReferencedName, string? ForeignDirectory }`.
- **`WatchDiagnostics`** (new record): `{ IReadOnlyList<UnresolvedPlayer> UnresolvedPlayers }`.
- **`WatchingCandidate`** (modified): add `string? Note` (the demote reason; null normally).
- **`WatchingResult`** (new record): `{ IReadOnlyList<WatchingCandidate> Candidates, WatchDiagnostics Diagnostics }`.
- **`WatchingResolver.Resolve`** (modified): returns `WatchingResult` (was `IReadOnlyList<WatchingCandidate>`). New flow:
  1. Build context; run signals → hits (player hits carry `MatchKind`).
  2. Group by clip path → provisional candidates with provisional confidence (bare-name unambiguous = *provisional* high pending lock).
  3. Probe `inUse` for **player-hit** candidates (few; offline-safe). Finalize bare-name confidence: provisional-high + not-locked ⇒ `low` + note.
  4. Compute unresolved players via `PlayerTitleResolution.For`. If no resolved player hits and ≥1 unresolved ⇒ drop access-only candidates (suppression).
  5. Rank, `Take(limit)`, probe access-time candidates in the taken set (tiebreaker/`inUse` display), final re-rank — as pass 1.
  6. Return `{ Candidates, Diagnostics(UnresolvedPlayers) }`.
- **`PlayerTitleSignal`** (modified): resolve via `PlayerTitleResolution.For`; emit hits with `MatchKind`; ambiguity logic (multi-player / multi-match) unchanged.

`ProbeInUse` gains the offline-attribute short-circuit. `AccessTimeSignal` is unchanged.

---

## 5. Surfaces

### MCP `library_watching` (`ReadTools.cs`)
- Result gains a top-level **`warning`** object when `Diagnostics.UnresolvedPlayers` is non-empty:
  ```
  "warning": {
    "type": "player_outside_library",
    "message": "<human-readable>",
    "unresolvedPlayers": [ { "player": "...", "referencedName": "...", "foreignDirectory": "..."|null } ]
  }
  ```
- Each candidate gains an optional **`note`** (the demote reason) when present.
- **Description update (to the agent):** "If `warning` is present, a media player is showing a file that is not in the configured clips library — tell the user they may be playing from the wrong folder (name the player and, if given, the folder) and do **not** tag. If a candidate has a `note`, mention it and confirm before tagging."

### CLI `clipmetascribe <dir> --watching` (`WatchingCommand.cs`)
- Prints the warning block prominently **above** any candidates (e.g. `⚠ A player (vlc) is playing "X.mp4", which isn't in this library — you may be in the wrong folder.`).
- Prints the confirm `note` on demoted candidate rows.
- When candidates are suppressed (row 7), prints the warning and a line that no in-library candidate was found.

---

## 6. Testing (all deterministic; fake `IProcessWindowSource` + temp empty `.mp4`s)

- **Collision guard:** full-path match → `high` regardless of lock; bare-name match locked → `high`; bare-name not-locked → `low` + note; bare-name offline → `low` + note and the probe did **not** open the file.
- **Warning:** a player naming an `.mp4` absent from the library produces an `UnresolvedPlayer` (full-path → `ForeignDirectory` populated; bare-name → null).
- **Suppression (row 7):** unresolved player + no resolution ⇒ `Candidates` empty, diagnostics present.
- **Mixed (row 8):** one bare-name match (locked → high) + one unresolved foreign player ⇒ candidate stands AND diagnostics present (not suppressed).
- **Row 2 quiet:** player open, title has no `.mp4` ⇒ no warning, no suppression, normal fallback.
- **Multi-player (row 6):** two resolvable players ⇒ all `low`, bare-name lock rule still applied.
- **Surfaces:** MCP result shows `warning` + candidate `note` (shape asserted via `structuredContent`); CLI prints warning above candidates and the note on demoted rows.
- **Regression:** the no-player path and the pass-1 confidence/fallback behavior are unchanged; `ToolsList` surface unchanged (no new tool).

Core logic tested through `clipmetascribe.Tests`; MCP shape through `clipmetamcp.Tests`; both build/pass clip-less on CI (no real player launched).

---

## 7. Risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | Pause/stop releases the lock ⇒ bare-name correct hit demoted to confirm (nag) | Accepted, scoped to bare-name only (MPC unaffected); demote-not-drop keeps the clip available; revisit trust policy after dogfooding (recorded in PITFALLS) |
| 2 | Lock probe hydrates a cloud-placeholder file (download) | Offline-attribute short-circuit: never open an offline file; bare-name offline ⇒ `low` (correct lean — an un-downloaded file isn't the one playing) |
| 3 | Warning fires spuriously on an idle/metadata-title player | Row 2: a title with no `.mp4` is never unresolved; only a named-but-absent `.mp4` warns |
| 4 | Suppression hides a genuinely-useful recency guess | Only when *no* player resolved; the access-time data is still queryable on explicit request; asymmetry favors avoiding a confident wrong tag over minor friction |
| 5 | Return-type change to `Resolve` breaks callers | All callers are in this repo (MCP handler, CLI, tests) and updated in the same change |
| 6 | Offline detection differs across cloud providers | Use `FileAttributes.Offline` + reparse-point placeholder handling consistent with the existing `LibrarySandbox` cloud-aware code; best-effort, never fatal |

---

## 8. Definition of Done

1. `dotnet build` — 0 warnings, 0 errors (incl. CA1416).
2. `dotnet test` — all pass, including the new state-table cases and the unchanged pass-1 regression cases, on Windows and clip-less CI.
3. The state table (§1) is realized exactly: full-path always `high`; bare-name `high` only when locked; warning + suppression only on named-but-absent with no resolution; row 2 quiet; mixed keeps the resolution.
4. The lock probe never opens an offline/placeholder file.
5. No directory searching anywhere; foreign-folder info comes only from the player title.
6. Zero NuGet added; public types documented; the pause/lock-release trust-policy caveat recorded in `docs/PITFALLS.md`.
