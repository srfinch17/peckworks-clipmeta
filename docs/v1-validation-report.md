# ClipMeta v1 Release Validation Report

**Date:** 2026-06-29
**Build target:** v1 (validated against v1.6.0 / pass-7)
**Libraries exercised:** `ClipmetaTesting` (51 clips), production Team Fortress 2 library (~3,700 clips)
**Verdict:** v1 ready to ship.

---

## Summary

This session validated the watch-and-tag MCP workflow end to end, first against the
51-clip test library and then against the full production library. The core gaming-mode
loop (clip a moment, dictate a tag, resolve the live target, write metadata into the MP4)
held up cleanly across the run. The edge cases encountered all resolved into one of two
buckets: working as designed, or a documented and gracefully-degrading limitation. None
corrupts data or writes silently to the wrong target on the normal path.

The final open item, behavior and responsiveness at production scale, was smoke-tested
at the end of the session: a single `library_watching` call against ~3,700 files returned
one high-confidence `recent_write` candidate with no fallback clutter and a fast turnaround,
and the write landed correctly.

---

## What was validated

### Core workflow (test library)

Six consecutive resolve-and-tag operations in gaming mode, no media player open,
`FileSystemWatcher` detecting each new clip:

| Clip | Players | Tags | Notes |
|------|---------|------|-------|
| 161.DVR_1 | chuck | main menu, inventory | |
| 162.DVR_2 | chuck | scout, loadout, paint | |
| 163.DVR_3 | chuck, chicken | soldier, loadout, backpack, inspecting | two players in one clip |
| 164.DVR_4 | chuck, chicken | airshot, pit, highlight, kill | rating 5 |
| 165.DVR_5 | chicken | 360 noscope, headshot, sniper, highlight, kill | rating 5, solo |
| 168.DVR_6 | chuck | market garden, upward, red | tagged after explicit path confirmation (see Finding 1) |

Each write produced a timestamped backup and was confirmed by file-state readback.

### Scale smoke test (production library)

Library pointer switched to the full ~3,700-clip Team Fortress 2 library. A live clip was
recorded and a single `library_watching` call issued:

| Clip | Players | Tags | Result |
|------|---------|------|--------|
| 229.DVR | chuck | main menu | single high-confidence `recent_write`, no access_time fallback, fast resolve, clean write |

This confirms the enumeration path does not choke or hang at production size on the happy
path (n=1). See the scale caveat under Open items.

---

## Findings and dispositions

### Finding 1: `multiple_players_active` masks `recent_write` (documented limitation)

With two media players open (VLC and MPC), `library_watching` returns
`anyLiveTarget: false` and a `multiple_players_active` warning, and a legitimate
`recent_write` candidate does not surface as a live target. Confirmed by closing both
players and watching the same `recent_write` resolve clean at high confidence.

**Disposition:** Acceptable known limitation. The failure mode is graceful: the tool
reports "too ambiguous, confirm the path" rather than binding the wrong clip or writing
silently. The assistant degrades to asking for an explicit path. This is the intended
interaction of two prior fixes, the pass-6 `multiplePlayersActive` confidence cap (≥2 open
players ⇒ confirm) correctly takes precedence over Policy A's single-fresh-save survival.
Not a ship blocker.

### Finding 2: outside-library warning suppressed when multiple players active (minor messaging gap)

When both open players are showing files outside the configured library, the
`multiple_players_active` check takes precedence and the `player_outside_library` warning
never surfaces. The user is told the bind is ambiguous but is not told that both players
are pointed at the wrong folder.

**Disposition:** Minor, non-blocking. Worth a small precedence or message-merge fix later
so the outside-library signal is not fully masked. Does not affect data integrity.

### Finding 3: forged NTFS creation-time surfaces a clip as `recent_write` (works as intended)

Gaming-mode freshness (`RecentWriteSignal`) keys on **NTFS creation time**, not write time.
A deliberate test bumped an already-tagged clip's timestamps to "now" with PowerShell, 
including `$item.CreationTime = Get-Date`, and clip 168.DVR_6 then surfaced as a
high-confidence `recent_write`.

This is correct behavior, not a bug. A file whose creation time genuinely reads "now" is,
by the signal's definition, indistinguishable from a fresh save, so it *should* surface.
Three layers were verified to explain it:

- `RecentWriteSignal` reads only `CreationTimeUtc`; there is no write-time ("mtime") path to
  invert. The signal classified a created-now file as fresh.
- ClipMeta's own tag write goes through `File.Replace`, which **preserves the destination's
  original creation time**, so tagging _6 never made it look fresh; only the manual forge did.
- The `SelfActionLedger` (which masks clips ClipMeta itself wrote for ~5 minutes) had
  legitimately expired by the time of the re-resolve, and it guards ClipMeta's own actions,
  not an external `touch`-style edit by the user.

**Disposition:** Works as intended. No code change. The trigger requires a manual
creation-time forge that does not occur in normal recording. *(An earlier "mtime-inversion"
read of this finding was a misdiagnosis, the test set `CreationTime`, which is the field the
signal actually uses.)*

### Finding 4: overwrite guard and `hasMetadata` candidate flag (considered, dropped)

A guard against writing onto an already-tagged live target was evaluated and rejected. The
"live target already has tags" state is reachable through normal re-tagging (for example,
adding a second player to a clip moments after the first tag), and in that case writing to
the tagged clip is the correct, intended behavior, tag accumulation handles it by design.
A blocking check would interrupt a legitimate and common flow.

**Disposition:** Works as intended. No guard added. The optional `hasMetadata` flag on
candidates was also dropped to avoid injecting confirmation noise into a path that is
functioning correctly.

---

## Open items

None blocking v1.

Carried forward as small, non-urgent items:

1. Precedence or message-merge so the `player_outside_library` warning is not masked by
   `multiple_players_active` (Finding 2).

**Scale caveat (not a blocker):** production-scale behavior was smoke-tested at n=1 on the
happy path. Repeated calls, the `access_time` fallback at ~3,700 files, and a players-open
resolve at that size were not separately timed. The enumeration is known not to hang; broader
scale timing is a nice-to-have, not a release gate.

---

## v1 sign-off

The core watch-and-tag workflow is validated, and smoke-tested at production scale. The
remaining findings are either intended behavior or a gracefully-degrading documented
limitation. Nothing on the normal path corrupts data or writes to the wrong target. ClipMeta
is ready to be tagged v1.
