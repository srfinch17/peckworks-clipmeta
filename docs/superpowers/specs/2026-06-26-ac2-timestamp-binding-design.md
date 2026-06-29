# AC2, Spoken-at Timestamp Binding & Fire-N-Ahead, Design Spec

**Date:** 2026-06-26
**Status:** Approved-for-build (autonomous pass-4 deferred slice; design surfaced here for review at PR time)
**Pass:** pass-4 (follows the review-mode watcher, `2026-06-26-review-mode-watcher-design.md`, and the dogfood follow-ups, `2026-06-26-pass4-dogfood-followups-design.md` §7 "deferred: AC2")

---

## 1. Problem

The review-mode watcher (pass-4) fixed the poll-at-call-time binding race with a *timing heuristic*: it records timestamped title segments and, at tool-call time, binds the **previous-stable** segment when the current one only just started. That closes the common case (user dictates a tag a beat after the clip advanced).

But the heuristic only ever reasons about **now**. It cannot bind a clip the user described *several clips ago*. Two real scenarios break it:

1. **Backlog / batch dictation.** The user narrates tags for clips 1–4 in a burst while the player has already moved to clip 5. The model processes them a turn later. The heuristic can only offer one answer (the previous-stable clip relative to *now*), it has no way to map "the second thing I said" to clip 2.
2. **Latency between speech and tool call.** Even a single dictation can arrive at the server well after the user spoke, by which time the player may have advanced one or more clips past the previous-stable one. The 2-second threshold is a guess at this latency; it is not a measurement.

The watcher already *has* the data to resolve both exactly: a history of which title played during which wall-clock window. What is missing is the **one fact only the client knows, when the user actually spoke.** If the MCP client passes that timestamp, the server can look up the exact segment covering it and bind the right clip deterministically, no heuristic guess.

This also unlocks **fire-N-ahead**: to clear a backlog, the model issues N `library_watching` calls, one per dictation, each carrying that dictation's `spoken_at`. Each call resolves its own historical clip independently.

---

## 2. Scope

### In
- An optional `spoken_at` (ISO-8601 / RFC-3339 timestamp) argument on the `library_watching` MCP tool.
- Server-side **exact segment lookup**: find the recorded title segment whose `[StartedAt, EndedAt)` window covers `spoken_at`, and bind that clip.
- **Heuristic fallback** when `spoken_at` is absent (today's behavior, unchanged) OR when it is present but matches no recorded segment (history aged out, or a gap with no player open), with a `timestampUnmatched` advisory flag so the caller knows it fell back to a guess.
- **Multi-player ambiguity at the spoken instant**: if two different players' segments cover `spoken_at`, refuse to bind (existing `multiplePlayersActive` semantics).
- Tool-description guidance instructing the model to pass `spoken_at` (the time the user dictated) whenever it is known, especially when clearing a backlog, and explaining the fire-N-ahead pattern.

### Out
- **No new tool, no array/batch argument.** Fire-N-ahead is N ordinary single-clip calls; one `spoken_at` per call. (YAGNI: a batch arg duplicates the per-call resolution path and complicates `MarkBound` sequencing for no capability gain.)
- **No client-side changes.** We cannot make Claude Desktop emit timestamps; we make the server *accept* one and degrade gracefully when it does not. Whether any given client populates `spoken_at` is the client's concern.
- **No persistence of segment history across server restarts.** The watcher's ring buffer is in-memory (unchanged). `spoken_at` older than the buffer simply misses and falls back, correct behavior.
- Gaming mode (`FileSystemWatcher`, newest-written clip), its own later slice.

---

## 3. Architecture

One new optional parameter threaded through the existing review pipeline. No new types beyond one flag constant.

```
library_watching(spoken_at?)                 ── clipmetamcp/Tools/ReadTools.cs
        │  parse ISO-8601 → DateTimeOffset?   (lenient; bad/absent ⇒ null)
        ▼
WatchingResolver.ResolveReview(..., spokenAt?)   ── clipmeta.core/Watching/WatchingResolver.cs
        │  pass-through
        ▼
ReviewBindingResolver.Resolve(segments, lastBoundId, now, threshold?, spokenAt?)
        │
        ├─ spokenAt provided?
        │     ├─ segment(s) cover spokenAt?
        │     │     ├─ exactly one player  → bind that segment (no correction)        [EXACT]
        │     │     └─ 2+ players cover it  → AmbiguousMultiPlayer + flag             [AMBIGUOUS]
        │     └─ none cover it             → run heuristic, append timestampUnmatched [FALLBACK]
        └─ spokenAt absent → run heuristic exactly as today                          [HEURISTIC]
```

### 3.1 Coverage rule

A segment **covers** an instant `t` when:

```
segment.StartedAt <= t  &&  t < (segment.EndedAt ?? now)
```

- For the still-open (current) segment, `EndedAt` is null → the upper bound is `now`. An instant after `now` (client clock skew into the future) is not covered by anything → FALLBACK → heuristic binds the current clip, which is the right answer for "just now."
- Within a single player, segments are sequential and non-overlapping by construction (the watcher closes the prior segment when the title changes), so at most one same-player segment covers `t`.
- Across players, two segments can cover the same `t` (two players open at once) → AMBIGUOUS.

### 3.2 What EXACT returns

A `ReviewBinding` with `Chosen = covering segment`, `CorrectedFrom = null` (this is not a previous-stable correction, it is a direct hit), `AmbiguousMultiPlayer = false`. `ResolveReview` then promotes the single player-title candidate to `high` confidence exactly as it does for the heuristic path, sets `BoundSegmentId`, and marks `RecommendationConfident`. The promoted candidate keeps its true lock state (an old clip is almost certainly unlocked; a recently-passed one may still be held, both are fine, the bind is confident either way). The `CorrectedBindNote` is **not** applied (nothing was auto-corrected; the caller told us the time).

`sameClipTwice` and `sequenceSkip` flags are still derived relative to `lastBoundId` (useful: two dictations landing in the same segment legitimately means two tags for one clip; a skipped stable segment between binds is worth surfacing). `autoCorrected` is never emitted on the EXACT path.

### 3.3 The `timestampUnmatched` flag

New `ReviewFlag.TypeTimestampUnmatched = "timestampUnmatched"`. Emitted only when `spoken_at` was **provided but matched no segment**, alongside whatever the heuristic decided. It tells the model: "I could not find the exact moment you named (it may have aged out of history); this is a best-effort guess, confirm before tagging." Absent `spoken_at` never produces this flag (no exact lookup was requested). `Clips` is empty; the flag is purely a signal.

### 3.4 Fire-N-ahead sequencing

The model clears a backlog by issuing the dictations **oldest-first**, each call carrying that dictation's `spoken_at`. Each call:
1. resolves the exact segment for its timestamp,
2. is promoted to a confident bind, and
3. on the MCP side, `MarkBound(boundId)` advances `lastBoundId`.

Because the calls go oldest→newest and each binds an adjacent earlier segment, `sequenceSkip` does not fire spuriously between them. If two dictations fall in the same segment, the second call emits `sameClipTwice`, correct (two tags, one clip). The model then issues a write/queue call per resolved path.

---

## 4. Components & Changes

| File | Change |
|------|--------|
| `clipmeta.core/Watching/ReviewFlag.cs` | Add `TypeTimestampUnmatched` const + doc. |
| `clipmeta.core/Watching/ReviewBindingResolver.cs` | Add optional `DateTimeOffset? spokenAt` param to `Resolve`. Extract the existing heuristic body into a private `ComputeHeuristic(...)`. Add the EXACT / AMBIGUOUS / FALLBACK branch in front of it. FALLBACK appends `timestampUnmatched` to the heuristic result's flags. |
| `clipmeta.core/Watching/WatchingResolver.cs` | Add optional `DateTimeOffset? spokenAt = null` to `ResolveReview`; pass it to `Resolve`. (The EXACT path needs no special handling in `ResolveReview` beyond what the heuristic path already does, a `Chosen` segment with `CorrectedFrom == null` already takes the high-promotion branch.) |
| `clipmetamcp/Tools/ReadTools.cs` | `WatchingSchema`: add `spoken_at` string property. `Watching` handler: parse it leniently to `DateTimeOffset?` and pass to `ResolveReview`. Update the tool description with the dictation-time guidance and the fire-N-ahead note. |

No changes to `WatchingResult`, `ReviewBinding`, `TitleSegment`, or the watcher itself, the data is already recorded; AC2 only reads it differently.

### 4.1 Parsing `spoken_at`

`DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var dto)`. A missing or unparseable value yields `null` → the call behaves exactly like today's no-timestamp call (heuristic, no `timestampUnmatched` flag, we never attempted an exact lookup). We do **not** error on a bad timestamp: a watched-clip read must never fail on an optional convenience arg.

---

## 5. Testing

TDD, test-first. New/extended tests:

**`ReviewBindingResolverTests` (core, clip-less, pure):**
- `Resolve_SpokenAtInPastSegment_BindsThatSegment_NotCurrent`, segments _1(0–10s), _2(10–25s), _3(25–open); `spokenAt` at 15s → binds _2 (id 2), not the current _3. No `autoCorrected`.
- `Resolve_SpokenAtInOpenSegment_BindsCurrent`, `spokenAt` at 30s (inside the open segment) → binds the current segment, no correction.
- `Resolve_SpokenAtOutsideHistory_FallsBackToHeuristic_FlagsTimestampUnmatched`, `spokenAt` before the earliest segment → heuristic result + `timestampUnmatched` flag present.
- `Resolve_SpokenAtCoveredByTwoPlayers_Ambiguous`, overlapping vlc + mpc segments both covering `spokenAt` → `AmbiguousMultiPlayer`, `multiplePlayersActive` flag, `Chosen` null.
- `Resolve_SpokenAtAbsent_NoTimestampUnmatchedFlag`, regression: omitting `spokenAt` never emits `timestampUnmatched`.

**`ResolveReviewTests` (core + temp clips):**
- `ResolveReview_SpokenAt_BindsHistoricalClip_PromotedHighConfident`, three touched clips, three segments; `spoken_at` inside the middle segment → `Candidates[0].Path` is the middle clip, `high`, `RecommendationConfident`, `BoundSegmentId == 2`, no `autoCorrected` flag.
- `ResolveReview_FireNAhead_OldestFirst_BindsEachInTurn`, drive three calls with three timestamps (one per segment), threading `lastBoundId` via `BoundSegmentId` between calls; assert each resolves its own clip and no spurious `sequenceSkip`.

**`LibraryWatchingToolTests` (MCP shape, empty .mp4s):**
- `Watching_AcceptsSpokenAtArgument_NoError`, a call with a valid `spoken_at` string succeeds (shape intact, no `isError`).
- `Watching_BadSpokenAt_DegradesToHeuristic_NoError`, `spoken_at: "not-a-date"` does not error and does not emit `timestampUnmatched`.

**Surface guard:** `clipmetamcp.Tests` `ToolsList_ContainsTheFullToolSurface` is unaffected (no tool added/removed), but run the **full** `clipmetamcp.Tests` project per the CLAUDE.md rule, since the tool *schema* changed.

---

## 6. Risks

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Client never sends `spoken_at`, so AC2 is dead weight | Medium | The heuristic remains the default path; AC2 is additive and zero-cost when unused. Tool description nudges capable clients to send it. |
| Clock skew between client and server skews coverage | Low–Med | Future `spoken_at` falls through to heuristic (binds current, correct). Past skew within a segment still lands in the right segment unless skew exceeds a clip's play length (seconds-to-minutes); acceptable, and `timestampUnmatched` covers the gross-miss case. |
| `spoken_at` aged out of the ring buffer (64 segments) | Low | FALLBACK + `timestampUnmatched` flag → model confirms with user. 64 segments covers a long session. |
| Fire-N-ahead out-of-order calls trip `sequenceSkip` | Low | Documented contract: issue oldest-first. Skip flag is advisory, never blocks a write. |
| Bad timestamp string crashes the read |, | Lenient parse → null → heuristic. Never throws. |

---

## 7. Definition of Done

1. `dotnet build`, 0 warnings, 0 errors, all projects.
2. `dotnet test`, full `clipmetascribe.Tests` and full `clipmetamcp.Tests` green (the tool schema changed → full MCP project, not a filter).
3. Zero NuGet added.
4. `spoken_at` documented in the tool description with the fire-N-ahead pattern; new flag and method params carry XML doc comments.
5. New gotchas (if any) appended to `docs/PITFALLS.md`.
