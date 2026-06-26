# Gaming Mode — Recent-Write Clip Resolution — Design Spec

**Date:** 2026-06-26
**Status:** Approved-for-build. The §3.1 decision is **resolved: Policy A** (owner, 2026-06-26) — a single freshly-written clip with no player open is an auto-taggable live target.
**Pass:** pass-4, final deferred slice (follows leftovers PR #37 and AC2 PR #38).

---

## 1. Problem

Every watched-clip path so far assumes a **media player is open** (player-title resolution) or recently was (access-time fallback). The remaining scenario from the dogfood backlog is **gaming mode**: the user is playing a game (no media player open at all), presses their capture key, a clip is **saved to the library folder**, and they want to tag "the clip I just made" — by voice, without alt-tabbing to open it.

Today `library_watching` in that situation has only the access-time fallback, which is:
- **low confidence** (any app can bump access time), and
- explicitly **not a live target** (`anyLiveTarget=false`), so the model is told *do not auto-tag — confirm the exact path with the user first*.

That makes zero-friction "tag the clip I just saved" impossible while gaming. We need a reliable signal for "this clip was just written to disk."

---

## 2. The signal: recent write time (synchronous, no FileSystemWatcher)

A clip the game just saved has a **`LastWriteTimeUtc` of ~now**. Unlike access time, write time is *not* bumped by merely playing an old clip, so it cleanly distinguishes "just created" from "recently watched." We already enumerate the whole library once per resolution; reading each file's write time there costs nothing extra.

**Decision — synchronous signal, not a `FileSystemWatcher`.** The original backlog note said "FileSystemWatcher." On reflection a background FSW buys nothing for *this* use case: the only fact we need ("which clip was written most recently, and how long ago") is already available synchronously from `File.GetLastWriteTimeUtc`, read during the existing single enumeration. A synchronous `IWatchSignal` adds **no background thread, no disposal surface, no platform FSW quirks**, and matches the codebase's open-for-extension signal model exactly. (A FSW would only be worth it for *exact creation-time binding* — the gaming analogue of AC2's `spoken_at`, binding the clip created nearest the spoken instant. That is a possible future enhancement, explicitly out of scope here.)

### New component
`RecentWriteSignal : IWatchSignal` (`SourceName = "recent_write"`):
- Constructor takes an injectable `Func<DateTime>? clock` and `TimeSpan? window` (defaults: real clock, **5-minute** freshness window — see §3.2).
- `Detect`: emit a hit for each clip whose `LastWriteTimeUtc` is within `window` of now, newest-write first. `Ambiguous = (number of in-window clips > 1)` — exactly one fresh clip is the unambiguous "just saved" case.

### Supporting change
`LibraryClip` gains `DateTime LastWriteTimeUtc`, populated in `WatchContext.EnumerateLibrary` from `File.GetLastWriteTimeUtc` (same try-block as access time; a file whose times can't be read is still skipped).

---

## 3. Integration into the resolver — and the open decision

`RecentWriteSignal` registers in `WatchingResolver.CreateDefault` alongside the existing two. In `ResolveCore`, recent-write is treated as a **fallback tier strictly stronger than access-time but weaker than a player hit**, and it only matters when **no player resolved** (gaming = no player). Concretely:

- A path with a **player hit** is unchanged — the player dominates; its recent-write hit is ignored for sourcing. (This preserves *every* existing player-path test and the review/AC2 paths: a confidently chosen clip is never diluted by background saves.)
- A path with **no player but a recent-write hit** → `Source = "recent_write"`, and `Confidence = high` iff it is the single in-window clip, else `low` (mirrors the player-title unambiguous/ambiguous rule the owner already approved).
- Access-time-only paths are unchanged, and are dropped beneath a recent-write winner the same way they are dropped beneath a player winner (existing §8a rule, extended).
- Recent-write hits survive `include_access_fallback:false` (they are the gaming signal, not the recency hint) but are still suppressed by the wrong-directory rule (a player showing a foreign file means the user is *not* gaming).

### 3.1 DECISION (RESOLVED — Policy A) — does a just-saved clip become an auto-taggable "live target"?

**Resolved 2026-06-26: Policy A.** A single freshly-written clip (no player open, no lock) is treated as a high-confidence live target (`anyLiveTarget=true`); multiple fresh writes stay `low`/confirm. This intentionally reverses the §196 contract for the single-fresh-write case.

This was the one call deferred to the owner, because it **reverses an explicit owner-approved contract.** `WatchingResolverTests.Resolve_NoPlayerNoLock_AnyLiveTargetIsFalse` deliberately asserts: *no player open + file not locked ⇒ `anyLiveTarget=false` ⇒ the model must confirm before tagging.* A clip saved while gaming has no player and (once the save completes) no lock — so gaming mode's value depends on flipping exactly that case to "confident / taggable."

The three viable policies:

| Policy | `anyLiveTarget` for a single fresh write (no player, unlocked) | Effect |
|--------|-------------|--------|
| **A. Treat as live (recommended)** | `true` (when it's the *single* in-window clip) | Zero-friction "tag the clip I just made." Reverses the §196 contract for the fresh-write case only; multi-fresh stays `low`/not-live. |
| **B. Strictly safe** | `false` always | Recent-write still ranks above access-time and is clearly labeled, but the model must still confirm the path. Preserves the existing contract verbatim. |
| **C. Freshness-gated** | `true` only if written in the last ~30–60s | "Just clipped" is auto-taggable; "saved a few minutes ago" still asks. A middle ground; more moving parts. |

Everything else in this spec is independent of this choice; only the `anyLiveTarget` clause and which existing fallback tests change depend on it.

### 3.2 Freshness window default

5 minutes for "is this a recent write at all" (controls whether the clip is surfaced as `recent_write` vs ordinary access-time). If Policy C is chosen, a separate, tighter "auto-tag" sub-window (~30–60s) gates `anyLiveTarget`. Both are constants, easily tuned after a dogfood.

---

## 4. Tests

**New (`RecentWriteSignalTests`, pure/synthetic):** single fresh write → one unambiguous hit; two fresh writes → both ambiguous; a clip written outside the window → no hit; injected clock drives all timing.

**New (`WatchingResolverTests` gaming cases):**
- single fresh write, no player → `recent_write`, ranks first, confidence per §3.1, access-time rows dropped beneath it;
- two fresh writes, no player → both `recent_write` low;
- fresh write **with** a player open on another clip → player wins, fresh write does not displace it.

**Existing tests that change with this feature (must be reconciled, not blindly edited):** the `Touch()`-based fallback tests create freshly-written files, so several will shift from `access_time` to `recent_write` sourcing, and — under Policy A or C — `Resolve_NoPlayerNoLock_AnyLiveTargetIsFalse` inverts for the single-clip case. Each change will be justified in the PR as correct new behavior, not a masked regression. Affected: `Resolve_NoPlayer_FallsBackToMostRecentAccessAsLow`, `Resolve_NoHighWinner_KeepsAccessTimeRows`, `Resolve_NoPlayerNoLock_AnyLiveTargetIsFalse`, `Resolve_PlayerWithNoFilenameInTitle_StaysQuiet`, and the cold-start fallback case in `ResolveReviewTests`.

**Full suites** (`clipmetascribe.Tests`, `clipmetamcp.Tests`) green; the MCP tool description gains a `recent_write` sentence (no new tool/arg → surface guard unaffected, but run the full MCP project per the CLAUDE.md rule).

---

## 5. Risks

| Risk | Mitigation |
|------|-----------|
| **Reverses an owner safety contract** (§3.1) | Not shipping until the owner picks A / B / C. |
| Recent-write pollutes player/review/AC2 paths | Recent-write is ignored on any path with a player hit, and dropped beneath any player/recent-write winner — those paths are unchanged. |
| A clip still being written shows mid-write | It is locked → `inUse:true` → the deferred-tag queue already handles "tag when the lock clears." |
| Write-time granularity / app re-touching write time | 5-min window is generous; worst case falls back to access-time (today's behavior). |
| Several existing tests shift | Reconciled individually with written justification; full suite is the regression net. |

---

## 6. Definition of Done

1. Owner picks the §3.1 policy.
2. `dotnet build` 0/0; full `clipmetascribe.Tests` + full `clipmetamcp.Tests` green.
3. Zero NuGet added.
4. `recent_write` documented on the tool; new types/params carry XML docs; any gotcha → `docs/PITFALLS.md`.
