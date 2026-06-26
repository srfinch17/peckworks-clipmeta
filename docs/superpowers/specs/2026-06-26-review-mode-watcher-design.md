# Review-Mode Watcher — Design Spec

**Date:** 2026-06-26
**Round:** Watch-and-tag, pass 4 (binding correctness — review mode)
**Author:** Peckworks Lab
**Predecessors:** pass-1/1.5 (#27/#28), pass-2 deferred queue (#30), pass-3 detection + write semantics (#32/#33).
**Source evidence:** `clipmeta_testrun_log.md` (two live dogfood runs, 2026-06-26) and the owner's
derived `clipmeta_watch_and_tag_SPEC.md`. This spec implements **only** the review-mode slice of
that larger document (see §9 for what is deliberately deferred).

---

## Problem Statement

The watch-and-tag loop has a **silent data-integrity bug**: a spoken tag can bind to the *wrong*
clip. `library_watching` resolves "which clip" by inspecting open player windows **at the moment the
tool executes** — a full assistant turn *after* the user dictated. The user's natural rhythm is
*watch clip N → dictate → advance to N+1*; if the advance lands before the (late) poll, the poll
reads N+1 and the tag binds there.

Two live runs isolated the root cause:

- **Run 1 (MPC-HC, 5 dictations):** binding drifted by one from clip 2 on; one clip skipped, another
  double-tagged. Entangled with a separate, intermittent MPC title dropout.
- **Run 2 (VLC, 5 clips, clean playlist order):** clips 1–3 bound correctly; **clip 4 bound to clip 5,
  skipping clip 4.** VLC's title detection was flawless on *every* poll — yet drift still occurred.

Run 2 is decisive: drift is **not** a title-detection problem and **not** player-specific. It is a
race between the user's advance and the assistant's poll, and it is **silent and intermittent**
(1 of 5, on the shortest clip). Every wrong binding showed the polled clip at
`secondsSinceAccess ≈ 0–0.1` — the clip had *just started*. A clip the user actually watched and is
describing would have been playing for several seconds. **That distinction is the lever.**

The current architecture cannot exploit it: `WatchContext.Build` snapshots player windows *once per
call* (`WatchContext.cs:60`) and `WatchingResolver.Resolve` scores that single "now" snapshot. There
is no history, so the resolver cannot know a clip *just* started versus has been playing a while.

> **Reconciliation note (symptom-log triage).** Several other testrun findings were reconciled
> against `main` and are **not** code bugs in the current build: provenance *is* stamped on the queue
> drain (default `StampProvenance=true` flows through `QueuedMutation.ToMutation` → `Mp4Writer`), but
> `tagged_by` is `IsInternal` and hidden from `clip_get_metadata` by design — the log "verified" via a
> surface that hides it. `anyLiveTarget` is emitted unconditionally on `main` (`ReadTools.cs:562`).
> Those are measurement artifacts. Genuine residual items (MPC title-retry, dry-run preview, notes
> separator, a queue-drain→provenance test, lock backoff) are real but **out of scope here** — they
> are the secondary-fixes batch (§9). This spec targets the one architectural bug: the binding race.

---

## Scope of This Round

**In scope** — review mode (manual walk through clips in a media player) only:

- A continuous, **read-only** `ReviewWatcher` background thread (Core) that records cheap **title
  segments** (raw player title + start/end), never resolving clips in its hot loop.
- A pure `ReviewBindingResolver` implementing the **"ignore-just-started → previous-stable"** rule.
- A Core `ResolveReview` entry point that reuses the **entire existing** `WatchingResolver` pipeline
  over the heuristic-chosen title, promotes the corrected bind, and attaches review flags.
- Inline `review[]` flags (`autoCorrected` / `sameClipTwice` / `sequenceSkip`) on `library_watching`,
  derived purely from segment Ids + one `MarkBound` call. **No new MCP tool.**
- `QueueDrainPump`-style lifecycle wiring in the MCP shell.

**Out of scope** (each its own future spec — §9): timestamp ingestion / fire-N-ahead (AC2), gaming
mode / `FileSystemWatcher` (AC7–AC8), the §7 secondary-fix batch, a persisted review file or
dedicated review tool, and field-normalization depth.

**Acceptance criteria covered this round:** AC1 (race immunity, single off-by-one), AC3
(same-clip-twice flag + accumulation), AC4 (skip detection), AC5 (cold start), AC6 (locked-file
deferral — unchanged, already works). **AC2 (fire-N-ahead) is explicitly deferred** — it requires a
per-dictation timestamp key the heuristic cannot synthesize (see §9).

---

## 1. Architecture

Three units, each with one job, all decoupled through existing seams:

```
ClipMetaCore.Watching (Core — testable, no MCP/OS coupling)
 ┌─────────────────────────────────────────────────────────────────────┐
 │  ReviewWatcher  (background thread; IDisposable)                      │
 │   • polls IProcessWindowSource every ~250ms                          │
 │   • records TITLE SEGMENTS (raw title + start/end), never resolves   │
 │   • Snapshot() → thread-safe copy; MarkBound(id); lastBoundId        │
 │                                                                       │
 │  ReviewBindingResolver  (pure, static)                               │
 │   • input: segment snapshot + now + threshold                        │
 │   • output: ReviewBinding { Chosen, CorrectedFrom, StableSeconds,    │
 │             AmbiguousMultiPlayer, Flags[] }                          │
 └─────────────────────────────────────────────────────────────────────┘
                               │ chosen title
                               ▼
   WatchingResolver.ResolveReview  ── reuses Resolve: PlayerTitleResolution,
   (new Core entry, wraps Resolve)    LibraryTitleMatcher, access-time fallback,
                                      wrong-dir diagnostics, lock probe, §6 attrib,
                                      anyLiveTarget — all preserved.
                               ▲
 clipmetamcp (thin shell)      │
   • Program.Serve(): construct ReviewWatcher after sandbox (library configured),
     Start() before session.Run(), Dispose() after Run() returns.
   • ReadTools.Watching: trailing-injectable watcher; reads Snapshot(), calls
     ResolveReview, serializes enriched result with review[].
```

**Why these boundaries.** The watcher records *facts* (titles over time) and knows nothing about
clips or MP4s. The heuristic is a *pure function* over those facts (trivially testable with a fake
clock). The heavy library/title resolution stays in the already-tested `WatchingResolver`. This
mirrors the `QueueDrainPump` precedent: a Core background driver, OS/MCP coupling injected, the shell
wires only lifecycle. SOLID preserved — every change is additive; no existing signal, the queue, or
any write path is touched.

**Key reuse seam.** `WatchContext.Build` gains an overload that accepts a supplied
`IReadOnlyList<ProcessWindow>` instead of calling `source.GetPlayerWindows()`, so the resolver runs
over the heuristic-chosen title with zero downstream change.

---

## 2. ReviewWatcher (Core) — data model & loop

```csharp
public sealed record TitleSegment(
    long Id,                       // monotonic, assigned on open — enables cross-call bind tracking
    string ProcessName,
    string RawTitle,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);      // null = still the current title for that process

public sealed class ReviewWatcher : IDisposable
{
    public ReviewWatcher(
        IProcessWindowSource windowSource,
        Func<DateTimeOffset> clock,                       // injectable for tests
        TimeSpan pollInterval,                            // default ~250ms
        IReadOnlyCollection<string>? playerNames = null,  // default MediaPlayers.KnownProcessNames
        int maxSegments = 64);                            // bounded ring buffer

    public void Start();                                  // launch background loop
    public IReadOnlyList<TitleSegment> Snapshot();        // locked copy
    public long LastBoundId { get; }
    public void MarkBound(long segmentId);                // record the segment library_watching recommended
    public void Dispose();                                // signal stop, join thread
}
```

**Per-tick loop** (wrapped in try/catch → log & continue, never throws):

1. `windows = windowSource.GetPlayerWindows(playerNames)`.
2. Track current title **per player process**. For each open player whose title differs from its last
   recorded title: close that process's open segment (`EndedAt = clock()`), open a new one with the
   next `Id`. A player that vanished closes its open segment.
3. Ring buffer keeps the last `maxSegments`; oldest dropped.

**Cost.** Title polling only — no library enumeration, no MP4 parsing, no clip resolution in the loop.
A no-player tick is a near-empty list. This is the same `GetPlayerWindows` call `library_watching`
already makes once per call, now on a timer; the per-call cost is unchanged in kind.

**Threading.** One `lock` around the segment list and `lastBoundId`; `Snapshot()` returns a copy so the
resolver never reads a mutating buffer. `MarkBound` is a cheap locked write.

---

## 3. ReviewBindingResolver (pure) — the previous-stable rule

```csharp
public sealed record ReviewBinding(
    TitleSegment? Chosen,            // title to resolve to a clip (null ⇒ no correction / defer)
    TitleSegment? CorrectedFrom,     // set when previous-stable was picked over a just-started current
    double StableSeconds,
    bool AmbiguousMultiPlayer,       // 2+ players active ⇒ refuse correction, warn
    IReadOnlyList<ReviewFlag> Flags);
```

`STABLE_THRESHOLD` (default ~2 s) is a single named constant.

**Algorithm:**

1. `current` = newest segment overall (max `StartedAt`). If none → `Chosen = null` (cold/no player).
2. If a **second player** also has activity within `STABLE_THRESHOLD` of `current.StartedAt` →
   `AmbiguousMultiPlayer = true`, `Chosen = null`. No correction; the caller warns.
3. `currentDuration = now − current.StartedAt`.
4. If `currentDuration < STABLE_THRESHOLD` **and** the immediately prior segment has
   `duration ≥ STABLE_THRESHOLD` → `Chosen = prior`, `CorrectedFrom = current` (the off-by-one fix).
   Else `Chosen = current` (no correction).

**This pure function is the entire fix for the reproduced bug.** It is unit-tested by feeding
synthetic segments over a fake clock — including the exact Run-2 dict4 replay (`_5` open 0.1 s, `_4`
stable before → binds `_4`).

**Flags** (derived from segment Ids + `lastBoundId`):

| Flag | Condition | Surfaced meaning |
|---|---|---|
| `autoCorrected` | `Chosen != current` | "Bound *‹prev›*; you'd advanced to *‹current›* (open Ns)." |
| `sameClipTwice` | `Chosen.Id == lastBoundId` | "Second narration on the same clip — it accumulates." (AC3) |
| `sequenceSkip` | stable, never-bound segments exist with `Id` strictly between `lastBoundId` and `Chosen.Id` | "Played but never tagged: […]." (AC4) |

---

## 4. ResolveReview (Core) — reuse, don't reinvent

New entry point on `WatchingResolver` (or a thin `ReviewWatchingResolver` collaborator):

```csharp
WatchingResult ResolveReview(
    string libraryRoot, IReadOnlyList<TitleSegment> segments, IProcessWindowSource windowSource,
    long lastBoundId, DateTimeOffset now, int limit, bool includeAccessFallback);
```

Steps:

1. Run `ReviewBindingResolver` → `ReviewBinding`.
2. Build `WatchContext` over the **chosen** segment's title (via the new `Build` overload taking
   supplied `ProcessWindow`s) and run the **existing** `Resolve` pipeline unchanged.
3. **Cold start** (empty snapshot): one live `GetPlayerWindows` poll → exactly today's behavior
   (covers the sub-250 ms first-call gap and the no-player case).
4. Promote/annotate the corrected bind, attach flags, return an enriched `WatchingResult`.

### 4.1 Three interactions that need explicit handling

- **Not-locked demotion guard.** Today an unlocked bare-name hit is demoted to low with
  *"may be a same-named file elsewhere"* (`WatchingResolver.cs:120-121`). A **corrected** bind is, by
  definition, the clip the user just advanced *away* from — *expected* to be unlocked, and the watcher
  saw that title play for `StableSeconds`. So **the not-locked demotion does not apply to a
  history-confirmed corrected bind.** It is reported with its true (unlocked) lock state but kept
  high-confidence, with a review note instead of the misleading caveat. Genuine basename ambiguity
  (the title matching two library files in different folders → `ByFileName` returns >1) is still
  `Ambiguous` → low, unchanged.
- **`anyLiveTarget`.** Extended: true if a `player_title` hit **or** `inUse` **or** a history-confirmed
  corrected bind. A corrected clip is a real, confident identification even though it is now writable
  (unlocked) — the caller treats it as a live target, not a recency guess.
- **The just-started current clip.** When corrected to the previous, the current (just-started) clip is
  demoted beneath the bind with a note (*"open now but only started Ns ago — probably not what you're
  describing"*), kept visible for transparency rather than hidden.

### 4.2 Multi-player ambiguity

When `AmbiguousMultiPlayer`, `ResolveReview` makes **no** correction and adds a `warning` of type
`multiple_players_active` (same inline mechanism as the existing `player_outside_library` warning):
*"More than one media player is active — too ambiguous to bind a clip safely. Confirm the exact path
with the user before tagging."* Raw candidates still return; nothing is auto-recommended.

---

## 5. Inline surface & MCP wiring

### 5.1 `library_watching` response (additive — no breaking change)

Existing fields (`candidates`, `anyLiveTarget`, `warning`, `drainedFromQueue`, `queuePending`) plus a
`review[]` array of `{ type, … }` objects; the recommended clip carries its note via the existing
`Candidate.Note`. The **tool description** is updated to instruct: prefer the top candidate; pass any
`review` entries to the user as a **non-blocking** heads-up (the user reconciles later — never a
blocking prompt, per the testrun UX finding); note that a recommended clip may be a now-unlocked
previous clip (directly writable). The model holds the running "needs review" list in its own context.

> Tool-description change ⇒ per CLAUDE.md the **full** `clipmetamcp.Tests` runs. **No tool is added or
> removed**, so `ToolsList_ContainsTheFullToolSurface` stays green.

`ResolveReview` surfaces the chosen segment's `Id` (and whether it was a confident single-match
recommendation) on the `WatchingResult`. The **shell** (`ReadTools.Watching`) then calls
`watcher.MarkBound(chosenId)` — but only on a confident single-match recommendation, so a
low-confidence/ambiguous call never creates a false `sameClipTwice`. `ResolveReview` itself stays a
pure function of `(segments, lastBoundId, now, …)`, holding no watcher reference. Documented
assumption: a `library_watching` call in this workflow precedes a tag, so "recommended = bound" is a
fair proxy.

### 5.2 Lifecycle (mirrors `QueueDrainPump`)

- `Program.Serve()` constructs the `ReviewWatcher` after the sandbox, **only when a library root is
  configured**, with `ProcessWindowSource.ForCurrentPlatform()`, `() => DateTimeOffset.UtcNow`, 250 ms;
  `Start()`s it before `session.Run()`; `Dispose()`s it after `Run()` returns on stdin EOF.
- `ReadTools.RegisterAll` gains the watcher as a **trailing optional injectable (default null)**. When
  null — tests, or no library — `library_watching` uses today's live-poll `Resolve` verbatim (graceful
  degradation; existing tests keep exercising that path).
- The watcher and the drain pump coexist as independent Core background drivers owned by the shell. The
  watcher is **read-only** (never writes a file), so it cannot race the pump or any writer — no
  `WriteGate` involvement.
- **Post-install restart quirk:** `Start()` only launches a thread (no library scan, no blocking work),
  so it adds nothing to `initialize` latency and comes up cleanly after the documented Desktop restart.

---

## 6. Edge cases & error handling

- Watcher tick never throws (try/catch → log & continue; a transient `GetPlayerWindows` hiccup skips
  the tick).
- Empty snapshot / no player → no chosen title → access-time fallback + `anyLiveTarget:false`
  (preserved).
- Title resolving to no library clip → existing `player_outside_library` warning; no correction.
- Clip renamed mid-session (seen in the log) → resolution is fresh at call time, so the new name
  resolves; a stale old-title segment won't match and is ignored.
- Threshold boundary → `<` strict; one named constant, tunable.
- Two narrations, no advance → `sameClipTwice`; existing per-field append accumulates both — no loss.
- Long session → ring buffer drops oldest beyond `maxSegments`.
- Dispose race → stop-signal + join before `Serve()` returns.

---

## 7. Affected types (change summary)

| Type | Change |
|---|---|
| `TitleSegment` (new, Core) | Record: `Id`, `ProcessName`, `RawTitle`, `StartedAt`, `EndedAt?`. |
| `ReviewWatcher` (new, Core) | Background poller; `Start`/`Snapshot`/`MarkBound`/`LastBoundId`/`Dispose`. |
| `ReviewFlag` (new, Core) | Discriminated flag (`autoCorrected`/`sameClipTwice`/`sequenceSkip`) + payload. |
| `ReviewBinding` (new, Core) | Heuristic output. |
| `ReviewBindingResolver` (new, Core, pure) | The previous-stable rule + flag derivation. |
| `WatchContext` | + `Build` overload taking supplied `IReadOnlyList<ProcessWindow>`. |
| `WatchingResolver` | + `ResolveReview`; not-locked-guard exception + `anyLiveTarget` extension for a corrected bind; just-started current demotion. |
| `WatchingResult` | + `Review` flags (and any corrected-bind metadata) — additive. |
| `clipmetamcp` `ReadTools` | `library_watching` takes the watcher (trailing injectable); calls `ResolveReview`; emits `review[]`; description update. |
| `clipmetamcp` `Program` | Construct/Start/Dispose the `ReviewWatcher` around the session. |

No change to `IWatchSignal`, the signals' emission, the queue, the pump, or any write path.

---

## 8. Test strategy

Run the **full** `clipmetascribe.Tests` and `clipmetamcp.Tests` (tool-description change) — not a
`--filter` (CLAUDE.md surface rule).

- **`ReviewBindingResolverTests`** (pure, fake clock): previous-stable correction (Run-2 dict4 replay
  → binds `_4`); no correction when current is stable; empty/cold; ambiguous multi-player; threshold
  boundary; `sameClipTwice`; `sequenceSkip`.
- **`ReviewWatcherTests`** (fake `IProcessWindowSource` + fake clock): title change opens/closes
  segments; vanished player closes its segment; ring-buffer cap; `Snapshot()` is an isolated copy; a
  throwing source is swallowed; `MarkBound`/`LastBoundId`; `Dispose()` joins.
- **`ResolveReview` integration** (scratch clips + fake source/segments): corrected bind promoted
  **high + unlocked + review note** (not the not-locked caveat); `anyLiveTarget:true` on a corrected
  bind; just-started current demoted; access fallback preserved with no correction; multi-player and
  wrong-dir warnings preserved.
- **`clipmetamcp` `LibraryWatchingToolTests`**: `review[]` present when flagged; `anyLiveTarget`
  present; `ToolsList_ContainsTheFullToolSurface` still green; stdout purity with the watcher running;
  watcher-null fallback path.
- **Regression:** existing `WatchingResolverTests`, queue, and pump suites stay green.

---

## 9. Risk table

| # | Risk | Mitigation |
|---|---|---|
| 1 | Heuristic mis-corrects when the user genuinely advanced *then* dictated about the NEW clip within the threshold | Documented failure mode; the timestamp increment (§ deferred) resolves it exactly. Threshold tunable; this is rarer than the natural watch→dictate→advance rhythm. |
| 2 | Promoting a corrected (unlocked) bind past the not-locked guard re-opens the same-named-file risk | Basename→multiple-clip ambiguity still demotes to low; only the *lock-state* reason for demotion is waived, and only for a history-confirmed bind. |
| 3 | Watcher thread cost (4 polls/s for the session) | Title polling only; no library/MP4 work; same call kind `library_watching` already makes once per call. |
| 4 | `MarkBound`-at-recommend over-counts `sameClipTwice` if `library_watching` is called without tagging | Only confident single-match recommendations mark bound; documented assumption; worst case is a spurious advisory flag, never a wrong write. |
| 5 | Two players open mid-session | `AmbiguousMultiPlayer` → no correction + explicit warning (owner-confirmed behavior). |
| 6 | Watcher startup worsens the post-install initialize timeout | `Start()` is non-blocking (thread launch only); no scan. |
| 7 | Stale segment titles after a clip rename | Resolution is fresh at call time; stale titles simply don't match. |

---

## 10. Definition of Done

1. `dotnet build` — 0 warnings, 0 errors, all projects.
2. `dotnet test` — full `clipmetascribe.Tests` and `clipmetamcp.Tests` pass (not filtered), including
   the new `ReviewBindingResolverTests` (Run-2 dict4 regression), `ReviewWatcherTests`, and the
   `ResolveReview` integration + `library_watching` `review[]` tests.
3. The reproduced off-by-one binds the correct (previous-stable) clip — AC1 closed.
4. `sameClipTwice` (AC3) and `sequenceSkip` (AC4) flags emit; cold start (AC5) binds via one live poll.
5. `anyLiveTarget` covers a corrected bind; the not-locked guard does not demote it.
6. No MCP tool added/removed; `ToolsList_ContainsTheFullToolSurface` green.
7. Zero NuGet packages added to production projects.
8. New public types/methods documented; `docs/PITFALLS.md` updated with the poll-at-call-time race and
   the not-locked-guard exception.

---

## 11. Deferred / follow-on (recorded for continuity)

- **Timestamp ingestion / fire-N-ahead (AC2):** the next increment. The no-timestamp heuristic resolves
  *one* dictation against the segment tail; mapping N backlogged dictations onto N historical segments
  needs a per-dictation submit-time key into the segment log. Sequenced after this watcher.
- **Gaming mode / `FileSystemWatcher` (AC7–AC8):** separate spec — newest-written-file binding, no
  player introspection.
- **Secondary-fix batch (testrun §7):** dry-run preview computes current-state not predicted-state;
  MPC title-retry before access-time fallback; a queue-drain→provenance **test** (behavior correct on
  `main`, untested); notes prose separator (`" "` → `". "`); bounded retry on the initial source-open
  lock; `drainedFromQueue` echo on a no-op drain.
- **Persisted review file / dedicated review tool:** only if in-session inline flags prove insufficient.
- **Field-normalization depth:** local rules vs LLM — tuning, deferred.
