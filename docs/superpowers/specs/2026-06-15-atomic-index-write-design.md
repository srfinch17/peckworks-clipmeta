# Atomic index-write — Design Spec

**Date:** 2026-06-15
**Status:** Approved (brainstorming) → implementing
**Feature:** 2 of 3 deferred (CopyTags → **atomic index-write** → batch operations)

---

## Problem

`ClipMetaIndex.WriteToFile` opens the destination with `new StreamWriter(filePath,
append: false, …)`, which **truncates the target the instant it opens**. If the
process is interrupted between that open and the final flush — a crash, power loss,
disk-full, or an exception while serializing — the existing `.clipmeta-index` is left
truncated or empty. The user loses a built index to a partial write. This contradicts
the project's safety-first discipline, which everywhere else mutates via a temp file
and an atomic swap (see `Mp4Writer`).

## Scope

### In
- Make `ClipMetaIndex.WriteToFile` write to a temp file then atomically swap it into
  place, so the existing index is never visible in a half-written state and a failed
  write leaves the previous index intact.
- Reuse the write engine's transient-lock retry for the swap (AV/indexer race; see
  PITFALLS 2026-06-12).

### Out
- The format, the `Write`/`Read` (TextWriter/TextReader) APIs, and the on-disk
  encoding (UTF-8 with BOM) are unchanged — round-trip compatibility is preserved.
- Stale-cache warnings (FileSizeBytes/LastModified comparison) — a separate deferred item.

## Design

`WriteToFile(IndexData data, string filePath)`:

1. Serialize to `tempPath = "{filePath}.{guid}.tmp"` via the existing `Write` + a
   `StreamWriter` using the **same `Encoding.UTF8`** as today.
2. Atomically swap: `File.Move(tempPath, filePath, overwrite: true)` — on Windows,
   same-volume `MoveFileEx(REPLACE_EXISTING)` is atomic and works whether or not the
   target already exists (no TOCTOU `Exists` branch). The temp and target are in the
   same directory, hence the same volume.
3. Wrap the swap in `Mp4Writer.RetryOnTransientLock` (5 attempts, 100 ms × attempt) —
   the temp is already fully written, so retrying the atomic swap weakens nothing; it
   only tolerates a transient AV/indexer lock, exactly as the MP4 writer's swap does.
4. On ANY exception, delete the temp (best-effort) and rethrow. The original index is
   untouched because it is never opened for writing until the swap.

`RetryOnTransientLock` is an `internal static` helper already living in `Mp4Writer`
and unit-tested; it is a general file-lock utility, so `ClipMetaIndex` (same assembly)
reuses it rather than duplicating the retry loop. (If a third consumer appears, extract
it to a neutral home — not yet, YAGNI.)

## Error handling

| Case | Behavior |
|------|----------|
| Serialization throws mid-write | temp (partial) deleted; **existing index intact**; exception propagates |
| Transient AV/indexer lock on swap | retried up to 5×; succeeds or finally throws (existing index intact) |
| First write (no existing index) | `File.Move(overwrite:true)` creates it |

## Testing (TDD)

- **The bug, captured:** pre-write a valid index, then call `WriteToFile` with an
  `IndexData` whose entry enumeration throws partway (simulating an interrupted write).
  Assert the **existing index is byte-for-byte unchanged** and **no `.tmp` remains**.
  This fails on the truncate-on-open implementation and passes on the atomic one.
- Successful write leaves **no temp file** behind.
- Round-trip via `WriteToFile`→`ReadFromFile` still works (regression), including the
  first-write (no pre-existing target) path.

## Definition of Done

1. `dotnet build` — 0 warnings / 0 errors.
2. `dotnet test` — all pass, including the new atomic-write tests; no regressions.
3. Zero NuGet; format/encoding unchanged; `ClipMetaIndex` stays the only file touched in Core.
4. PITFALLS updated with the truncate-on-open → temp-then-swap lesson.
