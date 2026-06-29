# Watched-Clip Detection Robustness, Design Spec
**Date:** 2026-06-25
**Round:** Watched-clip resolution, pass 3 (detection hardening)
**Author:** Peckworks Lab
**Predecessors:** pass-1 (#27), pass-1.5 (#28), pass-2 deferred-tag queue (#30)

---

## Problem Statement

A live dogfooding session (2026-06-25, tagging a 38-clip library through the ClipMeta MCP
server in Claude Desktop, across MPC-HC and VLC) validated the watch-and-tag loop end-to-end, 
queue-while-locked, auto-flush on lock release, multi-field merge, explicit flush, but exposed
a cluster of **detection** weaknesses that all share one consequence: the tool too often resolves
a clip at **low confidence**, so every tag leans on the *user* having confirmed the target rather
than the *server* asserting it. The specific failures:

1. **MPC-HC title detection is intermittent.** Across the session the player-title path fired
   `✓✗✗✓✓` for MPC-HC while VLC was clean every single call. Same library, same files locked the
   whole time. When detection failed, the clip still resolved, but via the access-time + lock
   fallback at `confidence: low`, so the high-confidence assertion the feature exists to provide
   simply did not happen for MPC-HC.

2. **No "nothing is live" signal.** When the user narrated after closing the player, the server
   still returned the just-flushed clip as a low-confidence candidate at `secondsSinceAccess: 0`
   (its access time freshened by *our own flush write*, not by playback). Nothing in the result
   said "no clip is actually open." Refusing to tag was a *model* judgment call, not a server
   guarantee, a less cautious caller would have overwritten the wrong clip.

3. **A failed title parse nulls the player identity.** When `player_title` does not fire, the
   candidate's `Player` is `null`, so the tool cannot even report *which* player it failed on, 
   a diagnostic blind spot exactly when it is most wanted.

4. **Access-time fallback is noisy and unreliable.** Stale low-confidence candidates are listed
   beneath a clear high-confidence winner (cosmetic noise), and the access-time signal itself is
   polluted by ClipMeta's own reads and flush-writes, and is frequently disabled at the OS level
   on Windows (`NtfsDisableLastAccessUpdate`).

The root cause of #1 is now understood at the code level (see §2). This round makes player-title
resolution **library-aware and title-format-independent**, gives callers an explicit live-target
signal, and stops the access-time fallback from masquerading as actionable.

---

## Scope of This Round

**In scope (all in `ClipMetaCore.Watching`, surfaced through CLI `--watching` and MCP
`library_watching`):**

- **#2** Replace brittle extract-then-exact-match title resolution with **match-the-title against
  known library filenames** (boundary-checked, longest-match containment). Fixes MPC-HC.
- **#3** Add an explicit `AnyLiveTarget` signal to `WatchingResult`; plumb it through
  `library_watching` output and update the tool description so callers refuse auto-tag when no
  clip is live.
- **#6** Best-effort **player attribution from the open-window snapshot** so a locked, live clip
  reports its player even when its title could not be parsed.
- **#8** Suppress stale access-time candidates beneath a high-confidence winner; formally
  demote access-time to advisory-only in ranking and documentation.

**Out of scope this round:**

- Write semantics, per-field append, provenance stamping, the zero-touch background flush watcher
, these are the **companion spec** (`2026-06-25-queue-write-semantics-and-provenance.md`).
- Client-side concerns that live in Claude Desktop, not this repo: the input-box pacing block, and
  capturing the open file at *narration* time. Better detection here narrows the drift window but
  cannot close it; the true fix is client-side and is recorded only as context.
- Restart Manager (`rstrtmgr.dll`) lock-owner enumeration. The §5 window-snapshot heuristic covers
  the common single-player case without P/Invoke; full lock-owner attribution is deferred.
- Performance instrumentation (Stopwatch timing), separate quick-win PR.

---

## 1. Current Architecture (what we are changing)

One resolution pass (`WatchingResolver.Resolve`) runs registered `IWatchSignal`s over a
once-enumerated library (`WatchContext`), groups hits per clip, and scores confidence:

```
WatchContext.Build(root)                     enumerate library once; snapshot player windows
   │  ByFileName: name → clip(s)   ByFullPath: path → clip   PlayerWindows: (proc, title)[]
   ▼
PlayerTitleResolution.For(context)           the single source of truth for player→clip
   │  for each player window:
   │     PlayerTitleParser.Extract(title)  → FullPath | BareName | null   (PURE TEXT)
   │     resolve extracted value via ByFullPath / ByFileName (EXACT key lookup)
   ▼
PlayerTitleSignal.Detect / AccessTimeSignal.Detect   →   SignalHit[]
   ▼
WatchingResolver: group hits, score confidence, lock-probe, rank, cap   →   WatchingResult
```

The brittle step is **`PlayerTitleParser.Extract` + exact key lookup**. It is the only place
title text is turned into a clip reference, and it assumes the title contains a *clean* `.mp4`
token that equals a library key.

---

## 2. #2, Library-Aware Title Matching (the MPC-HC fix)

### Root cause (confirmed in code)

`PlayerTitleParser.BareNameRegex` is `([^\\/:*?""<>|]+?\.mp4)`, it excludes `:` (a path/illegal
char) but **not** the ` - ` separator MPC-HC inserts between a playback-position prefix and the
filename. MPC-HC's title bar varies with playback state and config; when it shows position, e.g.

```
00:01:23 - Sons of the Forest 2025.03.17 - 23.27.30.27.DVR.mp4
```

the regex, scanning left-to-right for the first colon-free run ending in `.mp4`, starts *after*
the last colon (`...01:23`) and captures:

```
23 - Sons of the Forest 2025.03.17 - 23.27.30.27.DVR.mp4
```

That value is not a key in `ByFileName` (whose key is the exact basename), so the exact lookup
**fails** and the player-title path silently goes quiet. When MPC-HC's title happens *not* to show
position, the bare name is clean and resolution succeeds, hence the intermittent `✓✗✗✓✓`. VLC's
title is a stable `name.mp4 - VLC media player`, so it never trips this. **The defect is the
extract-then-exact-match strategy, not the read mechanism.**

### New strategy: containment against the known library

We have already enumerated every library filename (`WatchContext.ByFileName.Keys`). Instead of
extracting a token from arbitrary title text and hoping it equals a key, **ask which known library
filename appears inside the title.** This is immune to every title-format quirk, timecode
prefixes, paused/OSD text, custom formats, because we match against ground truth, not a guess.

Resolution order in `PlayerTitleResolution.For`, per player window:

1. **Full-path match (unchanged, still strongest).** `PlayerTitleParser.Extract` still extracts a
   drive-rooted full path first; if it resolves in `ByFullPath`, use it. Full paths disambiguate
   same-named files in different folders, so they keep priority.

2. **Basename containment (replaces brittle bare-name extraction).** Otherwise, scan the title for
   any known library basename using `LibraryTitleMatcher.FindBestMatch(title, ByFileName.Keys)`:

   - A candidate basename matches only at a **filename boundary**: the character immediately before
     its occurrence is the start of the string, a path separator (`\` `/`), or a non-filename
     character (whitespace, quote, `:` etc.). This prevents `myclip.mp4` in the title from matching
     a library file `clip.mp4`.
   - When several known basenames match, take the **longest** (most specific), this also resolves
     prefix-overlap deterministically.
   - The matched basename resolves through `ByFileName`, which may map to **more than one** clip
     (same name, different folders) → ambiguous, exactly as today.

3. **Unresolved (diagnostics unchanged).** If neither resolves, `PlayerTitleParser.Extract` still
   provides the *referenced value* (and, for a full path, the foreign directory) for the existing
   wrong-directory `WatchDiagnostics`. Extraction is retained **solely** to describe unresolved
   players; it no longer gates resolution.

### Where the logic lives

- `PlayerTitleParser` stays a **pure text** type for full-path extraction and the
  diagnostics-only referenced-value. Its doc comment is updated to say bare-name *resolution* is
  now library-aware and lives in `PlayerTitleResolution`.
- New `LibraryTitleMatcher` (pure, testable): `string? FindBestMatch(string title,
  IEnumerable<string> knownBasenames)`, boundary-checked, longest-match, case-insensitive. No
  library or OS dependency; takes the basename set as a parameter.
- `PlayerTitleResolution.For` orchestrates the 1→2→3 order above, producing the same `PlayerMatch`
  records it does today, so `PlayerTitleSignal` and `WatchingResolver` are unchanged downstream.

### Ambiguity & safety (preserved)

The existing `Ambiguous` semantics carry over verbatim: a hit is ambiguous when more than one
recognized player resolves a title, or when a bare/contained name resolves to more than one library
clip. The bare-name-not-locked collision guard (`NotLockedNote`, demote-to-low) still applies to
containment matches, because they are `TitleExtractionKind.BareName`.

---

## 3. #3, Explicit Live-Target Signal

### Problem

`WatchingResult` exposes ranked candidates but no answer to "is any clip *actually* open right
now?" When nothing is live, the access-time fallback still returns clips whose recency may be an
artifact of ClipMeta's own reads/writes, and the result looks identical to a real hit. The session
showed this is a real foot-gun: a flush write freshened a clip's access time to 0 s, making a
*just-tagged* clip the top "recent" candidate.

### Design

Add one boolean to the result:

```csharp
public sealed record WatchingResult(
    IReadOnlyList<WatchingCandidate> Candidates,
    WatchDiagnostics Diagnostics,
    bool AnyLiveTarget);          // NEW
```

**Definition:** `AnyLiveTarget == true` iff at least one returned candidate is genuinely live, 
i.e. it has a `player_title` hit **or** its lock probe reported `InUse == true`. Access-time-only,
not-in-use candidates do **not** count as live.

- Computed in `WatchingResolver.Resolve` from the final candidate list, after lock probing.
- `library_watching` (MCP) and `--watching` (CLI) surface it: MCP adds `"anyLiveTarget": false`
  to the result object.
- **Tool-description change (`ReadTools` / `library_watching`):** state that when `anyLiveTarget`
  is `false`, the caller must **not** auto-tag or auto-queue, it must confirm the exact path with
  the user first, because every returned candidate is an unverified recency guess. This pushes the
  guard the session relied on (model discretion) into an explicit contract.

We deliberately **do not** suppress the access-time fallback when no player is open. The session
confirmed that "here are the most recently touched clips, but confirm" is useful behavior when
nothing is live (Test 1 was a PASS). `AnyLiveTarget` lets the caller treat those candidates
correctly without removing them.

> Note: the existing `suppressAccessFallback` (Row-7: a player open on a *foreign directory* with
> nothing resolved) is unchanged, that case still hard-suppresses, because a wrong-folder warning
> should lead with no tempting answer.

---

## 4. #4 placeholder, n/a

(Intentionally omitted; item numbering follows the triage. #1/#4/#5/#7 are in the companion specs.)

---

## 5. #6, Player Attribution When the Title Fails

### Problem

A candidate that is `InUse == true` but resolved only via access-time carries `Player == null`,
because `Player` is sourced only from a `player_title` hit (`WatchingResolver.cs:95`). The tool
then cannot report which player holds a live, locked clip.

### Design (best-effort, no P/Invoke)

After lock probing, for any returned candidate that is `InUse == true` **and** has `Player == null`,
attribute a player from the already-captured `WatchContext.PlayerWindows` snapshot:

- If **exactly one** recognized player window is open whose title did **not** resolve to a library
  clip, set `Player` to that window's `ProcessName` and annotate `Note` (e.g.
  `"player title not recognized, attributed by open-player heuristic"`).
- If zero, or more than one such window is open (genuinely ambiguous), leave `Player == null`, 
  never guess between two players.

This is cheap (operates on the existing snapshot), introduces no new OS calls, and directly fixes
the common single-player blind spot. Note that once #2 lands, MPC-HC titles resolve, so this is a
**backstop** for any future/unknown player or custom title format, its value is correctness of the
diagnostic, not the hot path.

> Deferred (recorded): authoritative lock-owner enumeration via Windows Restart Manager
> (`RmStartSession` / `RmGetList`, P/Invoke to `rstrtmgr.dll`, no NuGet). Correct even with several
> players open and even when no window matches, but materially more code; revisit only if the
> heuristic proves insufficient in real use.

---

## 6. #8, Quiet the Access-Time Fallback

### 6a. Suppress stale candidates beneath a high winner

When the final list contains **any** `confidence == "high"` candidate (a single unambiguous
player-title hit), drop the `access_time`-sourced candidates from the returned list entirely. They
are pure noise once a clip is positively identified, and the session flagged them as such. Keep all
`player_title` candidates (including ambiguous/low ones, a genuine two-player situation must still
surface every contender). If there is **no** high winner, behavior is unchanged: access-time
candidates remain, capped by `limit`.

Implemented as a final filter in `WatchingResolver.Resolve` before returning.

### 6b. Access-time is advisory-only

The session confirmed two independent reasons the access-time signal cannot be trusted as more than
a hint:

- **Self-pollution.** ClipMeta's own reads (`library_list`, `clip_get_metadata`, `library_vocab`)
  and **flush writes** bump last-access time, collapsing the recency ordering and freshening
  already-handled clips to `secondsSinceAccess ≈ 0`.
- **OS disablement.** Windows commonly ships with last-access updates disabled, so the signal may
  be uniformly stale.

**Decision: do not fight this.** We will *not* attempt to snapshot-and-restore access times around
our own IO (fragile, racy, and useless when the OS disables atime). Instead:

- Lock state (`InUse`) already outranks access time in the final sort
  (`Confidence` → `InUse` → `LastAccessTimeUtc`, `WatchingResolver.cs:142-144`), keep it that way;
  a held handle is a far stronger live signal than recency.
- Document in the `library_watching` tool description and `AccessTimeSignal` XML docs that
  access-time candidates are advisory recency hints requiring confirmation, never auto-tag targets
  (reinforced by `AnyLiveTarget`, §3).
- Append a PITFALLS entry recording the self-pollution + OS-disablement facts so this is not
  rediscovered.

---

## 7. Affected Types (change summary)

| Type | Change |
|---|---|
| `LibraryTitleMatcher` (new, pure) | `FindBestMatch(title, knownBasenames)`, boundary-checked longest containment match. |
| `PlayerTitleResolution` | Order: full-path → `LibraryTitleMatcher` containment → unresolved. Library-aware bare matching. |
| `PlayerTitleParser` | Unchanged behavior; doc clarifies bare-name *resolution* moved to `PlayerTitleResolution`; extraction now feeds full-path resolution + diagnostics only. |
| `WatchingResult` | + `bool AnyLiveTarget`. |
| `WatchingResolver` | Compute `AnyLiveTarget`; §5 player attribution; §6a stale-suppression filter. |
| `WatchingCandidate` | No shape change (reuses existing `Player` / `Note`). |
| `clipmetamcp` `ReadTools` (`library_watching`) | Emit `anyLiveTarget`; description: refuse auto-tag when false; access-time is advisory. |
| `clipmetascribe` `--watching` rendering | Surface live-target state / attributed player. |

No change to `IWatchSignal`, `WatchContext`, `AccessTimeSignal`'s emission, `PlayerTitleSignal`,
the queue, or any write path. SOLID preserved: the new matcher is additive; signals are untouched.

---

## 8. Test Strategy

All watching tests live in `clipmetascribe.Tests` (`PlayerTitleParserTests`,
`PlayerTitleResolutionTests`, `WatchingResolverTests`). Drive resolution with the existing fake
`IProcessWindowSource` so no real player is needed. **Per the CLAUDE.md surface-test rule, run the
full `clipmetascribe.Tests` and `clipmetamcp.Tests` projects** (not a `--filter`) after any change
to the resolver or the `library_watching` tool surface.

**`LibraryTitleMatcherTests` (new, the MPC-HC regression net)**
- MPC-HC with timecode prefix: `"00:01:23 - Sons of the Forest 2025.03.17 - 23.27.30.27.DVR.mp4"`
  → resolves to that exact library clip. *(This is the bug; it must go red without the fix.)*
- MPC-HC paused / OSD variants of the same title → resolve.
- VLC `"name.mp4 - VLC media player"` → resolve (no regression).
- Boundary safety: library has `clip.mp4`; title names `myclip.mp4` → does **not** match `clip.mp4`.
- Longest-match: library has both `clip.mp4` and `my clip.mp4`; title names `my clip.mp4` →
  resolves to `my clip.mp4`, not `clip.mp4`.
- No `.mp4` / unrelated title → no match (null).
- Same basename in two folders → resolves ambiguous (two matches).

**`PlayerTitleResolutionTests` (extend)**
- Full-path title still wins and disambiguates folders (unchanged).
- Title that previously failed (timecode prefix) now produces a resolved `PlayerMatch`.
- Unresolved foreign-directory title still yields the correct `UnresolvedPlayer` diagnostics.

**`WatchingResolverTests` (extend)**
- `AnyLiveTarget == true` when a player-title hit exists; `true` when a candidate is `InUse`;
  `false` when only not-in-use access-time candidates remain.
- §6a: with a high-confidence winner present, access-time candidates are dropped from the result;
  with no winner, they remain.
- §5: a locked candidate with an unparseable single open player window is attributed that player;
  with two such windows, `Player` stays null.
- Regression: queue/flush behavior and the Row-7 foreign-directory suppression are unchanged.

**`clipmetamcp.Tests` (extend)**
- `library_watching` output includes `anyLiveTarget`.
- `ToolsList_ContainsTheFullToolSurface` still passes (no tool added/removed, guards against the
  registration-order trap noted in CLAUDE.md).

---

## 9. Risk Table

| # | Risk | Mitigation |
|---|---|---|
| 1 | Containment false-positive (one filename a substring of another) | Filename-boundary check + longest-match; explicit boundary/prefix tests. |
| 2 | Performance: scanning every known basename per title at large libraries | Match is O(players × library) substring scans, players ≈ 1–2; acceptable at hundreds–thousands. If it ever bites, index basenames, recorded, not built. |
| 3 | `AnyLiveTarget` consumers not updated → guard not enforced | Tool description change shipped same PR; MCP test asserts the field is present. |
| 4 | Player heuristic mis-attributes when 2 players open | Attribute only when exactly one unresolved player window exists; else null. |
| 5 | Dropping access-time candidates hides a real target | Only dropped when a high-confidence player hit exists (positive identification); never when uncertain. |
| 6 | Regex/extraction still used for diagnostics drifts from resolution | Single source of truth (`PlayerTitleResolution`) owns both; extraction feeds only diagnostics + full path. |

---

## 10. Definition of Done

1. `dotnet build`, 0 warnings, 0 errors, all projects.
2. `dotnet test`, full `clipmetascribe.Tests` and `clipmetamcp.Tests` pass (not filtered),
   including the new `LibraryTitleMatcherTests` MPC-HC regression and the `AnyLiveTarget` /
   stale-suppression / attribution tests.
3. The MPC-HC timecode-prefix title resolves to a `player_title` / high-confidence candidate
   (the headline session bug is closed).
4. `library_watching` returns `anyLiveTarget`, and its description instructs callers to refuse
   auto-tag when it is `false`.
5. A live, locked clip whose title cannot be parsed reports its player via the single-open-player
   heuristic (or null when genuinely ambiguous).
6. With a high-confidence winner, stale access-time candidates no longer appear in the result.
7. Zero NuGet packages added to production projects.
8. New public types/methods documented; PITFALLS updated with the title-extraction defect and the
   access-time self-pollution / OS-disablement facts.

---

## 11. Companion / Follow-on Work (recorded for continuity)

- **`2026-06-25-queue-write-semantics-and-provenance.md`**, per-field append (notes/tags/players
  vs set), `tagged_by: Peckworks ClipMeta` provenance stamp (opt-out), and the zero-touch
  background flush watcher. Sequenced **after** this spec so write semantics operate on
  better-identified targets.
- **Perf instrumentation**, Stopwatch per MCP handler + structured timing log (standalone PR).
- **Restart Manager lock-owner enumeration**, upgrade path for §5 if the heuristic proves weak.
- **Open data issue (not code):** clip `Sons of the Forest 2025.03.18 - 00.15.57.28` round and the
  `18.25.20.35` drift have unreliable `notes`; re-verify in the next test round.
