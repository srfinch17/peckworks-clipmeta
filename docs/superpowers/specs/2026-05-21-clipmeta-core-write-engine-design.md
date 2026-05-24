# ClipMeta.Core + Write Engine — Design Spec
**Date:** 2026-05-21
**Round:** 2 (clipmetaview complete; this round delivers Core + write capability)
**Author:** Peckworks Lab

---

## Problem Statement

Gamers and other content creators accumulate large libraries of MP4 clips named only by timestamp. Finding specific moments (a rocket jump, a funny death, a highlight play) requires re-watching everything. The goal is a tool that lets you tag clips at the moment of viewing — or at the moment of creation — so that later you can search a 5,000-clip library in seconds and pull exactly the footage you need for an edit.

Metadata must live **inside the MP4 file** so it travels with the file regardless of where it is moved, renamed, or copied. No sidecar files. No databases. No NTFS alternate data streams.

---

## Scope of This Round

**In scope:**
- Extract `ClipMeta.Core` as a standalone C# class library
- Define format-agnostic interfaces (`IMediaParser`, `IMediaWriter`, `IClipMetaLogger`)
- Move all MP4 parsing code from `clipmetaview` into Core
- Implement `Mp4Writer` — safe metadata write engine
- Define the `com.peckworkslab.clipmeta` tag schema
- Implement `clipmetascribe` CLI (replaces "Hello World" stub)
- Structured logging with simple and verbose levels
- Batch operations across entire directories
- Two-directory test clip strategy (pristine + scratch)
- Search index for large libraries

**Out of scope this round:**
- GUI / web app
- MCP server
- `clipsearch` as a standalone tool (search lives in Core; CLI surface is a stub)
- Voice input
- Formats other than MP4 (interfaces defined; only MP4 implemented)

---

## 1. Solution Structure

```
peckworks-clipmeta.slnx
│
├── ClipMeta.Core/                  ← new class library (net10.0, zero NuGet deps)
│   ├── Abstractions/
│   │   ├── IMediaParser.cs
│   │   ├── IMediaWriter.cs
│   │   ├── IClipMetaLogger.cs
│   │   └── MediaHandlerRegistry.cs
│   ├── Mp4/                        ← moved from clipmetaview
│   │   ├── BoxHeader.cs
│   │   ├── FullBoxHeader.cs
│   │   ├── BoxNode.cs
│   │   ├── BigEndianReader.cs
│   │   ├── BigEndianWriter.cs      ← new
│   │   ├── MetadataKeys.cs
│   │   └── Mp4Parser.cs
│   ├── Write/                      ← new
│   │   ├── Mp4Writer.cs
│   │   ├── FreeformAtomWriter.cs
│   │   └── MetadataMutation.cs
│   ├── Schema/                     ← new
│   │   └── ClipMetaSchema.cs
│   ├── Search/                     ← new
│   │   ├── ClipMetaIndex.cs
│   │   └── ClipMetaSearch.cs
│   └── Logging/
│       ├── FileLogger.cs
│       └── NullLogger.cs
│
├── clipmetaview/                   ← thin CLI; references ClipMeta.Core
│   ├── Rendering/
│   │   └── TreeRenderer.cs         ← stays here (view-specific)
│   ├── AppRunner.cs
│   └── Program.cs
│
├── clipmetascribe/                 ← thin CLI; references ClipMeta.Core
│   ├── Commands/
│   │   ├── WriteCommand.cs
│   │   ├── ListCommand.cs
│   │   ├── StatsCommand.cs
│   │   ├── VocabCommand.cs
│   │   ├── IndexCommand.cs
│   │   └── ExportCommand.cs
│   └── Program.cs
│
├── clipmetaview.Tests/             ← existing; update project reference to Core
│
├── clipmetascribe.Tests/           ← new; MSTest, no NuGet
│
└── testclips/
    ├── pristine/                   ← originals; READ ONLY; never written during tests
    └── scratch/                    ← copies regenerated from pristine before each write test run
```

**Key rule:** Both CLIs are thin shells. Business logic lives entirely in `ClipMeta.Core`. A CLI's `Program.cs` parses arguments and calls Core. Nothing else.

---

## 2. Format-Agnostic Interfaces (SOLID)

The architecture is open for extension and closed for modification. Adding MKV support in a future round means implementing two interfaces and registering them — zero changes to existing code.

```csharp
/// <summary>Reads a media file and returns its box/atom tree.</summary>
public interface IMediaParser
{
    /// <summary>Returns true if this parser can handle the given file path.</summary>
    bool CanParse(string filePath);

    /// <summary>Parses the file and returns the root node of its structure tree.</summary>
    MediaNode ParseFile(string filePath);
}

/// <summary>Writes metadata mutations into a media file safely.</summary>
public interface IMediaWriter
{
    /// <summary>Returns true if this writer can handle the given file path.</summary>
    bool CanWrite(string filePath);

    /// <summary>
    /// Applies the mutation to the file using a temp-file strategy.
    /// The original file is never opened for writing; if anything fails the
    /// original is untouched.
    /// </summary>
    void WriteMetadata(string filePath, MetadataMutation mutation, IClipMetaLogger logger);
}

/// <summary>Structured logger for clipmeta operations.</summary>
public interface IClipMetaLogger
{
    LogLevel Level { get; }
    void Log(string message);
    void LogVerbose(string message);   // no-op unless Level == Verbose
}

/// <summary>Selects the correct parser or writer for a given file by extension.</summary>
public sealed class MediaHandlerRegistry
{
    public void RegisterParser(IMediaParser parser);
    public void RegisterWriter(IMediaWriter writer);
    public IMediaParser GetParser(string filePath);   // throws UnsupportedFormatException if none
    public IMediaWriter GetWriter(string filePath);
}
```

`BoxNode` keeps its existing name and all existing fields for this round — renaming it to something more generic is deferred until a second format is actually implemented and the abstraction earns its keep. `IMediaParser.ParseFile()` returns `BoxNode`. All 80 existing clipmetaview tests remain valid.

---

## 3. Tag Schema

### Domain

All custom metadata uses the reverse-domain namespace tied to the company's owned domain `peckworkslab.com`:

```
com.peckworkslab.clipmeta
```

This namespace is written into every `----` freeform atom. It is globally unique, professionally attributed to Peckworks Lab, and appropriate if the tool is used by others.

### Fields

| Field name | Atom | Value format | Example |
|---|---|---|---|
| `schema` | `com.peckworkslab.clipmeta:schema` | Integer string | `"1"` |
| `game` | `com.peckworkslab.clipmeta:game` | Single string | `"Team Fortress 2"` |
| `players` | `com.peckworkslab.clipmeta:players` | Pipe-separated | `"Ben\|Scott"` |
| `tags` | `com.peckworkslab.clipmeta:tags` | Pipe-separated | `"market garden\|funny moment\|high score"` |
| `timecode` | `com.peckworkslab.clipmeta:timecode` | Pipe-separated HH:MM:SS | `"00:00:45\|00:01:23"` |
| `rating` | `com.peckworkslab.clipmeta:rating` | Integer 1–5 | `"4"` |
| `notes` | `com.peckworkslab.clipmeta:notes` | Free UTF-8 text | `"Ben gets the kill not me lol"` |

**Delimiter rationale:** Pipe (`|`) is used rather than comma because commas appear naturally in game titles, player names, and moment descriptions. Comma is never used as a storage delimiter; display layers may render pipes as commas or bullet points for readability.

**Custom fields beyond the schema:** Any field name is valid. The schema defines well-known fields with normalization and first-class CLI support; unknown fields are stored and round-tripped correctly. Example: `--set map "2Fort"` stores `com.peckworkslab.clipmeta:map`.

**Schema version:** Every write stamps `schema = "1"`. Future format changes increment this value; tooling detects and can migrate older files.

### Normalization on write

Every write (set or append) applies:
1. Trim leading/trailing whitespace from each value
2. Lowercase all tag values (for case-insensitive search consistency)
3. Deduplicate pipe-separated lists (preserve order, remove exact duplicates)
4. Canonical timecode format: always store as `HH:MM:SS`; accept `45`, `0:45`, `00:00:45` on input

### `ClipMetaSchema` (C#)

```csharp
public static class ClipMetaSchema
{
    public const string Domain  = "com.peckworkslab.clipmeta";
    public const string Schema  = "schema";
    public const string Game    = "game";
    public const string Players = "players";
    public const string Tags    = "tags";
    public const string Timecode= "timecode";
    public const string Rating  = "rating";
    public const string Notes   = "notes";

    public static string AtomName(string field) => $"{Domain}:{field}";
}
```

---

## 4. Write Engine

### The Golden Rule

The source file is **never opened for writing**. All mutations go to a temp file. If anything fails at any point, the temp file is deleted and the original is untouched.

### Write Pipeline

```
Source file (read-only)
        │
        ▼
Mp4Parser.ParseFile()
  → full MediaNode tree with accurate byte offsets and sizes
  → detect fragmented MP4 → throw UnsupportedFormatException if found
        │
        ▼
Mp4Writer.Write(sourceFilePath, mutation, logger)
  → open source stream (read) + temp stream (write) side by side
  → walk box tree, writing each box to temp:
      Container boxes (moov, udta, meta, ilst):
        recalculate size from children; write corrected header
      Leaf boxes (ftyp, mdat, free, etc.):
        stream-copy bytes directly from source; never load mdat into memory
      New/modified ---- atoms:
        write per FreeformAtomWriter spec below
      stco/co64 boxes (ALL tracks):
        if moov grew by delta AND mdat position follows moov,
        increment every offset entry by delta
        log each adjusted entry at Verbose level
  → append free box padding after ilst (on first write)
        │
        ▼
Mp4Parser.ParseFile(tempFilePath)
  → verify: box count matches, moov present, ilst present, ---- atoms readable
  → verify: stco/co64 values are internally consistent
        │
        ▼
File.Replace(tempFilePath, sourceFilePath, backupPath: null)
  → atomic swap on same filesystem
  → if --backup flag set: File.Replace(temp, source, source + ".bak")
        │
        on any exception at any step:
        → delete temp file
        → log error
        → rethrow with context (file path, step that failed)
```

### Three Write Scenarios

1. **Update existing** — a `----` atom with our domain and field name already exists → replace its `data` child value bytes; recalculate sizes up the tree.

2. **Append to existing ilst** — our atom isn't in ilst yet → write a new `----` atom at the end of ilst; recalculate ilst and all ancestor sizes.

3. **Create from scratch** — ilst (or meta, or udta) doesn't exist → build the chain and insert into moov:
   ```
   udta
   └── meta (FullBox: version=0, flags=0)
       ├── hdlr (FullBox: handler_type="mdir", name="")   ← REQUIRED; QuickTime/Final Cut reject meta without this
       └── ilst
           └── ---- (our atom)
   ```

### `free` Box Padding

On the **first** write to a file that had no existing clipmeta atoms, append a `free` box of 512 bytes immediately after `ilst`. This padding absorbs future metadata additions without shifting `mdat`, eliminating the need to adjust stco/co64 on routine re-tags. When a future write exceeds the available padding, a full rewrite is performed and a new padding block is written.

### FreeformAtomWriter — `----` Atom Structure

```
[4 bytes] size of ---- box (big-endian)
[4 bytes] FourCC: 0x2D2D2D2D  ("----")
  [4 bytes] size of mean box
  [4 bytes] FourCC: "mean"
  [1 byte]  version = 0          ← FullBox prefix; DO NOT omit
  [3 bytes] flags = 0            ← FullBox prefix; DO NOT omit
  [N bytes] domain string (UTF-8, no null terminator): "com.peckworkslab.clipmeta"

  [4 bytes] size of name box
  [4 bytes] FourCC: "name"
  [1 byte]  version = 0          ← FullBox prefix; DO NOT omit
  [3 bytes] flags = 0            ← FullBox prefix; DO NOT omit
  [N bytes] field name string (UTF-8, no null terminator): e.g. "tags"

  [4 bytes] size of data box
  [4 bytes] FourCC: "data"
  [1 byte]  version = 0
  [3 bytes] type indicator: 0x00000001 (UTF-8 text)
  [4 bytes] locale = 0
  [N bytes] value (UTF-8)
```

**Critical:** Both `mean` and `name` are FullBoxes. The 4-byte version+flags prefix is mandatory. Omitting it shifts all subsequent bytes and produces a malformed atom that reads back as garbage.

### stco/co64 Adjustment

- Every `trak` box has its own `stbl → stco` or `stbl → co64`. **All of them must be adjusted** — not just the first. A stereo video with separate video and audio tracks has two stco tables; missing one desynchronises that track.
- Adjustment applies only when `mdat` begins at a byte offset **after** the end of `moov`. If `mdat` precedes `moov`, chunk offsets are unaffected.
- For `stco` (32-bit offsets): if `offset + delta > UInt32.MaxValue`, the write must fail with a clear error. In practice this means the file is approaching 4 GB and should use `co64` already; log a warning if the headroom is under 10%.
- Log every adjusted entry at Verbose level: `stco[trak=1]: 3842 entries += 87 bytes`.

### Fragmented MP4 Detection

Check for presence of `moof` boxes at the top level during parse. If found, abort write with:
```
UnsupportedFormatException: "clip001.mp4 uses fragmented MP4 format (contains moof boxes).
Write is not supported for fragmented files. This format is common with Xbox Game Bar captures."
```

### Preservation of Foreign Atoms

When rewriting `ilst`, all existing atoms whose domain is **not** `com.peckworkslab.clipmeta` are copied byte-for-byte into the new ilst, in their original order, before our atoms are appended. This preserves iTunes atoms (`©nam`, `©ART`, etc.) and third-party `----` atoms (HandBrake, FFmpeg, etc.) exactly.

### File Lock Detection

Before opening the source file for read, attempt to open it with `FileShare.None`. If this throws `IOException`, surface a friendly message:
```
"clip001.mp4 is currently open by another process (likely a video player).
Close the file and try again."
```

### Orphaned Temp File Cleanup

On startup, `clipmetascribe` scans the working directory for files matching `*.mp4.tmp`. If any are found, it warns:
```
"Warning: found 2 orphaned temp file(s) from a previous interrupted write:
  clip003.mp4.tmp
  clip007.mp4.tmp
These can be safely deleted."
```
It does not delete them automatically — the user confirms.

### `--set field ""` Semantics

Setting any field to an empty string is treated as a delete of that atom. An empty value stored in the file is noise; there is no distinction between "empty" and "absent."

---

## 5. `MetadataMutation`

```csharp
/// <summary>Describes a set of metadata changes to apply atomically to one file.</summary>
public sealed class MetadataMutation
{
    /// <summary>Fields to set (or delete if value is null/empty).</summary>
    public Dictionary<string, string?> SetFields { get; } = new();

    /// <summary>Fields to append values to (pipe-delimited; deduplicates on write).</summary>
    public Dictionary<string, string> AppendFields { get; } = new();

    /// <summary>Field names to delete entirely.</summary>
    public HashSet<string> DeleteFields { get; } = new();

    /// <summary>If true, remove ALL com.peckworkslab.clipmeta atoms from the file.</summary>
    public bool ClearAll { get; init; }

    /// <summary>If true, log what would change without writing anything.</summary>
    public bool DryRun { get; init; }
}
```

---

## 6. Logging

### Interface

```csharp
public enum LogLevel { Simple, Verbose }

public interface IClipMetaLogger
{
    LogLevel Level { get; }
    void Log(string message);
    void LogVerbose(string message);
}
```

### FileLogger

- **Default location:** same directory as the file being processed, named `clipmeta.log`
- **Override:** `--log "C:\path\to\custom.log"`
- **Rotation:** max 3 files, 10 MB each; oldest deleted when limit is reached
- **Both levels write to the same file**; verbose entries are additionally prefixed with `[V]`

### Log entry format

```
# Simple (default):
[2026-05-21 14:32:01] WRITE  clip001.mp4  tags+="market garden|funny moment"  OK  231ms
[2026-05-21 14:32:01] ERROR  clip002.mp4  file is open by another process

# Verbose adds (prefixed [V]):
[2026-05-21 14:32:01] [V] PARSE  14 boxes  moov@0x00000020  mdat@0x0000B4AF
[2026-05-21 14:32:01] [V] WRITE  scenario=append  delta=+87 bytes
[2026-05-21 14:32:01] [V] STCO   trak=1  3842 entries += 87
[2026-05-21 14:32:01] [V] STCO   trak=2  3842 entries += 87
[2026-05-21 14:32:01] [V] VERIFY temp file re-parsed OK  14 boxes intact
[2026-05-21 14:32:01] [V] SWAP   clip001.mp4 ← clip001.mp4.tmp
```

### What Simple logs

Every operation: file path, operation type, fields changed, success/failure, duration. All errors at any verbosity level.

### What Verbose adds

Every box encountered during parse; byte offsets of moov and mdat; write scenario (update/append/create); size delta; every stco/co64 entry adjusted; temp file path; verification result; swap confirmation.

---

## 7. `clipmetascribe` CLI

### Single-file operations

```bash
# Read
clipmetascribe "clip001.mp4" --list
clipmetascribe "clip001.mp4" --list --output json

# Write (set replaces; append adds to existing list)
clipmetascribe "clip001.mp4" --set tags "market garden|funny moment"
clipmetascribe "clip001.mp4" --append tags "high score"
clipmetascribe "clip001.mp4" --set game "Team Fortress 2"
clipmetascribe "clip001.mp4" --set timecode "00:00:45"
clipmetascribe "clip001.mp4" --set rating "4"
clipmetascribe "clip001.mp4" --set notes "Ben gets the kill not me lol"

# Delete
clipmetascribe "clip001.mp4" --clear tags
clipmetascribe "clip001.mp4" --clear-all           # prompts: "Type YES to confirm"
clipmetascribe "clip001.mp4" --clear-all --yes      # skip prompt (for scripting)

# Copy tags between files
clipmetascribe --copy-tags "source.mp4" "dest.mp4"
```

### Batch operations (directory)

```bash
clipmetascribe --dir ".\clips" --set game "Team Fortress 2"
clipmetascribe --dir ".\clips" --recursive --set game "Team Fortress 2"
clipmetascribe --dir ".\clips" --append tags "rocket jump"
clipmetascribe --dir ".\clips" --clear-all --yes
clipmetascribe --dir ".\clips" --untagged            # list files with no clipmeta fields
clipmetascribe --dir ".\clips" --untagged game        # list files missing specifically 'game'
```

### Dry run (safe preview of any operation)

```bash
clipmetascribe --dir ".\clips" --clear-all --dry-run
# Output: "DRY RUN — no files will be modified"
#         "[would clear] .\clips\clip001.mp4"
#         "[would clear] .\clips\clip002.mp4"  ... etc.
```

### Reporting

```bash
clipmetascribe --dir ".\clips" --stats
# Output:
#   1,247 clips total
#   423 tagged  /  824 untagged
#   Top tags:  market garden (47)  funny moment (38)  headshot (31)
#   Games:     Team Fortress 2 (1,247)

clipmetascribe --dir ".\clips" --vocab tags        # all unique tag values across directory
clipmetascribe --dir ".\clips" --vocab game        # all unique game values

clipmetascribe --dir ".\clips" --export "library.csv"   # one row per file, all fields as columns
```

### Search (basic, in Core; full clipsearch is next round)

```bash
clipmetascribe --dir ".\clips" --find tags "market garden"
clipmetascribe --dir ".\clips" --find game "Team Fortress 2" --find tags "headshot"
clipmetascribe --dir ".\clips" --find tags "market garden" --since 2026-01-01
clipmetascribe --dir ".\clips" --find tags "market garden" --before 2026-03-01
# Multiple --find flags are AND logic: all conditions must match
# Output: one absolute file path per line (pipeable to xcopy, robocopy, etc.)
```

### Index (for large libraries)

```bash
clipmetascribe --dir ".\clips" --index          # build or rebuild clipmeta-index.json
clipmetascribe --dir ".\clips" --index --watch  # stub: print notification when new .mp4 appears
```

Search checks for `clipmeta-index.json` first; falls back to full scan. Index entries include file path, last-modified time, and all clipmeta field values. Stale detection: compare recorded mtime against current filesystem mtime per file.

### Safety and output flags

```bash
--dry-run           # preview without writing
--backup            # keep .bak copy before any write
--verbose           # verbose logging
--log "path"        # custom log file location
--output json       # machine-readable output (for MCP/GUI)
--output text       # default human-readable
--yes               # skip confirmation prompts (scripting)
--version           # print "clipmetascribe 1.0.0 (ClipMeta.Core 1.0.0)"
```

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Invalid arguments / file not found / unsupported format |
| 2 | Write failure (original file intact) |
| 3 | Verification failure (temp file did not round-trip; original file intact) |

---

## 8. Search Index

### `clipmeta-index.json` (per directory)

```json
{
  "schema": 1,
  "generated": "2026-05-21T14:32:01Z",
  "entries": [
    {
      "file": "clip001.mp4",
      "mtime": "2026-05-20T10:00:00Z",
      "game": "Team Fortress 2",
      "players": ["Ben", "Scott"],
      "tags": ["market garden", "funny moment"],
      "timecode": ["00:00:45"],
      "rating": 4,
      "notes": "Ben gets the kill not me lol"
    }
  ]
}
```

Multi-value fields (players, tags, timecode) are stored as JSON arrays in the index for fast intersection queries. The index is the only place JSON appears — MP4 files store pipe-delimited strings.

---

## 9. Test Strategy

### Two-directory discipline

- `testclips/pristine/` — originals; never opened for writing; checked in; the ground truth
- `testclips/scratch/` — regenerated by the test runner at the start of every write-test session by copying from pristine; any test that writes a file uses the scratch copy

```csharp
internal static class ScratchClips
{
    public static string Prepare(string pristineFilePath)
    {
        // Copy pristine file to scratch/ and return the scratch path
        string scratch = Path.Combine(
            Path.GetDirectoryName(pristineFilePath)!
                .Replace("pristine", "scratch"),
            Path.GetFileName(pristineFilePath));
        File.Copy(pristineFilePath, scratch, overwrite: true);
        return scratch;
    }
}
```

### Required test coverage for `clipmetascribe.Tests`

**`BigEndianWriterTests`**
- Write uint16/uint32/uint64 → read back with BigEndianReader → values match
- Write FourCC → read back → string matches

**`FreeformAtomWriterTests`**
- Written atom parses back with correct domain, field name, and value
- `mean` and `name` boxes each have correct FullBox 4-byte prefix
- Value round-trips correctly for all known field types

**`Mp4WriterTests` (unit)**
- Write to a MemoryStream (mocked source) → re-parse → correct atoms present
- size==1 extended box sources pass through correctly
- `free` padding box is written on first clipmeta write
- Foreign atoms (`©nam`, third-party `----`) are preserved byte-for-byte

**`Mp4WriterIntegrationTests` (uses scratch clips)**
- All three scenarios: update existing, append to ilst, create from scratch
- After write: `Mp4Parser.ParseFile(scratchPath)` succeeds, atom readable
- After write: `VLC --play-and-exit scratchPath` exits 0 (if VLC available on PATH; skip if not)
- stco/co64 values after write equal pre-write values + measured delta
- All tracks (video + audio) have adjusted offsets
- Write to moov-before-mdat file: offsets adjusted correctly
- Write to mdat-before-moov file: offsets unchanged
- Fragmented MP4 → `UnsupportedFormatException` thrown, source file unchanged
- File locked by another process → friendly `IOException` message, source unchanged
- `--dry-run` → source file byte-for-byte identical before and after

**`BatchOperationTests`**
- `--set` on directory: all files gain the field; files that already had it are updated
- `--append` on directory: existing tags preserved; new tag appended; no duplicates
- `--clear-all` without `--yes`: no files modified
- `--clear-all --yes`: all clipmeta atoms removed from all files
- `--untagged`: returns only files missing all clipmeta fields
- Progress count reaches total file count

**`SearchIndexTests`**
- `--index` on scratch directory: `clipmeta-index.json` written with correct entries
- Stale detection: modify a scratch file's mtime → index reports it as stale
- `--find` with index present: returns correct paths; does not open MP4 files
- `--find` without index: falls back to scan; returns same results

**`NormalizationTests`**
- "Market Garden" stored as "market garden"
- " market garden " (extra spaces) stored as "market garden"
- Appending "market garden" when already present → stored once, not twice
- "0:45" timecode input → stored as "00:00:45"
- "45" timecode input → stored as "00:00:45"
- `--set tags ""` → atom deleted

---

## 10. Critical Implementation Notes

These are the things that will corrupt files silently if missed. Each should have a corresponding test.

| # | Risk | Mitigation |
|---|---|---|
| 1 | Fragmented MP4 (moof boxes) | Detect on parse; refuse write with clear error |
| 2 | Only one stco/co64 adjusted (others missed) | Walk ALL trak → stbl → stco/co64; integration test checks both tracks |
| 3 | `mean`/`name` FullBox prefix omitted | Unit test verifies parsed atom structure byte-by-byte |
| 4 | `hdlr` missing when creating meta from scratch | Scenario 3 test uses a file with no existing udta/meta/ilst |
| 5 | Foreign ilst atoms corrupted | Integration test verifies `©nam` value unchanged after write |
| 6 | stco adjusted when mdat precedes moov | Integration test with mdat-first file; offsets must not change |
| 7 | `co64` values exceed 32-bit boundary undetected | Log warning if post-adjustment value approaches UInt32.MaxValue |
| 8 | Temp file not deleted on exception | Verify in test: temp file absent after forced exception |

---

## 11. Definition of Done

This round is complete when:

1. `dotnet build` — zero errors, zero warnings across all projects
2. `dotnet test` — all tests pass including integration tests against scratch clips
3. Round-trip verified: write tags → read back with `clipmetaview` → tags visible in tree and summary
4. VLC plays each scratch clip correctly after write (no audio/video desync)
5. `--clear-all` requires explicit confirmation or `--yes` flag
6. Fragmented MP4 is refused with a helpful error message
7. Log file written at both verbosity levels; entries match the format in this spec
8. `clipmetaview` still builds and all 80 existing tests still pass after moving Mp4/ to Core
9. Zero NuGet packages added to any project
10. `clipmetascribe --version` prints correct version string

---

## 12. Future Rounds (not in scope, recorded for continuity)

- **Round 3:** MCP server — exposes Core as Claude-callable tools; voice → tag workflow; file watcher for new clip detection
- **Round 4:** Web GUI — browser-based viewer (tree + summary), search UI with game/tag/player dropdowns and checkboxes, clip timeline showing timecodes
- **Round 5:** `clipsearch` as a standalone CLI — output pipeable to robocopy/xcopy for building edit folders
- **Round 6:** Additional format support — MKV, MOV (implement `IMediaParser`/`IMediaWriter`, register; Core unchanged)
