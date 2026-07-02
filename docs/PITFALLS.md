# PITFALLS, peckworks-clipmeta

A living log of mistakes, gotchas, and hard-won knowledge. **Append a new entry whenever we hit and fix a real bug or discover something non-obvious.** Consult this before touching the MP4 parser or writer.

Format: newest entries at the top of "Field-discovered." The "MP4 format hazards" section below is seeded from the write-engine design spec and is foundational reference.

---

## Field-discovered (append here as we go)

## 2026-07-01, `BinaryReader.ReadBytes` does not throw at EOF, it returns a short array (task B1)
**Symptom:** A nemesis review truncated a file mid-header (a box declaring an extended size,
`size == 1`, whose 8-byte extended-size field is cut off at EOF) and the whole directory scan
died with a raw, uncaught `System.ArgumentException: The array starting from the specified
index is not long enough...`, naming no file, outside every scanner's catch list
(`ClipMetaFinder`/`ClipMetaIndex`/`ClipMetaVocab`/`ClipMetaExporter` all caught `IOException`,
`UnauthorizedAccessException`, and `InvalidDataException`, none of which `ArgumentException` is).
**Cause:** `BigEndianReader.ReadUInt16/32/64`/`ReadFourCC` called `BinaryReader.ReadBytes(n)`
directly and fed the result straight to `BitConverter`. `ReadBytes` is documented to return
fewer bytes than requested at end-of-stream instead of throwing, so a truncated file handed
`BitConverter` a short array, which throws `ArgumentException`, a type nothing downstream
expected or caught.
**Fix:** `BigEndianReader` now routes every fixed-width read through a private `ReadExactly`
helper that throws `EndOfStreamException` on a short read. `EndOfStreamException` derives from
`IOException`, so it is caught by scanners' pre-existing `catch (IOException)` even without any
further change, and `Mp4Parser.Parse`'s outer `catch (EndOfStreamException) -> InvalidDataException`
still fires for the (rare) case where the short read happens outside `ParseBoxes`' own lenient
header-read catch.
**Non-obvious wrinkle, do NOT "fix" this without re-reading first:** for the *specific*
demonstrated construction (extended-size field truncated), the failure is inside
`Mp4Parser.ParseBoxes`' box-header read, which is wrapped in its own
`catch (EndOfStreamException) { break; }` (the deliberate "a damaged file should still be
viewable up to the damage" leniency). That catch swallows it and returns whatever was already
parsed (empty, if this is the first box), it does **not** rethrow, so `Mp4Parser.ParseFile`
does **not** throw `InvalidDataException` for this exact file shape, it succeeds with an
empty/partial tree. This is intentional and load-bearing: `clipmetamcp.Tests/LibrarySandboxTests.cs`
(`GetMetadata_ThroughJunctionPointingInsideLibrary_StillWorks` et al.) and
`clipmetamcp.Tests/Phase2ReadToolsTests.cs`'s shared 8-byte `noise.mp4` fixture both explicitly
assert that a too-short/garbage `.mp4` parses successfully to an empty tree. A guard like
"throw if a non-empty file parses to zero top-level boxes" looks like the obvious next
hardening step and will break both of those (and contradicts the parser's own documented
leniency comment). Verified empirically before and after the fix, see
`clipmetascribe.Tests/Mp4ParserTruncatedFileTests.cs`.
**Lesson:** `BinaryReader.ReadBytes(int)` silently short-reads at EOF, it never throws. Any
fixed-width binary parsing code must check the returned length itself. Before adding a stricter
EOF guard to the parser, grep for existing tests asserting the current lenient behavior, several
already encode "too-short-to-parse is not an error" as a contract, not an oversight.

## 2026-06-29, A forged NTFS creation-time is `recent_write` working AS DESIGNED, not an "mtime" bug
**Symptom:** A v1 dogfood deliberately bumped an already-tagged clip's timestamps to now
(PowerShell: `$i.LastWriteTime/$i.CreationTime/$i.LastAccessTime = Get-Date`) and the clip
re-surfaced as a high-confidence `recent_write`. The in-session narration (and a first-draft
handoff doc) called this an **"mtime-inversion bug"** and filed a deferred "mtime fix."
**Cause / reality:** `RecentWriteSignal` reads **only `CreationTimeUtc`**, there is no
write-time path to invert. The bump set `CreationTime` too, so the file genuinely read
"created now" and the signal *correctly* classified it as a fresh save. It is indistinguishable
from a real fresh clip by design. The `SelfActionLedger` did not mask it because (a) `File.Replace`
preserves the destination's original creation time, so tagging never made _6 look fresh, and
(b) the ledger's ~5-min self-write window had expired and it guards ClipMeta's OWN actions, not
a user's external `touch`.
**Disposition:** WORKS AS INTENDED. No code change. The "deferred mtime fix" was a phantom, 
it targeted a code path that does not exist. (See the foundational `2026-06-26, recent_write
must key on CREATION time` entry below for why creation-time is the right key.)
**Lesson:** Before recording a dogfood symptom as a bug, reconcile the *suspected mechanism*
against the code. "mtime bumped → re-surfaced" is impossible here; only a creation-time forge
explains it, and that is the design behaving correctly. A manual timestamp forge is not a
normal-path trigger, don't add a guard or a fix for it.

## 2026-06-29, A `--` in an MSBuild XML comment breaks every build (version-certainty setup)
**Symptom:** Adding `Directory.Build.props` with a comment that documented the CLIs' `--version`
flag failed all seven projects at evaluation: `MSB4024 ... An XML comment cannot contain '--'`.
**Cause:** XML forbids `--` inside a comment, and MSBuild imports `Directory.Build.props` before
anything compiles, so the malformed comment takes the whole build down (not one project). The
literal `--version` / any `--flag` in the comment is the trap.
**Fix:** Reworded the comment to avoid `--` (e.g. "version-flag output"). When documenting CLI
flags inside a `.props`/`.csproj` comment, never write a bare double-dash.
**Lesson:** prose that's safe in a `.cs` `//` comment can be a hard build error in an MSBuild XML
comment, `--`, unescaped `<`/`&`, etc. Build the file, don't eyeball it.

## 2026-06-28, Review-mode resolver: diagnose live, bind from history (pass-7)

`WatchingResolver.ResolveReview` must answer two questions from two time-bases. "Is a foreign player
open right now?" (the `player_outside_library` diagnostic + access suppression) MUST come from a LIVE
player poll, never from `binding.Chosen`'s segment, which may be a player that has since CLOSED.
Replaying a closed segment as a synthetic window made closed players "ghost" (warn after exit) and,
combined with the review-mode `recent_write` strip, blanked a valid in-library gaming candidate
(`anyLiveTarget:true` beside `candidates:[]`). "Which clip did the user describe?" (the bind) is the
historical question and is resolved separately from the chosen segment. Derive `anyLiveTarget` from
the FINAL candidate list (shared `IsLiveTarget` predicate) so true-beside-empty is impossible. Pass-6's
Policy A fix lived in `ResolveCore`; its tests never drove `ResolveReview` with a foreign SEGMENT
present, so the regression hid, always test the resolver through `ResolveReview` with seeded segments.

## 2026-06-28, Queue: wake the drain pump only for locked clips (pass-7)

`library_queue_tag` waking `QueueDrainPump` unconditionally made the (event-driven) pump drain an
UNLOCKED clip and book it under `autoFlushed` before an explicit `library_flush_queue` ran, so
`flush` reported `written:[]` though the write succeeded. Wake the pump only when `LockProbe.IsInUse`
is true; an unlocked tag then lands via the foreground drain and reports under `written`. The pump
idles on an event, so not waking it means no race with the foreground flush.

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

## 2026-06-26, Adding a ledger/creation-time signal makes test fixture timestamps load-bearing
**Symptom:** When `RecentWriteSignal` was extended with a self-action ledger and keyed on NTFS
creation time instead of write time, a broad wave of watching tests broke: tests that had nothing
to do with gaming mode started failing because their `TouchStale` helper only back-dated
`LastWriteTimeUtc`, creation time and access time were still "now" and the new signal fired.
**Cause:** Every new signal that reads a file's timestamp silently turns the fixture-creation
machinery into signal input. `TouchStale` was written to satisfy `RecentWriteSignal` v1 (write-time
only); adding creation time and ledger signals expanded what "stale" means without updating the
helper.
**Fix:** `TouchStale` now sets ALL THREE timestamps (`CreationTimeUtc`, `LastWriteTimeUtc`,
`LastAccessTimeUtc`) to a far-past value. Any test whose outcome depends on a timestamp signal
must set that timestamp **explicitly**, implicit "just created" times are forbidden in
timestamp-dependent test fixtures.
**Lesson:** When a signal's inputs grow (new timestamp field, new ledger entry), every test
helper that produces signal inputs must be audited and updated to cover the new fields. A helper
named `TouchStale` that only sets one of three timestamps is a latent "freshness leak" waiting
for the next signal extension.

## 2026-06-26, `recent_write` must key on CREATION time, not write time
**Symptom (two failure modes):** (1) A clip copied into the library (e.g. via Windows Explorer
or robocopy) looked OLD to `RecentWriteSignal` and was invisible to gaming mode, copy preserves
the source file's original `LastWriteTimeUtc`, so a clip made yesterday but copied now has an
old mtime. (2) ClipMeta's own `File.Replace` on a tag write bumped `LastWriteTimeUtc` to *now*,
making a just-tagged clip a "just captured" false-positive live target on the next resolution.
**Cause:** `RecentWriteSignal` v1 keyed on `LastWriteTimeUtc` (the file write time). On Windows,
copy-into-library preserves source mtime; `File.Replace` always updates it. Both are OS
behaviors that cannot be changed without imposing side-effects.
**Fix:** Switch the freshness key to NTFS creation time (`CreationTimeUtc`), copy-into-library
stamps a new creation time (the clip is genuinely new to this machine), and `File.Replace` does
NOT update creation time. Add two corroborating filters: an index-baseline check (a clip absent
from `.clipmeta-index` is treated as newly arrived) and a self-action ledger (paths ClipMeta
itself just wrote are excluded from the live-target candidates, preventing self-write false positives).
**Lesson:** "Recently written" in the gaming-mode sense means "newly arrived on this machine,"
not "file bytes recently changed." NTFS creation time is the right proxy for "new arrival,"
because copy-into-library resets it while ClipMeta's own rewrites do not.

## 2026-06-26, Background drain pump discarded its `DrainReport`; user-facing drain saw an empty queue
**Symptom:** A dogfood clip tagged while playing drained correctly (the tag landed on disk), but
`library_queue_status` and `library_flush_queue` reported `written: []` as if nothing had been
queued, even on the very next call after the tag drained. Silent success, misleading feedback.
**Cause:** `QueueDrainPump` called an internal drain helper that returned a `DrainReport` and
then discarded it. The foreground tools (`library_queue_status` / `library_flush_queue`) each
read the queue state fresh from disk, after the pump had already drained it to empty, so they
found nothing to report. The tag landed, but the report of what landed was gone.
**Fix:** Introduce `DrainJournal` (a thread-safe, bounded ring of recent `DrainReport`s keyed
by clip path). The pump writes to the journal on every drain; foreground tools read the journal
and surface its entries in `autoFlushed`. A journal entry is removed once surfaced so it fires
exactly once. `library_watching` also drains opportunistically and writes to the same journal
so all drain paths share one report channel.
**Lesson:** A background writer that silently succeeds is only half done, it must also leave a
report somewhere the foreground can find it. "Wire a silent background writer to a report-once
journal the foreground surfaces" is now the pattern for every drain path in this codebase.

## 2026-06-26, Gaming mode: a freshly-`Touch()`ed test clip is a "recent write", which silently changed access-time tests
**Symptom:** Adding `RecentWriteSignal` (gaming mode, resolve a clip just saved to disk when no player
is open) flipped several long-standing watching tests: candidates that asserted `Source == "access_time"`
became `"recent_write"`, and `Resolve_NoPlayerNoLock_AnyLiveTargetIsFalse` (plus the CLI's
`Run_NothingLive_PrintsRecencyCaution`) inverted to a live target.
**Cause:** Every test helper that creates a clip (`File.WriteAllBytes` / `Touch`) stamps `LastWriteTimeUtc`
= *now*, so under gaming mode each one is a "just saved" clip within the freshness window. Policy A makes
a *single* fresh write a high-confidence live target, exactly the case those tests assumed was "nothing
live."
**Fix:** Tests that pin the **access-time fallback** (or "nothing live") now back-date the write time
(`File.SetLastWriteTimeUtc(path, UtcNow.AddDays(-1))`, via a `TouchStale` helper) so the file is a pure
recency candidate again; gaming-mode behavior gets its own tests with fresh writes.
**Lesson:** When a new signal keys off a file timestamp that test fixtures set implicitly, the fixtures
become part of the signal's input. Make the timestamp **explicit** in any test whose outcome depends on
it, don't let "freshly created" silently mean "freshly captured." (Spec:
`docs/superpowers/specs/2026-06-26-gaming-mode-recent-write-design.md`. Policy decision: a single fresh
write with no player is auto-taggable, owner, 2026-06-26.)

## 2026-06-26, `dry_run` previewed the UNCHANGED file, not the predicted result
**Symptom:** A dogfood run saw `clip_set_fields` with `dry_run:true` "preview a merged result" that the
real write (which replaces) didn't perform. The model concluded dry-run and the real write disagreed.
**Cause:** `WriteTools.ExecuteWrite` set `mutation.DryRun=true`, the writer returned without touching the
file, and the handler then **read the unchanged file back** and reported *that*. So `dry_run` always
showed the file's *current* fields, never the *predicted* post-write fields, and with a field that
already had data, "current" looked like a merge.
**Fix:** New pure `MetadataPreview.Predict(current, mutation)` computes the predicted curated fields,
**reusing the writer's `Normalizer`** (exposed `NormalizeFieldValue`) so the preview cannot drift from
an actual write. `ExecuteWrite` serves the dry-run branch from it. A gold test pins it: `dry_run` fields
must equal a real write's read-back, for set/append/delete.
**Lesson:** A dry-run must compute the *predicted* state, never echo the unmodified input. When a
preview and the real operation share a normalization/merge rule, have them call the *same* code, a
re-implemented preview is exactly how preview-vs-actual drift (the original bug) creeps back. (Spec:
`docs/superpowers/specs/2026-06-26-pass4-dogfood-followups-design.md`.)
**Related:** the initial source-open in `Mp4Writer` now uses the same `RetryOnTransientLock` as the final
swap, a player's lingering post-close handle (or the indexer/AV) no longer fails an otherwise-good write.

## 2026-06-26, Watched-clip tag bound to the WRONG clip (poll-at-call-time race)
**Symptom:** In a watch-and-tag run, a spoken tag landed on the *next* clip: the user watched clip
N, dictated, advanced to N+1, and the tag bound to N+1. Silent and intermittent (1 of 5 in a live
run, on the shortest clip). VLC's title detection was flawless every poll, so it was NOT a
detection bug.
**Cause:** `library_watching` resolved "which clip" by snapshotting open player windows **at the
moment the tool executed**, a turn after dictation. `WatchContext.Build` took a single "now"
snapshot and `WatchingResolver.Resolve` had no history, so it could not tell a clip that *just
started* from one that had been playing a while. If the user advanced before the (late) poll, the
poll read N+1.
**Fix:** A continuous read-only `ReviewWatcher` thread records player-title **segments** (title +
start/end) over time. `ReviewBindingResolver` applies the rule "if the open clip *just started*
(< ~2 s), the user already advanced, bind the PREVIOUS stable clip," and
`WatchingResolver.ResolveReview` resolves the chosen title through the existing pipeline. Binding
correctness now depends on WHEN each title played, not on when the tool was called.
**Lesson:** A stateless "what's open now?" tool can never capture dictation-time state, the
earliest it learns of the dictation is the (late) call. For time-sensitive identification, record a
timestamped history and look *back* into it; don't re-snapshot at call time. (Spec:
`docs/superpowers/specs/2026-06-26-review-mode-watcher-design.md`.)
**Related gotcha, the not-locked-guard exception:** the resolver normally demotes an unlocked
bare-name hit ("may be a same-named file elsewhere"). But a review-mode *corrected* bind is
legitimately unlocked, the player advanced away from it, and the watcher saw that exact title
play for seconds. `ResolveReview` keeps a single history-confirmed match high-confidence rather than
demoting it. Do NOT "fix" the collision guard to also demote corrected binds; that would re-break this.

## 2026-06-25, Player-title detection: extract-then-exact-match is brittle (MPC-HC intermittency)
Dogfooding (2026-06-25) showed MPC-HC's player-title detection firing intermittently (`✓✗✗✓✓`)
while VLC was reliable. **Root cause:** the old bare-name path extracted an `.mp4` token from the
title with a regex (`PlayerTitleParser.BareNameRegex`) that excludes `:` but **not** the ` - `
MPC-HC inserts after a playback-position prefix. So `"00:01:23 - clip.mp4"` extracted as
`"23 - clip.mp4"`, which equalled no `ByFileName` key, and resolution silently went quiet exactly
when MPC-HC showed the time. **Fix:** invert it, `LibraryTitleMatcher.FindBestMatch` asks which
KNOWN library basename appears in the title (boundary-checked, longest-match, case-insensitive
containment), so any title-format quirk (timecode/OSD/paused/custom) is immune. Full-path titles
still resolve exactly first (folder disambiguation); a full path NOT in the library is still a
wrong-directory case and does NOT fall back to containment. **Lesson:** when matching external,
format-unstable text (a window title) against a known set, match the title *against the known set*,
don't extract-a-token-and-hope-it-equals-a-key.

## 2026-06-25, Access-time is advisory only; ClipMeta's own IO pollutes it, and Windows often disables it
The access-time fallback ranks clips by `LastAccessTimeUtc`, but that signal is doubly unreliable:
(1) **self-pollution**, ClipMeta's own reads (`library_list`, `clip_get_metadata`, `library_vocab`)
AND its flush **writes** bump last-access, freshening already-handled clips to `secondsSinceAccess≈0`
(a just-flushed clip can float to the top looking "live"); (2) **OS disablement**, Windows commonly
ships with last-access updates off (`NtfsDisableLastAccessUpdate`), so the signal may be uniformly
stale. **Decision: do not fight it** (no snapshot/restore of atime, fragile and useless when the OS
disables it). Instead the lock probe outranks atime, a high-confidence player hit suppresses stale
access-time rows entirely, and `WatchingResult.AnyLiveTarget` tells callers when NOTHING is actually
open/locked so they refuse to auto-tag a recency guess. **Lesson:** treat last-access time as a weak
hint, never as proof a file is being watched.

## 2026-06-25, Re-tagging clobbered notes; the queue merge only protected entries that COEXISTED
Dogfooding showed a second narration of the same clip overwriting the first note. Two bugs in one:
`library_queue_tag` mapped EVERY field to `SetFields` (last-wins), and the queue's merge only runs
while two entries coexist IN the queue, once the first note drained to disk, a later set clobbered
the on-disk value. Fix is per-field semantics: notes/tags/players route to `AppendFields`
(`ClipMetaSchema.QueueAppendFields`); the write engine's append fold reads the CURRENT on-disk value
and merges, so it survives the drain-then-retag path too. **Notes append as PROSE, not a pipe list**, 
`Normalizer.AppendValue` (and `TagQueue.Merge`) join `ProseFields` with a space, case preserved, no
dedup; running notes through `AppendToPipeList` would lowercase and pipe-mangle prose. **Lesson:** an
in-memory merge that only fires while two items coexist is not durable, fold the merge into the
operation that touches persistent state (here, the disk write).

## 2026-06-25, The background drain pump makes WriteGate load-bearing, not just insurance
The zero-touch flush pump (`QueueDrainPump`) is a genuine SECOND writer thread inside the MCP server
(the session loop was single-threaded before). Every pump drain MUST run inside the same
process-wide `WriteGate` single-flight as the direct write tools, or two rewrites could race at
`File.Replace`. The pump is decoupled from the gate via an injected `runExclusive` seam (Core has no
dependency on the MCP `WriteGate`); `Program.Serve` supplies the WriteGate-backed wrapper and
disposes the pump in a `finally` after the session loop. Note: a player RELEASING a file handle is
NOT a FileSystemWatcher event and process-exit hooks are racy, the pump POLLS the small queued set's
lock state while non-empty, idle on an event otherwise. Durability stays the queue's job: a host kill
before a lock clears leaves the tag in `.clipmeta-queue` for the next session. **Lesson:** when you
add a background worker that writes, the write-serialization primitive that was "future insurance"
becomes a correctness requirement, wire it through, don't skip it because the gate "isn't needed yet."

## 2026-06-25, Provenance stamp: treat `tagged_by` like `schema` (internal) to avoid read-surface ripple
`Mp4Writer` now stamps `tagged_by: Peckworks ClipMeta` on every write that stores a user field. Making
it a normal user field would have rippled across every test/surface that enumerates user fields. Fix:
`ClipMetaSchema.IsInternal` covers `tagged_by` too, so all curated surfaces (get_metadata, stats,
vocab, export, index, copy, everything through `ClipMetaReader.GetUserFields`) auto-exclude it; it's
still written to the file and shown in the raw `--list`/tree. Two gotchas: the stamp sits in
`SetFields`, so `RemoveOrphanedSchemaStamp`'s "does this write store a user field?" check must exclude
BOTH `schema` and `tagged_by` (else provenance keeps the schema alive), and a delete-last-field write
must sweep `tagged_by` alongside `schema`. **Lesson:** a field written on every mutation should be
modeled on the existing internal stamp (`schema`), not as user data.

## 2026-06-21, `Mp4Writer.WriteMetadata`'s full throw surface (a never-crash wrapper must catch all)
Building the deferred-tag queue's `Drain` (which must never crash on one bad clip), the first catch
set omitted `UnsupportedFormatException`, so a single fragmented/unsupported clip in the queue threw
straight out of the drain and skipped the persist, stranding already-written entries. The **complete**
exception surface of `Mp4Writer.WriteMetadata` is: `IOException`, `InvalidOperationException`,
`InvalidDataException`, `UnsupportedFormatException` (fragmented `moof` / unsupported format gates),
plus `ArgumentException` / `UnauthorizedAccessException` from the BCL + normalizer. Any "never-throw"
caller of the writer must catch **every** one of these. (Same class of miss as pass-1.5's `LockProbe`
dropping `NotSupportedException`, when a contract says never-throw, enumerate every documented throw.)

## 2026-06-21, Mutation keys are domain-qualified; strip the prefix for display
`MetadataMutation.SetFields`/`AppendFields`/`DeleteFields` are keyed by the **qualified atom name**
`ClipMetaSchema.AtomName(field)` = `com.peckworkslab.clipmeta:<field>` (see `WriteTools`). Building a
mutation with a **bare** field name (`"tags"`) writes the WRONG atom. Any code that constructs a
mutation directly (Core tests, the queue) must qualify keys via `AtomName`. Conversely, anything that
displays the keys (e.g. `library_queue_status` `changedFields`) must **strip** the `Domain + ":"`
prefix back to the user-facing name. Read back the user-facing name with `ClipMetaReader.GetUserFields`.

## 2026-06-21, `System.Text.Json` round-trips get-only record collection props by ctor-param name
The queue persists `QueuedMutation` (positional record with get-only `IReadOnlyDictionary<,>` /
`IReadOnlyList<>` properties) with **no custom converter**: STJ binds records by matching JSON
property names to **constructor parameters** (case-insensitive), so get-only collection props
deserialize correctly as long as the property names match the JSON. This is why the queue could reuse
plain records instead of mutable DTOs. (The `Save→Load` round-trip test is the proof, keep it: it's
the canary if a future record shape change breaks binding.)

## 2026-06-21, Deferred-tag queue: a playing clip is locked against File.Replace
A clip a media player is showing cannot be written (File.Replace needs FILE_SHARE_DELETE, which
players don't grant). Pass-2 queues the tag (.clipmeta-queue, JSON, library root) and drains it
when the lock clears, on the next library_watching/library_queue_tag call, or library_flush_queue
/ scribe --flush-queue for the last clip. Drains share the MCP WriteGate so they never race a
direct write. Per-player lock-release on next/stop/close is still a dogfooding TODO, record
observed behavior here when measured.

### 2026-06-21, Watched-clip resolution, pass 1.5 (wrong-directory honesty)

- **VLC bare-name matches can collide.** VLC reports only the file name, so a library `clip001.mp4`
  matches even when you're watching a *different* `clip001.mp4` elsewhere. Guard: a bare-name match
  is `high` only when the library file is **locked** (`LockProbe.IsInUse`); otherwise it is demoted
  to `low` with a confirm note. Full-path (MPC) matches are exact and stay `high` regardless of lock.
- **Pause/stop releases the lock, accepted trade-off.** If a player releases the file handle while
  paused, a *correct* bare-name match reads not-locked and is demoted to "confirm" (friction, not a
  wrong tag). MPC (full path) is unaffected. Revisit the trust policy after dogfooding tells us how
  MPC/VLC behave with the lock on stop vs. next vs. close.
- **Never lock-probe an offline/placeholder file.** Opening a Dropbox/OneDrive online-only file
  hydrates (downloads) it. `LockProbe` checks `FileAttributes.Offline` and reports not-locked WITHOUT
  opening, so a bare-name match to an un-downloaded library file stays `low` (correct: it isn't the
  file being played).
- **A player open with no readable filename is NOT a wrong-directory signal.** Only a title that
  names an `.mp4` absent from the library warns; a metadata-title/idle player stays quiet.

## 2026-06-21, Watched-clip resolution

- **Writing to a clip a player still holds open fails.** `File.Replace` deletes-and-swaps the
  target; that throws a sharing violation unless every open handle used `FILE_SHARE_DELETE`, which
  MPC-HC/VLC do not. A clip is writable only after the player advances ("next") or closes. The
  watched-clip resolver surfaces `inUse` so callers warn before attempting a write. **TODO when
  dogfooding:** confirm per player whether the lock releases on *stop*, on *next*, or only on
  *close*, this sets the deferred-tag queue's drain timing (pass 2).
- **ClipMeta's own reads bump last-access time.** That pollutes the access-time resolution signal.
  Fixed at the single parse choke point (`Mp4Parser.ParseFile`) with `AccessTimeGuard`
  (capture-then-restore, best-effort, restoring is itself a write that can lose to a lock).
- **Window titles only *select*, never *construct*.** A resolver candidate must come from a clip
  enumerated under the library root; a title naming a path outside the library matches nothing and
  is dropped. This is the containment guarantee, do not "resolve" a title path by trusting it.
- **Player title formats:** MPC-HC emits the full path; VLC emits `name.mp4 - VLC media player`
  (bare name). A VLC title with no `.mp4` is an embedded metadata title, expected, yields no player
  candidate. The recognized-player list lives in `MediaPlayers.KnownProcessNames` (extensible).

### 2026-06-15, Index write truncated the existing index on open (fixed: temp-then-atomic-swap)
- **Symptom (latent):** `ClipMetaIndex.WriteToFile` opened the destination with
  `new StreamWriter(filePath, append: false, …)`, which truncates the target the instant it
  opens. A write interrupted between that open and the final flush, crash, power loss,
  disk-full, or an exception while serializing, left the user's previously-built
  `.clipmeta-index` truncated or empty. Provable in a test: feed an `IndexData` whose entry
  list throws mid-enumeration and the on-disk index is left corrupt at the failure point.
- **Cause:** writing in place. Every *other* mutation in the codebase already goes temp →
  verify → atomic swap; the index writer was the one place that didn't.
- **Fix:** serialize to `"{filePath}.{guid}.tmp"`, then `File.Move(temp, filePath, overwrite: true)`
  (same-volume atomic `MoveFileEx(REPLACE_EXISTING)`), wrapped in the write engine's
  `Mp4Writer.RetryOnTransientLock` for the AV/indexer race. On any failure the temp is deleted
  and the original index is untouched (it is never opened for writing until the swap). Encoding
  unchanged (UTF-8) so the format round-trips exactly.
- **Lesson:** any "overwrite a file the user cares about" path must be temp-then-atomic-swap, not
  write-in-place, truncation happens at *open*, long before the bytes you meant to write.

### 2026-06-15, Foreign-atom test assumed a single `ilst`; a real clip has two `meta` boxes (fixed test)
- **Symptom:** Expanding the pristine corpus made `Write_ForeignAtoms_Preserved` fail on exactly
  one clip (`2022-02-01 21.50.02.mp4`): "Foreign atom count changed. Before: 2, After: 0", as if
  a clipmeta write had *deleted* two pre-existing metadata atoms.
- **Investigation (NOT a writer bug):** dumping the post-write tree proved the writer was correct
  and safe. This clip carries **two metadata containers**: a **moov-level `meta`** (a *sibling* of
  `udta`, `hdlr` type **`mdta`**, with a **`keys`** box and a key-indexed `ilst`, the Apple/
  QuickTime metadata-keys format used for make/model/GPS-style data), plus `udta/©xyz`. On write,
  clipmeta correctly leaves that foreign `meta` **byte-for-byte untouched** and creates its OWN
  iTunes-style `udta→meta→hdlr(mdir)→ilst` for its `----` atoms. The file now has **two `ilst`
  boxes**. mdat + all chunk offsets were also proven identical (media-integrity test passed).
- **Cause (in the test):** `Write_ForeignAtoms_Preserved` did `FindNode(root, type=="ilst")`,
  which returns the **first** `ilst`. Before the write that's the foreign one (2 atoms); after,
  the writer's brand-new (foreign-free) `ilst` sorts first → 0 foreign → false "lost atoms".
- **Fix (test only):** count foreign atoms across **every** `ilst` (`FindAllNodes`), not just the
  first. No production change, the writer's "don't touch a metadata format you don't own" behavior
  is exactly right.
- **Lesson:** an MP4 may hold more than one `meta`/`ilst` container, in different formats
  (iTunes `mdir` vs Apple `mdta`/keys), at different levels (movie `udta` vs moov-level `meta`).
  Test helpers that assume "the one ilst" are wrong on real-world clips. (Same class of harness
  assumption as the ZC112 chunk-window overrun below, diverse real clips surface what same-source
  fixtures never do. **`mdta`/keys metadata is a documented format the writer preserves but does
  not edit.**)

### 2026-06-12, File.Replace races antivirus on freshly-written files (fixed: bounded retry)
- **Symptom:** The write suite intermittently failed (~1 test, ~30% of runs) with
  `IOException: The process cannot access the file because it is being used by another process`
  out of `Mp4Writer`'s final `File.Replace`. Diagnosed by looping the suite under a TRX logger
  until it caught a red, it was always a *sharing violation*, never an assertion.
- **Cause:** on Windows, antivirus / the Search indexer grabs a just-written file for a second
  or two. The writer creates the temp, releases its own deny-writers handle, then calls
  `File.Replace`, and if AV is mid-scan on the temp or the destination, ReplaceFile fails. The
  writer correctly *failed safe* (refused, original untouched), but a clean write shouldn't lose
  to a transient lock, and a real user tagging a clip seconds after a recorder made it could
  hit the same thing.
- **Fix:** `Mp4Writer.RetryOnTransientLock` wraps ONLY the final swap, retrying up to 5× with a
  100 ms × attempt backoff on `IOException`/`UnauthorizedAccessException`. Safe by construction:
  the temp is already fully written and verified before the swap, so retrying the atomic
  operation weakens no guarantee; if every attempt fails the last exception still propagates
  (fail safe, unchanged). Non-transient exceptions are not retried. Deterministic unit tests
  drive the helper with zero delay (no real locks, no new timing-dependent flake).
- **Lesson:** an atomic file swap on Windows must tolerate transient AV/indexer locks; retry the
  *post-verification* swap, never the verification itself.

### 2026-06-12, Media-integrity scanner's fixed window overran the final chunk (fixed test)
- **Symptom:** Adding a new real clip (`ZC112.mp4`, mdat-first, 290 MB) made
  `RealClip_MultiFieldWrite_MediaByteIdentical` fail: "chunk table[1] entry 8931 points at
  different data after rewrite ... the track would play garbage", even though the mdat SHA-256
  check (which runs first) PASSED, i.e. the media was provably byte-identical.
- **Cause:** the WRITER was correct. `MediaIntegrityScanner` compares a fixed 64-byte window at
  each chunk offset; ZC112's last chunk sits 38 bytes before mdat-end, so the window spilled 26
  bytes into the following `moov` box, which legitimately changed when the test wrote metadata.
  The mismatch at "index 40" was 2 bytes past the mdat boundary, i.e. non-media.
- **Fix (test helper only):** `ClampToMdatEnd` bounds each comparison window to the end of the
  mdat containing the offset, so only real sample bytes are compared. No production code
  changed; ZC112 and the moov-first `Stargaze.mp4` both pass.
- **Lesson:** chunk *sample* data is bounded by its mdat; a verifier reading a fixed span past a
  boundary-hugging final chunk measures the next box, not the media. Diverse real clips (here a
  chunk flush against mdat-end) surface harness assumptions that synthetic fixtures and
  same-source clips never hit, exactly why the pristine set should span multiple creators.

### 2026-06-12, Clearing metadata left an ~80-byte schema/container husk (fixed)
- **Symptom:** An agent hammering the MCP write tools noticed a clip that was tagged then fully
  cleared came back ~80 bytes LARGER than pristine, twice (Stargaze 3,746,496 → 3,746,576;
  ZC112 +80). The file *looked* bare (reads filter the internal field) but still carried a
  `com.peckworkslab.clipmeta:schema=1` atom inside a live `udta→meta→hdlr→ilst` chain.
- **Cause:** two gaps. (1) The schema stamp is only ADDED on value-storing writes, but nothing
  ever REMOVED it when the last user field was deleted, `clip_clear_fields` (a delete-only
  mutation, not clear-all) left it orphaned. (2) Even clear-all, which did sweep the schema
  atom, left the now-empty `ilst`/`meta`/`udta` boxes behind.
- **Fix (`Mp4Writer`):** `RemoveOrphanedSchemaStamp` adds the schema key to `DeleteFields` when
  a mutation removes the last user field; `DetermineEmptyChainRemoval` then drops the emptied
  container chain (innermost-out: ilst → meta-if-only-hdlr-left → udta-if-only-meta-left). A
  write→clear round-trip is now byte-identical to pristine (new `WriteThenClearAll_ReturnsFile
  ToBytePristine` test proves it on a moov-first fixture, offsets and all).
- **Conservatism that matters:** the chain is dropped only when nothing else needs it. A
  surviving clipmeta field, OR any foreign atom (iTunes `©nam`, another tool's `----`), keeps
  the whole chain, tested by `ClearAll_WithForeignAtomPresent_KeepsChainAndForeignAtom`. The
  moov-size prediction subtracts the exact removed-box size, and the existing hard moov-size
  assert in `WriteMoov` is the backstop if that prediction is ever wrong.
- **Lesson:** "remove the data" and "remove the now-empty containers that held it" are two
  jobs; a rewriter that does only the first leaves growing cruft and makes "did we ever touch
  this file" unanswerable.

### 2026-06-12, Packed .mcpb install silently fails on the Microsoft Store build (workaround: unpacked)
- **Symptom:** Settings → Extensions → Advanced settings → **Install Extension…** → pick
  `clipmeta.mcpb` → the file dialog closes and *nothing* happens. No toast, no error, no card.
- **Cause (from `main.log` in the app's package container):** the packed file is routed into
  `installDxtUnpacked`, which expects a *folder* containing `manifest.json` and fails with
  `No manifest.json found in extension folder`, logged but never surfaced in the UI. App bug
  in Claude Desktop **Microsoft Store/MSIX build** (observed on `Claude_1.12603.1.0`); the
  bundle itself was verified well-formed (manifest at zip root, forward-slash entries).
- **Workaround that passed the E2E gate:** extract the bundle and use **Install Unpacked
  Extension** on the folder. `pack-mcpb.ps1` now keeps that folder as `dist/clipmeta-unpacked/`.
  Once installed: binary spawn, stdio handshake (2025-11-25 echoed), `tools/list`, and a real
  `clip_get_metadata` round-trip all worked first try, **R2 retired** 2026-06-12.
- **Where the Store build hides its logs/config:** NOT `%APPDATA%\Claude`, everything lives in
  `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\` (`logs\main.log`,
  `logs\mcp-server-<Name>.log`, `claude_desktop_config.json`). Check `main.log` first for any
  silent extension failure.
- **Lesson:** a silent UI no-op almost always has a logged error somewhere, find the app's log
  directory before re-trying gestures. And ship the unpacked layout alongside the bundle: it is
  the universal fallback when a host's packed-install path is broken.

### 2026-06-12, There is no drag-and-drop install for .mcpb bundles (fixed in docs)
- **Symptom:** The design spec, README, and pack-script output all said to install the bundle by
  "dragging onto Claude Desktop → Settings → Extensions." The user tried it on the real app:
  nothing happens, there is no drop target on the Extensions page.
- **Reality (per Anthropic's help center, verified 2026-06-12):** local bundles install via
  Settings → Extensions → **Advanced settings** → Extension Developer → **Install Extension…**
  → file picker. The folder-picker for `user_config` (our clips-library sandbox) appears during
  the install prompts.
- **Fix:** README, spec §"User install story", plan phase 5, and `pack-mcpb.ps1` messages all
  corrected to the button flow.
- **Lesson:** UI install-flow claims are field claims, not spec claims, they must be verified
  on the actual app version before they reach user-facing docs. We verified the *manifest
  schema* against live docs but never the *install gesture*. (Phase-4 `--install` exists
  precisely because the bundle flow could change under us; same reasoning applies to docs.)
- **Symptom:** The MCP library sandbox checked `resolvedPath.StartsWith(root)` after
  `Path.GetFullPath`, and an adversarial probe **escaped it**: a directory junction inside the
  library pointing outside it passes the lexical check while `FileStream` happily follows the
  reparse point to the outside target. `mklink /J` needs no admin rights.
- **Fix:** Containment is now checked on the OS-canonical path, every junction/symlink
  component resolved via `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)`, walking
  root-to-leaf, and the configured root is canonicalized the same way (a junction *root* is
  legitimate). Cloud-placeholder files (Dropbox/OneDrive) are reparse points but **not** links:
  `ResolveLinkTarget` returns null for them, so they pass through, a blanket reparse-point ban
  would have broken online-only clips.
- **Related fixes from the same review:** NTFS alternate-data-stream syntax
  (`real.mp4:payload.mp4`, the *stream* name satisfies a `.mp4` suffix check) is now refused;
  and `Path.TrimEndingDirectorySeparator` keeps the separator on a drive root, so naive
  `root + '\'` built `"C:\\\\"` and refused **every** file on a whole-drive library, use
  `Path.EndsInDirectorySeparator` before appending.
- **Lesson:** `GetFullPath` resolves `..` but not reparse points; the filesystem resolves both.
  Any check done on the lexical path can disagree with what the OS actually opens.

### 2026-06-11, `Compress-Archive` zip entry separators are PowerShell-version-dependent
- **Symptom (potential):** Under Windows PowerShell 5.1, `Compress-Archive` writes zip entry
  names with backslashes (`server\clipmetamcp.exe`), violating the ZIP spec; spec-strict
  extractors then can't find the `.mcpb` bundle's `server/clipmetamcp.exe` entry point, 
  installed-but-never-spawns. pwsh 7 on this machine happened to emit forward slashes.
- **Fix:** `pack-mcpb.ps1` uses `[System.IO.Compression.ZipFile]::CreateFromDirectory`, which
  always writes forward slashes regardless of which PowerShell runs the script.
- **Lesson:** Artifacts that must be byte-deterministic shouldn't depend on which shell built
  them; go to the BCL API directly.

### 2026-06-11, Garbage bytes in an .mp4 parse "successfully" to an empty tree
- **Symptom:** An MCP test assumed `Mp4Parser.ParseFile` throws on a garbage file; it doesn't, 
  the parser's deliberate leniency (clamp oversized boxes, stop at damage; see the mdat entry
  below) means tiny garbage files parse to a tree with no metadata, no exception.
- **Lesson:** "Corrupt file" tests against the read path must assert *empty result + session
  survives*, not an exception. Only the **write** path treats unaccounted bytes as fatal.

### 2026-06-10, Parse and copy used separate file opens (fixed)
- **Symptom (potential):** The writer parsed the source, closed it, then re-opened it to copy
  bytes. A process writing to the file in between (a capture tool still recording the clip
  being tagged) would make the copied bytes disagree with the parsed chunk offsets, torn output.
- **Fix:** One `FileShare.Read` (deny-writers) handle held across parse + copy via the new
  `Mp4Parser.Parse(FileStream)` overload. A live recorder now causes a clean up-front refusal
  ("another program has it open for writing") instead. The handle must be released *before*
  `File.Replace`, ReplaceFile needs write/delete access the held handle would block.
- **Lesson:** Read-then-act on a file path is a TOCTOU race; hold one handle across both steps.

### 2026-06-10, CLI swallowed flags as values; some errors were stack traces (fixed)
- **Symptoms:** `--set notes --backup` stored the literal text "--backup" as notes (while also
  enabling backup mode, flag detection scans the arg list independently). `--set tags` at the
  end of the line was silently ignored. `--set rating "five stars"` crashed with a raw .NET
  stack trace (`ArgumentException` from Normalizer was uncaught in Program). `--SET` (wrong
  case) was silently ignored while other flags matched case-insensitively. The fixed-name
  `clip.mp4.tmp` temp file would overwrite a real user file of that name. Appending to a
  non-text atom spliced its display placeholder ("[JPEG image, …]") into the file as data.
- **Fixes:** `BuildMutation` validates positional args (missing → error; an exact known-flag
  match where a value belongs → error; merely dashy values still accepted). `ArgumentException`
  / `InvalidOperationException` are caught → friendly message, exit 1. Temp files are
  `<name>.<guid>.tmp`. Appends verify the existing value is quoted text before merging.
- **Lesson:** Every "take the next arg" parse needs a missing/looks-like-a-flag check, and
  every exception type a CLI's core can throw needs a catch that turns it into an error line.

### 2026-06-10, Lenient parser + trusting writer = silent mdat deletion (CRITICAL, fixed)
- **Symptom:** Writing metadata into a moov-first file that had one unparseable box between
  moov and mdat produced a file with **no mdat at all**, the entire video silently deleted,
  exit code 0, verification passed.
- **Cause:** `Mp4Parser.ParseBoxes` deliberately `break`s (not throws) at a box it can't read,
  so everything after the damage was missing from the tree. `Mp4Writer` emits only the boxes
  in the tree, and the old `VerifyWrite` only checked "moov exists + set fields read back", 
  all true even with the video gone.
- **Fix:** The parser stays lenient (a damaged file should still be *viewable*), but the writer
  is now strict: `VerifyParseAccountsForWholeFile` refuses any write where the parsed top-level
  boxes don't tile the file byte-for-byte, or where any box was size-clamped (truncated file).
  Post-write checks now also assert temp length == original + delta and mdat count unchanged.
- **Lesson:** A writer that rebuilds a file from a parse tree inherits every silent omission of
  the parser. Read-lenient / write-strict must be an explicit, tested boundary.

### 2026-06-10, Delta-based offset patching had no cross-check (fixed preemptively)
- **Symptom (potential):** stco/co64 entries are shifted by a *predicted* moov-size delta
  computed independently of the bytes actually written. Any divergence (e.g. an exotic box
  layout the size calculation mis-accounts) would corrupt every chunk offset silently.
- **Fix:** `WriteMoov` now hard-fails if the rebuilt moov's actual size differs from the
  prediction, before anything reaches the original file.
- **Lesson:** When value A (offset delta) is derived from a prediction of value B (moov size),
  assert prediction == reality at the moment B becomes known. One `if` turns silent corruption
  into a safe abort.

### 2026-06-10, `--clear-all` re-added the schema atom it had just removed (fixed)
- **Symptom:** After `--clear-all`, `--list` still showed `schema 1`.
- **Cause:** `WriteMetadata` stamped `schema=1` into `SetFields` unconditionally, including on
  clear-all and delete-only mutations.
- **Fix:** Stamp only when the mutation actually sets or appends values.
- **Lesson:** "Do X on every write" rules need a definition of *write*; removal-only operations
  usually shouldn't create data.

### 2026-06-10, Tests verified metadata, never media (fixed)
- **Symptom:** All 286 tests passed while the mdat-deletion bug above existed. The test named
  for "stco/co64 adjustment" only asserted the file was still *parseable*, true even with
  every chunk offset corrupted. No test ever hashed mdat or checked a single offset value.
  (This file previously claimed a both-tracks stco integration test existed; it did not.)
- **Fix:** `Mp4WriteIntegrityTests` + `MediaIntegrityScanner` (an independent scanner sharing
  no code with clipmeta.core): SHA-256 of every mdat payload, plus a byte-compare at every
  stco/co64 entry (old offset in old file vs new offset in new file), on synthetic moov-first
  fixtures with two tracks and patterned chunks AND on every real pristine clip.
- **Lesson:** For a file *rewriter*, round-trip tests must assert the payload bytes, not just
  the structure. Also: the real test clips that happen to be on hand are all mdat-first, so the
  offset-adjustment code path is exercised ONLY by the synthetic moov-first fixtures. Layout is
  a per-file fact, not a property of the source tool, never assume all clips share one layout;
  both paths must stay covered regardless of what the real-clip sample looks like.

### 2026-06-09, Build fails NU1100 on a machine with no NuGet source
- **Symptom:** `dotnet build`/`restore` fails: `NU1100: Unable to resolve 'MSTest'`.
- **Cause:** Not the project. `dotnet nuget list source` returned "No sources found", the machine had no nuget.org source registered.
- **Fix:** `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`, then restore.
- **Lesson:** When restore fails for a package on a fresh machine, check `dotnet nuget list source` before editing csproj files. Production projects have zero NuGet deps; only the test projects pull MSTest.

---

## MP4 format hazards (foundational, from the write-engine design spec)

These are the things that **corrupt files silently** if missed. Each should have a corresponding test. Full detail in `docs/superpowers/specs/2026-05-21-clipmeta-core-write-engine-design.md` §4 and §10.

| # | Risk | Mitigation |
|---|------|------------|
| 1 | Fragmented MP4 (`moof` boxes, produced by some streaming/live-capture recorders) | Detect on parse; refuse write with a clear `UnsupportedFormatException`. |
| 2 | Only one `stco`/`co64` adjusted, others missed | Walk **ALL** `trak → stbl → stco/co64`. A stereo clip has video + audio tables; missing one desyncs that track. `Mp4WriteIntegrityTests` proves both tables on a two-track moov-first fixture byte-for-byte. |
| 3 | `mean`/`name` FullBox 4-byte version+flags prefix omitted | Both are FullBoxes. Omitting the prefix shifts all following bytes → atom reads back as garbage. Unit test verifies byte structure. |
| 4 | `hdlr` missing when creating `meta` from scratch | QuickTime/Final Cut **reject** a `meta` box with no `hdlr` (handler_type `mdir`). Scenario-3 test uses a file with no existing udta/meta/ilst. |
| 5 | Foreign `ilst` atoms corrupted on rewrite | Copy all non-`com.peckworkslab.clipmeta` atoms (iTunes `©nam` etc., third-party `----`) byte-for-byte, in order, before appending ours. Test verifies `©nam` unchanged. |
| 6 | `stco` adjusted when `mdat` precedes `moov` | Only adjust offsets when `mdat` starts **after** the end of `moov`. mdat-first files must be left unchanged. |
| 7 | `co64` / `stco` value exceeds 32-bit boundary undetected | For 32-bit `stco`, fail if `offset + delta > UInt32.MaxValue`; warn under 10% headroom (file approaching 4 GB should already use `co64`). |
| 8 | Temp file left behind on exception | On any failure, delete the temp file and rethrow with context; the source is never opened for writing. Test asserts no temp file remains after a forced exception. |

### Other write-engine invariants
- **The Golden Rule:** the source file is never opened for writing. All mutations → temp file → re-parse to verify → `File.Replace` (atomic same-filesystem swap).
- **Big-endian everywhere.** Every multi-byte MP4 integer is big-endian; always use `BigEndianReader`/`BigEndianWriter`.
- **The `©` prefix is byte `0xA9`.** Read FourCCs with `Encoding.Latin1`, not ASCII, or it mangles. Compare against `"©nam"` etc.
- **`free` padding:** on first clipmeta write, append a 512-byte `free` box after `ilst` so future re-tags don't shift `mdat` (avoids stco/co64 churn). Exceeding the padding triggers a full rewrite.
- **`--set field ""` deletes** the atom, empty and absent are not distinguished.
