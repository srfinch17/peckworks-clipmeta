# Resolver & Queue Trust Hardening, Design Spec

**Date:** 2026-06-26
**Status:** Approved-for-build (owner, 2026-06-26). Scope, novelty substrate, and roster-guard strength all resolved (see §0).
**Pass:** pass-5, from the 2026-06-26 live-tagging dogfood (41-clip test library). Targets P0 + P1; P2 deferred.

---

## 0. Decisions resolved up front

This spec was shaped against the actual code before any design (the brief's diagnoses were hypotheses written without source access). Three reconciliation findings and three owner decisions frame the whole round:

**Reconciliation, the brief got these wrong; they are NOT built here:**
- **Provenance is not a no-op.** `Mp4Writer.cs:126` stamps `tagged_by: Peckworks ClipMeta` on every user-field write, proven by `ProvenanceStampTests` (including the queue-drain case). It is *deliberately hidden* from `clip_get_metadata`/`library_export` via `ClipMetaSchema.IsInternal`, `customFields: []` is expected. Visible in the raw `clipmetaview` tree. No change.
- **Index staleness needing `rebuild:true`** is documented by-design behavior, not a bug. A doc/UX nit, out of scope.
- **P0-1's root cause is NOT a reporting-scope bug.** `QueueTools.DrainJson` reports correctly from the `DrainReport`. The real cause is the silent background pump (§3).

**Owner decisions (2026-06-26):**
1. **Scope:** P0 + P1 this round; P2 (sequence-reset, mode hint, backup strategy) is a fast follow.
2. **Novelty substrate:** index baseline + NTFS creation-time + in-memory self-action ledger (§2). Not a fresh-snapshot scheme, not a FileSystemWatcher.
3. **Roster guard:** soft advisory, the write proceeds and a non-blocking flag rides back (§6). Not a hard gate.

---

## 1. Problem

The 2026-06-26 dogfood validated the core mechanism (every review-mode tag bound to the correct clip, zero mis-binds across two runs), but surfaced four real defects concentrated in two areas, the gaming-mode signal and the queue's telemetry, plus one net-new disambiguation feature:

- **P0-1, drain telemetry is blind.** Every queue drain reported `written: []` while `library_export` confirmed all 12 queued tags landed on disk. A silent success and a silent drop are indistinguishable, the worst possible state for a data-trust promise.
- **P0-2, `recent_write` keys on the wrong clock.** Gaming mode (no player open, tag the clip the game just saved) failed: a freshly *copied-in* clip looked old, and a clip ClipMeta had just *written to* looked freshest, so real saves were invisible and tool-writes were false positives that could suppress the true new clip.
- **P1-1, self-reads pollute the access-time fallback.** ClipMeta's own `library_export` / `clip_get_metadata` reads bumped access time, floating long-dead files to the top of the fallback ranking.
- **P1-2, player-name ambiguity has no guard.** A dictation token (`"miami element"`, actually a warpaint) was silently filed as two players, polluting the players vocab.

The root of P0-2 and P1-1 is one missing capability: **ClipMeta has no memory of what it itself touched, nor of what was already in the library**, so a signal keyed on raw filesystem timestamps cannot tell "the game just saved this" from "I just tagged this" from "I just read this." The fix is one shared substrate, not four independent patches.

### Out of scope (this pass)
P2-1 sequence-reset, P2-2 mode hint, P2-3 backup strategy; the v1.1 FileSystemWatcher; any index-staleness UX. All deferred to a fast follow.

---

## 2. Foundation, `SelfActionLedger` + creation-time + baseline

### 2.1 `SelfActionLedger` (Core, new)
A process-wide, thread-safe ledger of what ClipMeta touched this session.

- State: `path → (DateTimeOffset LastSelfTouchUtc, SelfTouchKind Kind)` where `SelfTouchKind ∈ { Written, Read }`. A newer touch replaces an older one for the same path; a `Written` is never downgraded to `Read` within the window (a path we wrote then read is still "self-written").
- API: `MarkWritten(path)`, `MarkRead(path)`, `WasWrittenWithin(path, window, now)`, `WasTouchedWithin(path, window, now)`. Case-insensitive path keys (`StringComparer.OrdinalIgnoreCase`, Windows semantics). Lock-guarded, the queue-drain pump thread and request threads both touch it.
- Lifetime: in-memory, instantiated once in `Program.cs`, injected into read/write/queue tool registration and into `WatchContext.Build`. Resets on server restart by design (a restart is a new session; worst case reverts to today's behavior for pre-restart paths). Entries are pruned lazily when older than the freshness window so the ledger never grows unbounded and never permanently masks a path the user legitimately re-creates later.

Who marks what:
- **Every write** marks `Written`: `WriteTools.ExecuteWrite`, the queue `Drain` path, and the pump. (The drain/pump write inside Core, so the mark is taken there or threaded via the writer call site, see §2.4.)
- **Content reads** mark `Read`: `clip_get_metadata` and `library_export` handlers. `library_list` (directory names only, no file open) does **not** mark, it is low-pollution and safe for baseline snapshots, per the dogfood note.

### 2.2 Creation-time on `LibraryClip` / `WatchContext`
`LibraryClip` gains `DateTime CreationTimeUtc`, populated in the existing single `WatchContext.EnumerateLibrary` pass from `File.GetCreationTimeUtc` (same try-block as access/write time; a file whose times can't be read is still skipped, no extra scan, no new IO pass). On NTFS the creation timestamp is set fresh when a file appears in a directory **even when a copy preserves the source mtime**, which is exactly what defeats the copy-preserves-mtime trap.

### 2.3 Known-baseline paths on `WatchContext`
`WatchContext` gains `IReadOnlySet<string> KnownBaselinePaths` (case-insensitive), populated from the persisted `.clipmeta-index` when present. A path **not** in this set is "candidate-new." If the index is absent or unreadable, the set is empty and novelty falls back to creation-time + ledger alone (still a large improvement over mtime; an absent index is the gaming user's to rebuild).

### 2.4 Wiring the ledger through the signals
Signals stay pure functions of `WatchContext`. The context carries a reference to the ledger (or the two derived predicate inputs). `RecentWriteSignal` and `AccessTimeSignal` consult it; `PlayerTitleSignal` does not. `WatchContext.Build` overloads gain an optional `SelfActionLedger?` (default null ⇒ no exclusion, preserving every existing test that builds a context without one).

---

## 3. P0-1, drain visibility (the silent pump)

### Root cause
`QueueDrainPump.DrainOnce` (`QueueDrainPump.cs:109`) captures a `DrainReport` but uses it only for the `StillQueued.Count == 0` idle check, **the written paths are discarded.** The pump (polling every 3s, `Program.cs:106`) writes the clip the instant its lock clears, then tells no one. By the time the user calls `library_watching` / `library_flush_queue`, the queue is already empty, so those calls honestly report `written: []`. The synchronous drain reporting is correct; the gap is the pump's silent writes.

### Fix, `DrainJournal` (Core, new), report-once
A thread-safe journal with **report-once** semantics:
- `Record(IEnumerable<DrainedTag>)`, the pump appends each successfully auto-flushed entry `(path, fieldsChanged[], whenUtc)` after each `DrainOnce`.
- `TakePending()`, returns all accumulated entries and clears them. Called by `library_watching`, `library_flush_queue`, and `library_queue_status`, which surface them as:
  ```
  "autoFlushed": [ { "path": ..., "fields": [...], "agoSeconds": N }, ... ]
  ```
- Instantiated once in `Program.cs`, injected into the pump and into read/queue tool registration. Capped (drop oldest beyond ~50 entries) so a user who never calls again can't leak memory; the common flow (advance → next call) reports every auto-flush exactly once.

The synchronous `drainedFromQueue` block stays as-is (it reports the call's own drain). `autoFlushed` is additive, it answers "did the tag I queued while the player was open land when it closed?" from the next response alone.

---

## 4. P0-2, gaming-mode `recent_write` rework

`RecentWriteSignal.Detect` predicate becomes: a clip is a `recent_write` candidate iff **all** of
1. its path is **not** in `KnownBaselinePaths` (genuinely new to the library), **and**
2. its `CreationTimeUtc` is within the freshness window (default 5 min) of now, **and**
3. it was **not** self-written within the window (ledger exclusion).

Newest-creation first; `Ambiguous = (count > 1)`. Everything downstream is unchanged: single-new-clip → high confidence / `anyLiveTarget=true` (Policy A, already shipped); multiple → low/confirm; a player hit still dominates; `include_access_fallback:false` still suppresses it; `spoken_at` two-clip disambiguation is untouched (it already works on the candidate set this produces).

This kills both failure modes at once: keyed on creation time (fresh on copy-in) and excluding self-writes (our own tag no longer looks like a save).

---

## 5. P1-1, `access_time` self-read exclusion

`AccessTimeSignal.Detect` filters out any clip self-*touched* within the window (`ledger.WasTouchedWithin`). A clip ClipMeta just exported or read for metadata no longer floats to the top of the fallback ranking. Near-zero added code, it falls straight out of §2.1. `library_list` deliberately does not mark, so baseline directory listings stay safe.

---

## 6. P1-2, player roster guard (soft advisory)

The "is this token a person or a tag?" check lives on the **write/queue path**, where a `players` value is actually committed, not in the resolver (which only decides *which clip*).

- **Known set** = `library_vocab players` (the existing vocab over the configured library) ∪ an optional `roster` argument (an array of names the model declares for the session, e.g. parsed from "tonight it's chuck and chicken"). Comparison is case-insensitive on the trimmed token.
- **Tools affected:** `clip_set_fields`, `clip_append_field` (when the field is `players`), and `library_queue_tag`. Each gains an optional `roster: string[]` arg.
- **Behavior (soft):** the write/queue proceeds normally. If any committed `players` token is outside the known set, the response carries a non-blocking advisory:
  ```
  "review": [ { "type": "unknownPlayer", "token": "miami element",
                "knownPlayers": ["chuck", "chicken", ...] } ]
  ```
  so the model confirms with the user and corrects (players accumulate and are easily re-tagged, exactly how the warpaint fix was applied cleanly after the fact).
- **Disclaimer:** a one-line "name your players up front so new names can be told apart from tags" sentence added to the affected tool descriptions.

The `review[]` channel mirrors the watching advisories' shape, kept as a top-level array on the write response (additive, does not alter existing response fields).

---

## 7. Surface, versioning, packaging

- **No new tools.** All changes are additive response fields (`autoFlushed`, write-side `review[]`) plus one optional `roster` arg on three existing tools. Tool count stays **17**; `Phase2ReadToolsTests.ToolsList_ContainsTheFullToolSurface` is unaffected. Per the CLAUDE.md rule, **run the full `clipmetamcp.Tests` anyway** (surface assertions live outside the diff).
- **Version bump to v1.4.0** (a behavior change the owner will dogfood) in **both** `clipmetamcp/clipmetamcp.csproj` and `tools/mcpb-manifest.json`, `pack-mcpb.ps1` fails the pack if they disagree.
- **Repack** `dist/clipmeta.mcpb` at the end; the owner reinstalls in Claude Desktop.

---

## 8. Tests

**New, pure/synthetic (explicit timestamps, the fixtures-as-signal-input lesson):**
- `SelfActionLedgerTests`: mark/within-window/expiry; written-not-downgraded-to-read; case-insensitive paths; thread-safety smoke.
- `RecentWriteSignalTests` (extend): new clip with fresh **creation** time but **old mtime** (the copy-preserves-mtime regression) → detected; in-baseline path → excluded; self-written path → excluded; outside window → no hit.
- `DrainJournalTests`: pump records → `TakePending` returns once and clears; cap drops oldest; concurrent record/take is safe.
- `AccessTimeSignalTests` (extend): a self-read path within window is excluded; an untouched path is not.

**New, MCP behavior (`clipmetamcp.Tests`):**
- `library_watching` / `library_flush_queue` surface `autoFlushed` after a pump drain (driven via injected pump/journal + fake clock).
- `clip_set_fields` / `library_queue_tag` with an unknown player → `review[]` `unknownPlayer` present; with the name in `roster` → absent; with a known-vocab name → absent. The write still lands either way (soft).

**Existing tests to reconcile (not blindly edit):** any `WatchContext`-built test that now sees creation-time or a ledger; access-time fallback tests touching files ClipMeta would mark. Each shift justified in the PR as correct new behavior. A `TouchCreated` helper (back-/forward-dating creation time) joins the existing `TouchStale` so a fixture's timestamp is explicit input.

**Full suites** `clipmetascribe.Tests` + `clipmetamcp.Tests` green; `dotnet build` 0 warnings / 0 errors.

---

## 9. Risks

| Risk | Mitigation |
|------|-----------|
| Ledger resets on server restart | By design, a restart is a new session; worst case reverts to today's behavior for pre-restart paths. Durability is not this feature's job. |
| Creation-time is NTFS/local-only (ReFS/network shares differ) | Manifest is already `win32`-only and the library is local; documented. Falls back to access-time behavior if creation-time is unreadable. |
| Pump thread + request threads share ledger & journal | Both lock-guarded; the journal's record/take and the ledger's mark/query are the only mutation points. |
| Absent/stale `.clipmeta-index` ⇒ empty baseline | Novelty degrades gracefully to creation-time + ledger (still beats mtime). A genuinely-new clip is correctly "new" until the next rebuild, desired. |
| `autoFlushed` not seen if the user never calls again | Acceptable, `library_export` shows it on disk; the common advance→next-call flow reports it. Report-once prevents stale repeats. |
| Roster soft guard still writes a wrong player | Intentional (owner chose soft): players accumulate and re-tag cleanly; the advisory prompts the correction. |
| Several existing tests shift | Reconciled individually with written justification; the full suites are the regression net. |

---

## 10. Definition of Done

1. `dotnet build` 0 warnings / 0 errors, all projects.
2. Full `clipmetascribe.Tests` + full `clipmetamcp.Tests` green (real-clip integration + media-integrity included).
3. Zero NuGet added to production projects.
4. New public types/args carry XML docs; `autoFlushed`, write-side `review[]`, and the `roster` arg documented on the tools; new gotchas → `docs/PITFALLS.md`.
5. Version bumped to **v1.4.0** in csproj + manifest; `dist/clipmeta.mcpb` repacked.
