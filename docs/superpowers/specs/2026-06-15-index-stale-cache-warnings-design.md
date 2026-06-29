# Index stale-cache warnings, Design Spec

**Date:** 2026-06-15
**Status:** Approved (brainstorming) → implementing

---

## Problem

`ClipMetaIndex` records each clip's `FileSizeBytes` and `LastModified` at build time, but
`--index-search` never compares them against the files on disk. A clip that was retagged,
replaced, or deleted after the index was built still shows up with its **old** metadata and no
indication the result is out of date. The stored size/mtime exist precisely to detect this, 
they were just never used.

## Scope

### In
- A Core check that classifies one index entry against the current filesystem.
- `--index-search` annotates each stale result and prints a footer warning naming how many
  results are stale, when the index was built, and how to refresh.

### Out (YAGNI)
- Auto-refresh, a standalone `--index --check` audit of the whole library, content hashing.
- Whole-library staleness scanning, the check is scoped to the results actually shown.

## Design

### Core
```csharp
namespace ClipMetaCore.Read;
public enum StaleReason { Modified, Missing }
// on ClipMetaIndex:
public static StaleReason? CheckEntry(IndexEntry entry);   // null == current/unchanged
```
Rules:
- `!File.Exists(entry.FilePath)` → `Missing`.
- `new FileInfo(path).Length != entry.FileSizeBytes` → `Modified`.
- current `LastWriteTimeUtc` ≠ `entry.LastModified`, compared at **1-second precision**
  (`ToUnixTimeSeconds`) → `Modified`. Second precision matches how the index serializes/parses
  the timestamp and dodges tick-rounding false positives; a real edit moves mtime by far more.
- otherwise `null`.

### CLI (`IndexSearchCommand`)
After `ClipMetaSearch.Find`, for each match call `CheckEntry`, append a marker, and tally:

```
  clipB.mp4  [changed since index]
  clipC.mp4  [missing, file no longer exists]
```
Then, if any match is stale, a footer:
```
Warning: N result(s) changed or were removed since the index was built (YYYY-MM-DD HH:mm UTC). Run --index to refresh.
```
The check is **scoped to matched results**, the warning is about what the user is looking at,
and it costs one `FileInfo` per match. Staleness is **advisory**: it prints with the results and
**the exit code stays 0**.

## Data flow

```
--index-search → ReadFromFile → ClipMetaSearch.Find → matches
   for each match: ClipMetaIndex.CheckEntry(match) → null | Modified | Missing
                   print "  <relative>[ marker]"
   if any stale → footer warning (count + index.Built + "Run --index to refresh")
   return 0
```

## Error handling

- `CheckEntry` only reads file metadata (`File.Exists`, `FileInfo.Length`, `LastWriteTimeUtc`);
  if a `FileInfo` read throws `IOException`/`UnauthorizedAccessException` for a path that exists,
  treat it as `Modified` (we can't confirm it's current) rather than letting the search crash.
- No change to the "no index found" (exit 1) or "read error" (exit 2) paths.

## Testing (TDD)

- **Core `CheckEntry` (clip-less, temp files):** unchanged file → `null`; size differs →
  `Modified`; mtime moved → `Modified`; file deleted → `Missing`.
- **Command (`IndexSearchCommand`, clip-less):** a hand-built index whose entry points at a temp
  file with a *wrong* recorded size → search shows the `[changed since index]` marker and the
  footer warning; an entry recorded to match the file exactly → no marker, no warning.

## Definition of Done

1. `dotnet build`, 0 warnings / 0 errors.
2. `dotnet test`, all pass incl. new Core + command tests.
3. Zero NuGet; CLI stays thin; exit codes unchanged (staleness is advisory).
4. Public `CheckEntry`/`StaleReason` documented.
