# Queue Write Semantics, Provenance & Zero-Touch Flush — Design Spec
**Date:** 2026-06-25
**Round:** Watched-clip resolution, pass 3 (write side)
**Author:** Peckworks Lab
**Predecessors:** pass-2 deferred-tag queue (#30); companion spec
`2026-06-25-watched-clip-detection-robustness.md` (the *read* side — sequenced **first**).

---

## Problem Statement

The same 2026-06-25 dogfooding session that exposed the detection issues (companion spec) also
surfaced three things on the **write** side of the watch-and-tag loop:

1. **Re-tagging a clip silently overwrites free-text notes (data loss).** `library_queue_tag` maps
   *every* field to `MetadataMutation.SetFields` (`QueueTools.cs:104`), i.e. last-wins. The queue's
   in-memory merge is last-wins for set fields, and once a note has drained to disk a later set
   clobbers the on-disk value. In the session, the only reason "playing" + "driving through forest"
   both survived on one clip is that the model manually concatenated them. A voice user narrating in
   two breaths would lose the first. The decision (below) is **per-field semantics: append
   `notes`/`tags`/`players`, set the rest.**

2. **No provenance.** Nothing records that ClipMeta authored a tag. The owner wants every write to
   carry `tagged_by: Peckworks ClipMeta`. Confirmed absent — the seven clips tagged in the session
   have only `game`/`players`/`notes`. Decision: **auto-stamp on every write, with an opt-out.**

3. **The last clip of a session needs a manual flush.** The queue drains opportunistically on the
   next watched-clip tool call, so every clip *except the last* is zero-touch. The final clip — when
   the user closes the player and sends no further message — needs an explicit
   `library_flush_queue`. The owner wants the fast workflow ("speak it, click next, keep moving") to
   be zero-touch end to end. Decision: **build a background drain pump** that flushes the last clip
   when its lock clears, no user action required.

A note on what is **already right** and must not regress: the write engine already does
read-modify-write append on disk (`Mp4Writer.cs:139-170`) and already stamps a `schema` version on
every write (`Mp4Writer.cs:92-93`). Item 1 leans on the former; item 2 is modeled on the latter. The
durable queue (`.clipmeta-queue`) already guarantees no tag is lost across a crash or process exit;
item 3 is a *latency* optimization on top of that guarantee, never a replacement for it.

---

## Scope of This Round

**In scope:**

- **#1 Per-field append semantics** for `library_queue_tag`: `notes`/`tags`/`players` accumulate;
  `game`/`rating`/`timecode`/custom set. Plus the Core support it needs — **field-type-aware
  append** so `notes` joins as prose (case preserved, no dedup) while `tags`/`players` keep
  pipe-merge+dedup. This also fixes a latent `clip_append_field` bug (it currently lowercases and
  pipe-mangles appended notes).
- **#4 Provenance stamp** `tagged_by: Peckworks ClipMeta`, auto on every write, opt-out via a write
  flag. Backfill the seven session clips.
- **Zero-touch flush:** a background `QueueDrainPump` (Core) owned by the MCP server, draining the
  queue as locks clear while it is non-empty, idle when empty.
- **#7 Tool-description nudge** toward structured `players`/`tags` over dumping into `notes`.

**Out of scope:**

- The read/detection items (companion spec): library-aware title matching, `AnyLiveTarget`, player
  attribution, access-time quieting.
- Changing `clip_set_fields` semantics — it stays pure replace (its documented contract). Only
  `library_queue_tag` gains per-field append routing.
- Per-clip *timestamped* note history / structured multi-note model — appending into one `notes`
  string is the chosen model this round (see §1.4 for the rationale and the deferred alternative).
- Restart Manager, perf instrumentation (separate items).

---

## 1. Per-Field Append Semantics (#1 — the P0 data-loss fix)

### 1.1 The decision

| Field | Re-tag behavior via `library_queue_tag` | Join rule |
|---|---|---|
| `notes` | **append** | prose: `existing` + separator + `incoming`, trimmed, **case preserved, no dedup** |
| `tags` | **append** | pipe-merge + dedup + lowercase (existing list rule) |
| `players` | **append** | pipe-merge + dedup (existing list rule) |
| `game` | set (replace) | — |
| `rating` | set (replace) | — |
| `timecode` | set (replace) this round* | — |
| custom / unknown | set (replace) | — |
| any field set to `""` | **delete** (unchanged idiom) | — |

\* `timecode` is technically a pipe-list and a future round may move it to append; the owner's
explicit choice this round is "append notes/tags/players, set the rest," so `timecode` sets. Flagged,
not changed.

### 1.2 Field classification (Core, `ClipMetaSchema`)

`PipeFields` already = `{players, tags, timecode}`. Add:

```csharp
/// <summary>Free-text fields appended as prose (case preserved, no dedup), not pipe lists.</summary>
public static readonly IReadOnlySet<string> ProseFields = new HashSet<string> { Notes };

/// <summary>Fields library_queue_tag accumulates instead of overwriting on a re-tag.</summary>
public static readonly IReadOnlySet<string> QueueAppendFields = new HashSet<string> { Notes, Tags, Players };
```

`QueueAppendFields` encodes the owner's decision in one place. `ProseFields` drives the *join style*
for any append of those fields (queue merge **and** disk write), independent of routing.

### 1.3 Where the changes land

**`clipmetamcp` `QueueTools.QueueTag` (the routing):** replace the unconditional
`mutation.SetFields[...] = text` with per-field routing —

```
for each (name, value):
    if value == ""                  → SetFields[atom] = ""        // delete idiom, unchanged
    else if name ∈ QueueAppendFields → AppendFields[atom] = value  // accumulate
    else                             → SetFields[atom] = value     // replace
```

This is the whole P0 fix at the tool layer: appended fields now flow through `AppendFields`, which
the queue merge accumulates and the write engine folds onto the *current on-disk* value — closing
both the in-queue clobber and the drained-then-retag clobber.

**Core `Normalizer` / `Mp4Writer` (field-type-aware append fold):** today the fold
(`Mp4Writer.cs:141-170`) runs every append through `AppendToPipeList`, which lowercases, dedups, and
pipe-joins — correct for lists, **wrong for prose** (it would turn `"Chuck wins"` + `"raccoon"` into
`"chuck wins|raccoon"`). Make the fold consult `ProseFields`:

- Prose field → `combined = string.IsNullOrEmpty(current) ? incoming.Trim() : current.TrimEnd() + SEP + incoming.Trim()`; **no** lowercasing, **no** dedup.
- List field → `AppendToPipeList(current, incoming)` (unchanged).

`SEP` is a single space (`" "`), matching the chosen behavior (`"A"` + `"B"` → `"A B"`).
> Open knob (cosmetic): `"; "` reads better for multi-sentence narration. Recommend evaluating in
> the next test round; the constant lives in one place so it is a one-line change.

Mirror the same prose-vs-list distinction in **`TagQueue.Merge` / `PipeMerge`** so two queue entries
for the same locked clip accumulate `notes` as prose and `tags`/`players` as deduped lists.

**`clip_append_field` (bonus fix):** it already routes to `AppendFields`, so it inherits the
prose-aware fold automatically — appended `notes` will stop being lowercased and pipe-mangled. Add a
test pinning this; no handler change needed.

### 1.4 Why one appended `notes` string (and not a note history)

The session raised whether multi-narration should accumulate timestamped note entries. This round
chooses the simpler model — append into the single `notes` string — because it requires no schema
change, keeps `notes` a plain readable field, and matches the voice workflow ("add a phrase to what
this clip is about"). A structured/timestamped note model is recorded as a deferred option if real
use shows the flat string is insufficient.

---

## 2. Provenance Stamp (#4)

### 2.1 Schema

```csharp
public const string TaggedBy = "tagged_by";               // atom: com.peckworkslab.clipmeta:tagged_by
public const string ProvenanceValue = "Peckworks ClipMeta";
```

`tagged_by` is a **visible** field (it appears in `--list` and `clip_get_metadata` — that is the
point), but it is excluded from `--vocab` aggregation and "untagged" detection treats it the way it
treats `schema`: a clip carrying *only* `tagged_by` is not "tagged." In practice this never arises,
because (see §2.2) provenance is stamped only when the write also stores a real user field.

### 2.2 Stamping (Core, `Mp4Writer`)

Alongside the existing schema stamp, under the **same gate** (the mutation stores at least one user
set/append field — we do not brand a file we are not otherwise writing user data to):

```csharp
if (mutation.StampProvenance && (mutation.SetFields.Count > 0 || mutation.AppendFields.Count > 0))
    mutation.SetFields.TryAdd(AtomName(TaggedBy), ProvenanceValue);
```

`TryAdd` means a caller that explicitly sets `tagged_by` keeps its value (e.g. a downstream tool or a
re-brand). The orphaned-schema-stamp cleanup (`Mp4Writer.cs:281`) is mirrored for `tagged_by`, so a
mutation that ends up writing no user field leaves neither stamp behind.

### 2.3 Opt-out

Add to `MetadataMutation`:

```csharp
/// <summary>Stamp tagged_by: Peckworks ClipMeta on write (default true). Opt-out for users who
/// don't want provenance written into their files.</summary>
public bool StampProvenance { get; init; } = true;
```

- **MCP write tools:** add optional `stamp_provenance` (default `true`) to the shared write
  properties (next to `backup`/`dry_run`), mapped to `MetadataMutation.StampProvenance`.
- **CLI `clipmetascribe`:** add `--no-provenance` to the write command.
- **Queue drains always stamp.** `QueuedMutation` does not carry the flag; a queued tag is, by
  definition, ClipMeta-authored, so the drain reconstructs the mutation with `StampProvenance =
  true` (the default). The opt-out therefore governs direct `clip_set_fields`/`clip_append_field`
  only — which is the correct scope (a user opting out of branding is doing direct edits, not
  driving the watch loop).

### 2.4 Backfill the seven session clips

These predate the stamp. Provenance only fires when a write also stores a user field, so backfill is
a one-time idempotent re-write: for each of the seven clips, re-set an existing field to its current
value (e.g. `game` → same value). This triggers the stamp without altering user data. Low stakes
(test library); documented as a manual step, not a feature. If a larger library ever needs it, a
dedicated `clipmetascribe --dir … --restamp` maintenance op is the clean follow-on (recorded, not
built).

---

## 3. Zero-Touch Background Flush (`QueueDrainPump`)

### 3.1 Goal

Eliminate the one remaining manual step — the explicit `library_flush_queue` for the last clip —
so the workflow is zero-touch end to end. When the user closes the player on the final clip, its
write should land on its own, with no further message.

### 3.2 Why not FileSystemWatcher or a process-exit hook

The owner suggested both; neither is the right primitive:

- **FileSystemWatcher** raises events on file create/change/delete/rename. A media player
  *releasing its handle* (the unlock we are waiting for) is **not** a filesystem mutation, so FSW
  never fires on it. It would watch the wrong thing.
- **Process-exit hooks** (`Process.Exited`) are racy against real player behavior — playlists keep
  one process alive across clips, users run two players, processes are re-used — and require holding
  a handle per player process and continuously re-scanning as they churn.

The robust primitive is a **debounced poll of the lock state of the (tiny) queued set**, active only
while the queue is non-empty. The queued set is exactly the clips just tagged — a handful — and the
lock probe is a cheap shared-open attempt (`LockProbe.IsInUse`). When a lock clears, the next tick
drains it. When the queue empties, the pump idles until the next enqueue wakes it. This watches the
*right* signal (the lock) at negligible cost.

### 3.3 Design (Core: `ClipMetaCore.Watching.QueueDrainPump`)

A small, testable background driver around the existing `TagQueue.Drain`:

```csharp
public sealed class QueueDrainPump : IDisposable
{
    public QueueDrainPump(
        string libraryRoot,
        IMediaWriter writer,
        IClipMetaLogger logger,
        Func<string, bool> isInUse,
        Action<Action> runExclusive,     // serialize drains against other writers (WriteGate)
        TimeSpan pollInterval);          // default ~3s; injectable for tests

    public void Start();                 // launches the background loop (idle until Wake)
    public void Wake();                  // called after each Enqueue: there may be work
    public void Dispose();               // signals stop, joins the thread
}
```

**Loop:**

1. Wait on a wake signal (`AutoResetEvent`/`ManualResetEventSlim`) — zero CPU while idle.
2. On wake: run one drain under `runExclusive`. If `TagQueue.Status` shows entries still locked
   (`stillQueued` non-empty), wait `pollInterval` and drain again. Repeat until the queue is empty.
3. Empty → return to step 1.
4. `Dispose`/cancellation breaks the loop and joins; the thread is gone before `Serve()` returns.

**Invariants:**

- **Never throws into the void.** Each drain tick is wrapped; any exception is logged and the loop
  continues. (`TagQueue.Drain` already swallows per-entry write failures and keeps them queued; the
  pump guards the surrounding tick.)
- **Serialized with all other writes.** Drains run inside `runExclusive`, which the MCP shell backs
  with `WriteGate.Enter/Exit`. This is the moment `WriteGate` stops being "insurance against a future
  pipelined host" and becomes load-bearing: the pump is a genuine second writer thread. No drain can
  race a `clip_set_fields` or an opportunistic `queue_tag` drain at `File.Replace`.
- **Durability is still the queue's job, not the pump's.** If the host kills the server before the
  lock clears, the tag remains in `.clipmeta-queue` and drains on the next session's first
  watched-clip call. The pump optimizes the common case; it is not the integrity mechanism.

### 3.4 Wiring (MCP shell only — thin-shell rule)

- `QueueDrainPump` lives in Core (testable, no MCP dependency); `WriteGate` stays in `clipmetamcp`.
  Core stays decoupled via the injected `runExclusive` seam (the same trailing-injectable
  convention used elsewhere).
- `Program.Serve()` constructs the pump after the sandbox (only when a library root is configured),
  `Start()`s it before `session.Run()`, and `Dispose()`s it after `Run()` returns on stdin EOF.
- `QueueTools.RegisterAll` takes the pump so `QueueTag` can call `pump.Wake()` right after
  `TagQueue.Enqueue` — the enqueue is the signal that a lock-clear is coming. `library_flush_queue`
  remains for explicit/manual use and as the no-library-root fallback.

### 3.5 Minor interaction

A background drain rewrites a clip via `File.Replace` while a `clip_get_metadata` read might be in
flight; reads are not under `WriteGate`. This window already exists today (queue_tag drains
opportunistically mid-session); the pump widens it slightly. Reads already tolerate transient IO
(their handlers translate `IOException` into a refusal the model can retry), so this is an accepted,
documented minor risk, not a correctness hole.

---

## 4. Structured-Extraction Nudge (#7)

The session wrote almost everything into `notes`; `players` was used once and `tags` never — so
"find every clip with hetare" or "every raccoon clip" can't work, because the searchable nouns are
buried in prose. This is caller/model behavior, but the **tool descriptions are our lever**.

Update the `library_queue_tag` (and `clip_set_fields`) descriptions to instruct: when a narration
names people, route them to `players`; when it names searchable nouns/moments (objects, locations,
events), route them to `tags`; reserve `notes` for the free-text summary. With §1's per-field
append, repeated `players`/`tags` now accumulate cleanly, which makes following this guidance cheap
for the caller. No schema or code change beyond the description text.

---

## 5. Affected Types (change summary)

| Type | Change |
|---|---|
| `ClipMetaSchema` | + `TaggedBy`, `ProvenanceValue`, `ProseFields`, `QueueAppendFields`. |
| `MetadataMutation` | + `bool StampProvenance = true`. |
| `Mp4Writer` | Provenance stamp (gated, `TryAdd`); field-type-aware append fold (prose vs pipe); mirror orphan-cleanup for `tagged_by`. |
| `Normalizer` | Prose-join helper; ensure prose appends bypass pipe-list normalization. |
| `TagQueue` | `Merge`/`PipeMerge` field-type-aware (prose vs list accumulation). |
| `QueueDrainPump` (new, Core) | Background debounced drain loop; `Start`/`Wake`/`Dispose`. |
| `clipmetamcp` `QueueTools` | Per-field routing in `QueueTag`; `Wake()` after enqueue; pump param in `RegisterAll`. |
| `clipmetamcp` `WriteTools` | `stamp_provenance` arg; description nudge (#7). |
| `clipmetamcp` `Program` | Construct/Start/Dispose the pump around the session. |
| `clipmetascribe` write command | `--no-provenance`; (optional later) `--restamp`. |

No change to the read tools, the parser, or the `File.Replace` golden rule. SOLID preserved: the
pump is additive and Core-decoupled; field classification is data, not new branches in the writer's
hot path.

---

## 6. Test Strategy

Run the **full** `clipmetascribe.Tests` and `clipmetamcp.Tests` projects (not filtered) per the
CLAUDE.md surface-test rule — `QueueTools` routing and the tool surface change.

**Append semantics (`clipmetascribe.Tests`, with scratch clips)**
- Queue `notes:"A"`, drain to disk; queue `notes:"B"`, drain → on-disk `notes == "A B"` (the P0
  regression — must go red before the fix).
- Two `notes` queue entries while locked merge to `"A B"` in one entry (in-queue prose accumulation).
- Queue `tags:"x"` then `tags:"x|y"` → on-disk `tags == "x|y"` (dedup, lowercase preserved as list).
- Queue `players:"chuck"` then `players:"chicken"` → `"chuck|chicken"`.
- Queue `game:"A"` then `game:"B"` → `"B"` (set/replace, not appended).
- `notes:""` → deletes notes (delete idiom intact for an append field).
- `clip_append_field` on `notes` preserves case and does not pipe-mangle (latent-bug pin).

**Provenance (`clipmetascribe.Tests`)**
- Any write storing a user field stamps `tagged_by: Peckworks ClipMeta`.
- A write that stores no user field (pure delete / no-op) leaves no orphan `tagged_by`.
- `StampProvenance = false` → no stamp.
- A caller-supplied `tagged_by` is not overwritten (`TryAdd`).
- A queue drain stamps provenance (always-on for queue writes).

**`QueueDrainPump` (`clipmetascribe.Tests`, fakes — no real player/timer)**
- Inject a fake `isInUse` that returns true then false, a fake `runExclusive`, a fake `IMediaWriter`,
  a short interval: after the lock "clears," the pump drains the entry exactly once.
- Empty queue → pump idles (no drain calls) until `Wake()`.
- A drain that throws is swallowed; the loop survives and drains on the next tick.
- `Dispose()` stops the loop promptly and joins (no thread left running).
- Every drain is observed to run inside `runExclusive` (serialization proof).

**`clipmetamcp.Tests`**
- `library_queue_tag` routes `notes`/`tags`/`players` to append and `game` to set (assert via the
  resulting on-disk/queue state).
- `stamp_provenance:false` suppresses the stamp through the tool.
- `ToolsList_ContainsTheFullToolSurface` still passes (no tool added/removed).
- Stdout purity preserved with the pump running (no background write leaks to the protocol channel).

---

## 7. Risk Table

| # | Risk | Mitigation |
|---|---|---|
| 1 | Prose append run through pipe normalization → notes lowercased/dedup'd | `ProseFields` gates the join; explicit case-preservation test. |
| 2 | Background pump races a foreground write at `File.Replace` | All drains under `runExclusive`→`WriteGate`; serialization test. |
| 3 | Pump thread crash silently kills zero-touch flush | Per-tick try/catch + log; loop survives; durability fallback covers worst case. |
| 4 | Pump leaks/spins on shutdown | `Dispose` cancels + joins before `Serve()` returns; idle waits on an event, no busy-loop. |
| 5 | Provenance brands files a user didn't want | Opt-out flag; stamped only alongside real user writes; `TryAdd` respects a caller value. |
| 6 | `timecode` set-not-append surprises a user | Documented decision; revisit next round; trivial to move to `QueueAppendFields`. |
| 7 | Background drain collides with an in-flight read | Pre-existing window; reads tolerate transient IO; documented (§3.5). |

---

## 8. Definition of Done

1. `dotnet build` — 0 warnings, 0 errors, all projects.
2. `dotnet test` — full `clipmetascribe.Tests` and `clipmetamcp.Tests` pass, including the notes
   round-trip P0 regression, provenance, and `QueueDrainPump` tests.
3. Re-tagging a clip's `notes` through the queue accumulates instead of overwriting, on disk.
4. Every user-data write carries `tagged_by: Peckworks ClipMeta` unless opted out; the seven session
   clips are backfilled.
5. Closing the player on the last clip drains its queued tag with no further tool call (pump),
   while the durable queue still covers a host kill.
6. All clip mutations — direct, opportunistic drain, and pump drain — are serialized through the
   write single-flight.
7. Zero NuGet packages added to production projects.
8. New public types/methods documented; PITFALLS updated with the notes-clobber gap and the
   pump/WriteGate concurrency note.

---

## 9. Sequencing & Follow-on

- **Sequenced after** the companion detection spec, so append/provenance operate on
  better-identified targets (a wrong-target write is worse once writes also accumulate).
- **Deferred (recorded):** timestamped/structured note history; `timecode` append; a
  `--restamp` maintenance op for large-library provenance backfill; perf instrumentation.
- **Open data issue (not code):** the `18.25.20.35` drift and the VLC-round notes are unreliable;
  re-verify next test round before trusting that data.
