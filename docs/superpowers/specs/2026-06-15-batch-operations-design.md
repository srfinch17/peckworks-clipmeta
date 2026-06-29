# Batch write operations, Design Spec

**Date:** 2026-06-15
**Status:** Approved (brainstorming) → implementing
**Feature:** 3 of 3 deferred (CopyTags → atomic index-write → **batch operations**)

---

## Problem

Every write command (`--set`/`--append`/`--clear`/`--clear-all`/`--copy-from`) operates on
a single `.mp4`. Tagging or copying across a folder means invoking the tool once per file.
Batch lets the same write target a directory: apply the operation to every clip, isolating
per-file failures so one bad clip never aborts the run.

## Scope

### In
- When the positional path is a **directory** and a **write op** is present, apply that op to
  every `.mp4` in the directory (recursive, matching the existing `--find`/`--export`/`--index`
  directory commands).
- Per-file error isolation + an aggregate summary and exit code.
- Batch forms of `--set`/`--append`/`--clear`, `--clear-all` (confirm once), and `--copy-from`
  (batch-copy, skipping the source clip).
- `--dry-run`, `--backup` (per-file `.bak`), `--yes`, `--log` carry through.

### Out (YAGNI)
- A non-recursive flag, parallelism, per-file progress bars, glob filters.
- Read commands over directories already exist (`--find`/`--vocab`/`--export`/`--index`).

## Behavior decisions

| Decision | Choice | Why |
|----------|--------|-----|
| Trigger | positional is a directory **and** a write flag present | unambiguous; read-dir commands are handled earlier in `Program` |
| Recursion | recursive (`AllDirectories`) | consistency with existing directory commands |
| Error isolation | catch the same user-error exception set `Program` already maps; report + continue | one unreadable/locked/refused clip must not abort a 500-clip run |
| Exit code | `0` if zero failures, else `2` (write-failure family) | scriptable |
| `--clear-all` | confirm **once** naming the clip count, unless `--yes`/`--dry-run` | destructive across many files |
| Batch-copy | copy `--copy-from` source onto each clip; **skip** the file equal to the source | copying a clip onto itself is a no-op, not a failure |
| Empty dir | message "no .mp4 files found", exit `0` | nothing to do is not an error |

## Architecture

| Unit | Location | Responsibility |
|------|----------|----------------|
| `BatchCommand.Run(files, mutationFor, logger, output?)` | `clipmetascribe/Commands/` | Iterate files; for each, get its mutation (or `null` = skip), apply via `Mp4Writer`, isolate user-error exceptions, tally, print summary, return aggregate exit code. |
| `Program` batch dispatch | `clipmetascribe/` | Detect directory + write op; enumerate files; build the per-file `mutationFor` delegate for the specific op; handle `--clear-all` confirmation once; parse a `--copy-from` source once. |

`mutationFor` is `Func<string, MetadataMutation?>`, a fresh mutation **per file** (so the
writer's in-place normalization/schema-stamp never leaks between files), returning `null` to
skip (batch-copy's source). This keeps `BatchCommand` ignorant of *which* op it runs, it just
applies mutations and isolates failures, while `Program` owns op-specific construction.

Per-op `mutationFor`:
- `--set`/`--append`/`--clear`: `BuildMutation(args, file, dryRun, backup)` (fresh; per-file backup path).
- `--clear-all`: `new MetadataMutation { ClearAll = true, DryRun, BackupPath = backup ? file+".bak" : null }`.
- `--copy-from src` (parsed/validated once, source tree parsed once): `file == src ? null :`
  `MergeExplicit(ClipMetaCopier.BuildCopyMutation(sourceTree), BuildMutation(args, file, …))`.

## Data flow

```
dir + write flag
  └─ Program: enumerate *.mp4 (recursive) → files
              build mutationFor for the op (+ confirm clear-all once, parse copy source once)
              └─ BatchCommand.Run(files, mutationFor, logger)
                   for each file:
                     m = mutationFor(file)            (null → skipped++)
                     try Mp4Writer.WriteMetadata(file, m)   → updated++
                     catch user-error                       → report + failed++
                   print "N updated, M failed, K skipped (T clips)"
                   return failed == 0 ? 0 : 2
```

Each file still goes through the unchanged single-file write-safety chain (temp → verify →
atomic swap). Batch adds only iteration, isolation, and reporting.

## Error handling

- Per-file user errors (`IOException`, `UnsupportedFormatException`, `InvalidDataException`,
  `ArgumentException`, `InvalidOperationException`, `UnauthorizedAccessException`) are caught,
  reported as `FAILED <file>: <message>`, counted, and the run continues.
- A failure in building a file's mutation (e.g. a bad value) is isolated the same way.
- Unexpected (non-user) exceptions are NOT swallowed, they propagate (a real bug should not be
  hidden as a per-file "failure").

## Testing (TDD)

- **`BatchCommand` unit:** all-succeed → updated==count, exit 0; one file whose `mutationFor`
  throws a user-error → isolated (others succeed, exit 2, summary counts); a `null` mutation →
  skipped, not failed.
- **Integration:** batch `--set` over a temp dir of clip copies → every clip gains the field and
  each clip's **media is byte-identical**; a corrupt `.mp4` mixed in → it fails, others succeed,
  exit 2; batch-copy → every dest gains the source's fields and the source is skipped.
- **Dispatch:** directory + `--set` routes to batch; empty dir → exit 0 + message.

## Definition of Done

1. `dotnet build`, 0 warnings / 0 errors.
2. `dotnet test`, all pass incl. new batch unit + integration; media-integrity green.
3. Zero NuGet; CLIs stay thin; the single-file write path is unchanged.
4. `PrintUsage` documents directory write usage; new gotchas → PITFALLS if any.
