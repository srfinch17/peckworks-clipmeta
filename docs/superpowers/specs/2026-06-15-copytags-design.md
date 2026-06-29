# CopyTags, Design Spec

**Date:** 2026-06-15
**Status:** Approved (brainstorming) → implementing
**Feature:** 1 of 3 deferred (CopyTags → atomic index-write → batch operations)

---

## Problem

There is no way to copy clipmeta metadata from one clip to another. A user who has
tagged one clip and wants the same `game`/`players`/`tags`/etc. on a sibling clip
must retype every field. CopyTags adds a single read-source → write-dest command
built entirely on the existing, mature read and write engines.

## Scope

### In
- New CLI command: `clipmetascribe "dest.mp4" --copy-from "source.mp4"`.
- Core helper that turns a parsed source tree into a `MetadataMutation`.
- Merge semantics: source user fields are `--set` onto dest; dest's other fields survive.
- Composes with existing `--dry-run`, `--backup`, `--log`, and explicit `--set`/`--append`/`--clear`.

### Out (YAGNI for v1)
- Field selection (`--fields game,tags`).
- `--replace` (exact mirror); achievable today via `--clear-all` first.
- Copying into a whole folder, that is the **batch** feature (feature 3), where
  batch-copy will reuse this command's Core helper.

## Semantics, merge

Every clipmeta **user** field on the source becomes a `SetFields` entry for the dest.
Dest fields the source does not carry are left untouched. Example: source has
`game`,`tags`; dest has `rating` → result has `game`,`tags` (from source) and `rating`
(preserved). This matches the project's safety-first ethos: a copy never silently
destroys existing metadata. The internal `schema` field is never copied
(`ClipMetaReader.GetUserFields` already excludes it); the dest receives its own schema
stamp through the normal value-storing write path.

Pipe-delimited multi-value fields (`tags`,`players`,`timecode`) copy as whole values, 
a per-field set replaces the dest's value for that field.

## Architecture

Thin and reuse-first; no change to the parser or writer.

| Unit | Location | Responsibility | Depends on |
|------|----------|----------------|------------|
| `ClipMetaCopier.BuildCopyMutation(BoxNode source)` | `clipmeta.core/Read/` | Pure: source tree → `MetadataMutation` whose `SetFields` are the source's user fields (domain-qualified atom names). No IO. | `ClipMetaReader.GetUserFields`, `ClipMetaSchema.AtomName`, `MetadataMutation` |
| `CopyTagsCommand.Run(dest, source, dryRun, backupPath, extraMutation?, logger)` | `clipmetascribe/Commands/` | Parse source, build mutation (+ layer explicit ops), validate, delegate to the write-safety chain. | `Mp4Parser`, `ClipMetaCopier`, `Mp4Writer`/`WriteCommand` |
| `Program.cs` dispatch | `clipmetascribe/` | One branch for `--copy-from`; add `--copy-from` to `KnownFlags`; read its value with the existing `RequireArg`-style guard. | existing arg helpers |

Keeping `BuildCopyMutation` in Core (no IO) makes the read→mutation logic unit-testable
without the CLI and keeps the CLI a thin shell, per the project conventions.

## Data flow

```
source.mp4 ──Mp4Parser.ParseFile──▶ BoxNode
                                      │ ClipMetaCopier.BuildCopyMutation
                                      ▼
                          MetadataMutation { SetFields: source user fields }
                                      │ (+ explicit --set/--append/--clear layered on)
                                      ▼
dest.mp4 ──Mp4Writer.WriteMetadata──▶ temp → re-parse verify → atomic File.Replace
```

Source is opened read-only. Dest goes through the unchanged, proven write-safety chain.

## Error handling / edge cases

| Case | Behavior | Exit |
|------|----------|------|
| `--copy-from` missing its value (or value is a known flag) | arg error via existing `RequireArg` guard | 1 |
| source missing / not `.mp4` / unparseable | clear error; dest untouched | 1 |
| source has no clipmeta user fields | print "source has no clipmeta fields to copy"; **no write**; dest untouched | 0 |
| source path == dest path | reject ("source and destination are the same file"); no write | 1 |
| dest write / verification failure | existing write-safety messages | 2 / 3 |

## Testing (TDD)

- **Core unit (`ClipMetaCopier`):** all user fields incl. custom names become `SetFields`
  with domain-qualified keys; internal `schema` excluded; a source with no user fields
  yields an empty mutation.
- **Integration over the pristine corpus:** copy A→B; assert B gains A's fields; a
  pre-existing non-overlapping field on B survives; and **media is byte-identical**
  after the copy (`MediaIntegrityScanner`). Use `ScratchClips` copies.
- **Arg parsing:** `--copy-from` dispatch; missing source; source==dest; non-`.mp4` source.

## Definition of Done

1. `dotnet build`, 0 warnings / 0 errors.
2. `dotnet test`, all pass, including new Core + integration + arg tests; media-integrity green.
3. Zero NuGet added; CLI stays a thin shell; `BuildCopyMutation` is pure (no IO).
4. `--copy-from` documented in `PrintUsage`; public types XML-documented; any new gotcha → PITFALLS.
