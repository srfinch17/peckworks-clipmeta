# Watched-Clip Resolution — Pass 2 (Deferred-Tag Queue) — Design Spec
**Date:** 2026-06-21
**Status:** Approved for planning (brainstorm complete)
**Builds on:** `2026-06-21-watched-clip-resolution-design.md` (pass 1, merged PR #27) and `2026-06-21-watched-clip-resolution-pass1.5-design.md` (pass 1.5, merged PR #28)
**Author:** Peckworks Lab

---

## Problem Statement

Pass 1/1.5 resolve **which** library clip an open player is showing, with honest confidence. But resolution alone cannot *tag* a playing clip, because of a hard OS constraint:

**A playing file is locked against our write.** `File.Replace` (and our temp-file → atomic-swap engine) deletes-and-swaps the target, which fails with a sharing violation unless every open handle was opened with `FILE_SHARE_DELETE` — which media players do not grant. So we generally **cannot write to a clip while it is playing.** This is not a bug to fix; it is a constraint to design around.

The natural consequence: you watch a clip, say *"tag this as a headshot,"* and the write can only land once you advance to the next clip or close the player. The pump that frees the lock — advancing — is *also* the moment you issue your next command. So a **durable deferred-tag queue** that drains opportunistically on your next watched-clip call turns the constraint into a near-invisible delay: every tag lands the instant you move on, and only the *final* clip waits for an explicit flush.

This spec makes §9 of the pass-1 design concrete. It adds **no new resolution logic** — it persists confirmed tags and drains them as locks clear.

---

## Scope

**In scope:**
- A durable, library-root-resident tag queue (`.clipmeta-queue`, JSON) keyed by clip full-path.
- A `TagQueue` Core engine: load (corruption-tolerant), atomic save, enqueue-with-merge, single-threaded drain, status.
- **Opportunistic drain** on each watched-clip MCP call (`library_watching` *and* `library_queue_tag`), before resolving/enqueuing the current clip.
- **Explicit drain** via MCP `library_flush_queue` and CLI `clipmetascribe "<libraryDir>" --flush-queue`, for the final clip when there is no "next" command to pump the drain.
- MCP surfaces: `library_queue_tag`, `library_flush_queue`, `library_queue_status`.
- CLI surfaces: `--flush-queue`; a pending-count footer in `--watching` output.

**Out of scope (deferred / unchanged):**
- **Resolution changes.** Confidence, collision guard, wrong-directory warning are all pass-1/1.5 and untouched.
- **Low-confidence enqueue.** The queue only ever stores an **already-resolved, confirmed** path. Disambiguation is the agent's job, done conversationally *before* it calls `library_queue_tag`. The queue does not resolve and does not guess. (See §4.)
- **CLI enqueue.** You do not hand-type spoken tags; enqueue is MCP-only. The CLI gets flush + status only.
- **Session payload / "run profile" workflow** (e.g. auto-applying `game=Team Fortress 2`, spoken-tag composition) — its own future spec; sits *above* the queue.
- **Background daemon / filesystem watcher.** No polling; the drain is pumped by the calls you already make.
- **Per-player lock-release characterization.** Whether MPC/VLC release the lock on *next*, *stop*, or only *close* is an empirical dogfooding item (PITFALLS). The design is robust to all of them — it retries until the lock clears regardless — so the answer shapes user guidance, not architecture.

---

## 1. The natural pump (data flow)

```
You watch clip A ─► "tag this headshot"
   └─ agent calls library_watching ─► resolves A (high) ─► [drain runs: nothing queued yet]
   └─ agent calls library_queue_tag(A, tags+=headshot)
        ├─ [drain runs first: A still locked (you're watching it) → stays in nothing-to-do]
        └─ enqueue A → .clipmeta-queue  ◄── A is now durably queued

You advance to clip B  (A's handle is released by the player)
You watch clip B ─► "tag this airshot"
   └─ agent calls library_watching ─► resolves B
        └─ [drain runs first: A now unlocked → WRITE A, remove from queue]  ◄── A lands
   └─ agent calls library_queue_tag(B, tags+=airshot) ─► enqueue B

You stop (B is the last clip, still locked until you close the player)
   └─ close player, then: library_flush_queue (or CLI --flush-queue)
        └─ [drain: B now unlocked → WRITE B]  ◄── B lands
```

**UX consequence (honest):** every tag lands the moment you advance; you only ever wait on the *final* clip until a flush.

---

## 2. Data model

Three serializable records in `clipmeta.core/Watching/`, decoupled from the live `MetadataMutation` so the on-disk schema stays stable even if `MetadataMutation` grows new transient flags:

```csharp
// The DURABLE subset of a mutation — no DryRun, no BackupPath (transient write-time flags).
public sealed record QueuedMutation(
    IReadOnlyDictionary<string, string?> SetFields,
    IReadOnlyDictionary<string, string> AppendFields,
    IReadOnlyList<string> DeleteFields,
    bool ClearAll);

public sealed record QueuedTag(
    string ClipPath,                 // full path; the queue key
    QueuedMutation Mutation,
    DateTimeOffset EnqueuedAtUtc,
    string Confidence);              // the resolution confidence at enqueue time (record-keeping)

public sealed record TagQueueData(
    int Version,                     // schema version (start at 1)
    IReadOnlyList<QueuedTag> Entries);
```

- **Mapping helpers:** `QueuedMutation.From(MetadataMutation)` (drops `DryRun`/`BackupPath`) and `ToMutation()` (rebuilds a `MetadataMutation` for the write engine, `DryRun=false`, `BackupPath=null`).
- **Serialization:** `System.Text.Json` (BCL — zero-NuGet preserved). File `.clipmeta-queue` in the library root, mirroring `.clipmeta-index`'s location.

---

## 3. `TagQueue` (Core engine)

`clipmeta.core/Watching/TagQueue.cs` — the only new piece of logic. Static, mirroring `ClipMetaIndex`'s shape.

| Method | Behavior |
|--------|----------|
| `const QueueFileName = ".clipmeta-queue"` | File name written in the library root. |
| `Load(libraryDir)` → `TagQueueData` | Missing **or corrupt** file → empty queue. **Never throws** (same tolerance as index reads). |
| `SaveToFile(data, libraryDir)` | Atomic temp-then-swap: serialize to `…{guid}.tmp`, then `Mp4Writer.RetryOnTransientLock(() => File.Move(tmp, target, overwrite: true))`. Identical discipline to `ClipMetaIndex.WriteToFile`. No half-written queue ever visible. |
| `Enqueue(libraryDir, clipPath, mutation, confidence)` | **Merge** onto any existing entry for that path: set last-wins, append accumulates+dedups, delete unions, `ClearAll` ORs. One entry per clip — never a duplicate. Path-keyed case-insensitively on Windows (`StringComparer.OrdinalIgnoreCase`). Persists via `SaveToFile`. |
| `Drain(libraryDir, writer, lockProbe)` → `DrainReport` | **Single-threaded.** For each entry: if the clip is **gone** → drop (record in `Dropped`); else if `lockProbe.IsInUse(path)` is **true** → keep (record in `StillQueued`); else **apply** `entry.Mutation.ToMutation()` via the write engine → on success drop from queue (record in `Written`), on write failure keep + record. Persist the surviving queue once at the end. |
| `Status(libraryDir)` → `IReadOnlyList<QueueStatusEntry>` | Per-entry path, summarized fields, age (`now − EnqueuedAtUtc`), and current `locked?` (`lockProbe.IsInUse`). |

```csharp
public sealed record DrainReport(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> StillQueued,
    IReadOnlyList<string> Dropped);    // vanished/moved clips
```

**Dependency seam:** `Drain`/`Status` take the `LockProbe` (the cloud-safe pass-1.5 probe) and the write engine (`Mp4Writer` / `IMediaWriter`) as parameters so they are testable with fakes — consistent with the "testable surfaces" convention. The thin shells construct the real ones.

---

## 4. Confidence / the "dumb queue" invariant

The queue **never resolves and never guesses**. It stores only an explicit `clipPath` handed to it. The low-confidence decision lives where it already does — in pass-1/1.5 resolution + the agent:

- The agent calls `library_watching`, inspects candidates/diagnostics.
- **High-confidence single hit →** agent calls `library_queue_tag(path, fields)`.
- **Low-confidence / ambiguous / wrong-directory →** agent does **not** enqueue; it reports the ambiguity and asks you which clip you meant. Only once a confirmed path exists does it enqueue.

This keeps pass-1.5's "don't tag the wrong clip" guarantee intact: nothing reaches the durable queue that you didn't confirm. `library_queue_tag` may still defensively reject a `clipPath` that isn't inside the configured library (sandbox consistency with the other tools).

---

## 5. Drain triggers

- **Opportunistic (the pump):** drain runs at the **start of both** `library_watching` and `library_queue_tag`, before the current clip is resolved/enqueued. Draining on *both* watched-clip calls (not just enqueue) maximizes the chance a freed lock is caught promptly.
- **Explicit:** `library_flush_queue` (MCP) and `clipmetascribe "<libraryDir>" --flush-queue` (CLI) run a drain on demand — for the **final** clip, when you have stopped and there is no "next" command to pump the drain. (A best-effort flush on MCP-server shutdown is a possible nicety, noted but not required for v1.)

No daemon, no polling, no `FileSystemWatcher`.

---

## 6. Surfaces

### MCP (`clipmetamcp`)
| Tool | Input | Output |
|------|-------|--------|
| `library_queue_tag` | `clipPath` (already resolved/confirmed) + field mutations (same shape as the write tools) | Drains opportunistically, then enqueues; returns the enqueue result + any drain results (what just landed). |
| `library_flush_queue` | — | Drains now; returns `DrainReport` (landed / still-locked / dropped), model-readable. |
| `library_queue_status` | — | Lists pending entries (path, fields, age, locked?). |

- All three refuse cleanly (model-readable message) when no library is configured, like `library_watching`.
- **Registration:** adding three tools means updating `clipmetamcp.Tests` `ToolsList_ContainsTheFullToolSurface` (exact set + order). **Per CLAUDE.md, run the FULL `clipmetamcp.Tests` project, not a `--filter`** — that surface-wide assertion lives outside the diff. (It bit us registering `library_watching`.)
- Tool count goes 8 → 11. Update `MEMORY.md` / `reference_mcp_server` and note the `.mcpb` repack need.

### CLI (`clipmetascribe`)
- `--flush-queue` (with the existing library-dir argument) → new `FlushQueueCommand` (thin shell → `TagQueue.Drain`). Prints landed / still-locked / dropped.
- `--watching` output gains a **pending-count footer** (e.g. `Queued tags pending: 2`) sourced from `TagQueue.Status`.
- **No CLI enqueue.**

---

## 7. Safety invariants

- **Single-threaded drain** over the already per-file-safe write engine — no two writes race the same file; we simply never drain concurrently.
- **Cloud-safe:** drain/status reuse the pass-1.5 `LockProbe`, which never opens an `Offline` placeholder, so probing can never force a Dropbox/OneDrive download; it never throws.
- **Corruption-tolerant load:** a malformed `.clipmeta-queue` is treated as empty, never crashing a watched-clip call.
- **Atomic persistence:** temp-then-swap, so a crash/disk-full mid-save leaves the prior queue intact.
- **Field-level application at drain time:** the mutation is applied to the file's **current** parsed state when it drains, so an external edit between enqueue and drain is harmless (no stale snapshot, unlike a whole-file replace).
- **Vanished/moved clip → dropped with a note**, never a crash or a write to a wrong path.

---

## 8. Test strategy (extends §10 of the pass-1 spec)

Core logic tested **through** `clipmetascribe.Tests` (real write engine + temp dirs + fake/real `LockProbe`); MCP shape through `clipmetamcp.Tests` — matching the existing no-standalone-Core-test convention.

**`TagQueue` (engine):**
- Enqueue while locked → entry persisted to `.clipmeta-queue`; unlock → next `Drain` writes it; the file gains the tags.
- Re-tag an already-queued clip → payloads **merge** (append accumulates+dedups, set last-wins, delete unions), exactly **one** entry remains.
- `Drain` with a still-locked entry → entry stays; report lists it under `StillQueued`; file untouched.
- Explicit flush writes the last clip after the lock clears.
- **Queue survives a simulated restart** — write queue, re-`Load` from disk, contents identical.
- **Corrupt `.clipmeta-queue` → `Load` returns empty, no throw.**
- **Vanished/moved queued clip → `Drain` drops it (in `Dropped`), no crash.**
- Atomic save: a failed swap leaves the previous queue readable (mirror the index test).
- Offline placeholder entry → `Drain` never opens it (no forced download), stays queued.

**`QueuedMutation` mapping:**
- `From(MetadataMutation)` drops `DryRun`/`BackupPath`; `ToMutation()` round-trips set/append/delete/clearAll and sets `DryRun=false`.

**MCP (`clipmetamcp.Tests`):**
- `library_queue_tag`, `library_flush_queue`, `library_queue_status` registered — **`ToolsList_ContainsTheFullToolSurface` updated** (exact set + order); rides the stdout-purity harness on Windows and Linux.
- Each refuses cleanly when no library is configured.
- `library_queue_tag` rejects a `clipPath` outside the configured library (sandbox consistency).

---

## 9. File map

```
clipmeta.core/
└── Watching/
    ├── QueuedMutation.cs            ← NEW: durable mutation DTO + mapping
    ├── QueuedTag.cs                 ← NEW: queue entry record
    ├── TagQueueData.cs             ← NEW: queue file model
    └── TagQueue.cs                  ← NEW: load / save / enqueue / drain / status (the engine)

clipmetascribe/
├── Program.cs                       ← MODIFIED: route --flush-queue; --watching footer
└── FlushQueueCommand.cs             ← NEW: thin shell → TagQueue.Drain

clipmetamcp/
└── (tool registration)              ← MODIFIED: register library_queue_tag / _flush_queue / _queue_status

tests/                               ← TagQueue tests in clipmetascribe.Tests; tool-surface tests in clipmetamcp.Tests
```

(Exact `clipmetamcp` registration file and `clipmetascribe` arg-routing locations confirmed against current code in the implementation plan.)

---

## 10. Risk table

| # | Risk | Mitigation |
|---|------|-----------|
| 1 | Tagging faster than writes can land (locked files) | Durable queue + opportunistic/explicit drain |
| 2 | Queue write races a direct write on the same file | Single-threaded drain; per-file-safe write engine; queue keyed by path |
| 3 | Player never releases the lock on "next" (only on close) | Drain retries until the lock clears regardless; explicit flush for the last clip; empirical PITFALLS note for user guidance |
| 4 | Corrupt/partial queue file crashes a watched-clip call | Corruption-tolerant `Load` (→ empty); atomic temp-swap save |
| 5 | A wrong/guessed clip reaches the durable queue | "Dumb queue" invariant — only confirmed, agent-resolved paths enqueued; library-sandbox check on `clipPath` |
| 6 | Probing a queued clip forces a cloud download | Reuse cloud-safe `LockProbe` (never opens `Offline` placeholders) |
| 7 | Adding tools silently breaks the tool-surface contract | Update + run the FULL `clipmetamcp.Tests` (CLAUDE.md rule) |
| 8 | Persisting `MetadataMutation` leaks transient flags / brittle schema | Decoupled `QueuedMutation` DTO (no DryRun/BackupPath) |

---

## Definition of Done

1. `dotnet build` — 0 warnings, 0 errors, all projects.
2. `dotnet test` — all pass, including the new `TagQueue` tests and the updated MCP tool-surface test (full project run).
3. Zero NuGet packages added to production projects (`System.Text.Json` is BCL).
4. Public types documented; any new gotcha (e.g. observed per-player lock-release behavior during dogfooding) recorded in `docs/PITFALLS.md`.
5. `MEMORY.md` / `reference_mcp_server` updated for the 8 → 11 tool count and the `.mcpb` repack need.

## Acceptance behaviors

1. Tags spoken faster than writes can land are queued durably and drained as locks clear (opportunistic) and on explicit flush; the last clip lands after a flush.
2. Re-tagging a queued clip merges into one entry.
3. The queue survives an MCP-host restart/crash.
4. A vanished queued clip is dropped, not crashed on.
5. Nothing the user did not confirm is ever written.
