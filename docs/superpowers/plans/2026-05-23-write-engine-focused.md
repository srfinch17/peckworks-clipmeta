# Write Engine — Focused Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract ClipMeta.Core and implement a safe, thoroughly-tested MP4 write engine, then deliver a minimal `clipmetascribe write` CLI — leaving reporting, search, and batch features for a future round.

**Architecture:** ClipMeta.Core is a zero-NuGet-dependency class library holding all parsing and writing logic. Both CLIs (clipmetaview, clipmetascribe) are thin shells referencing Core. The write engine uses a temp-file strategy: the source file is never opened for writing; if anything fails the original is untouched.

**Tech Stack:** C# / .NET 10, MSTest 4.x, zero external NuGet packages (MSTest SDK counts as dev dependency only)

---

## Scope

**This plan implements:**
- ClipMeta.Core class library (extracts Mp4/ code from clipmetaview)
- BigEndianWriter, FreeformAtomWriter, Normalizer, FileLogger, Mp4Writer
- clipmetascribe CLI with **write operations only**: `--set`, `--append`, `--clear`, `--clear-all`

**Explicitly deferred to a future plan:**
- `list`, `stats`, `vocab`, `find`, `export`, `index` commands
- Search index (ClipMetaIndex, ClipMetaSearch)
- Batch directory operations (BatchOperation)
- CopyTags command

---

## Tasks 1–10: Foundation and Write Engine

Implement Tasks 1 through 10 **verbatim** from the reviewed plan at:

`docs/superpowers/plans/2026-05-21-clipmeta-core-write-engine.md`

**Modifications and exceptions — read carefully before starting:**

1. **Task 5 test project scope:** The `.csproj`, `ScratchClips.cs`, `TestClipsLocator.cs`, and `MinimalMp4Builder.cs` are all needed. The following test class files ARE in scope for Task 5: `BigEndianWriterTests.cs`, `FreeformAtomWriterTests.cs`, `FileLoggerTests.cs`, `Mp4WriterTests.cs`, `Mp4WriterIntegrationTests.cs`, `NormalizationTests.cs`. **Do NOT create** `SearchIndexTests.cs` or `BatchOperationTests.cs` — those belong to a future round.

2. **Task 11 (Search Index) in the existing plan:** **Skip entirely.**

3. **Task 12 (Batch Operations) in the existing plan:** **Skip entirely.**

4. **Task 13 (full CLI) in the existing plan:** **Skip entirely.** The write command is implemented as Task 11 of **this** plan.

**After Tasks 1–10 complete, verify:**
```powershell
dotnet build
dotnet test
```
Expected: full solution builds with 0 errors / 0 warnings. All 80 original clipmetaview.Tests pass. All new clipmetascribe.Tests pass.

---

## Task 11: clipmetascribe Write Command

**Files:**
- Modify: `clipmetascribe/clipmetascribe.csproj`
- Modify: `clipmetascribe/Program.cs`
- Create: `clipmetascribe/Commands/WriteCommand.cs`
- Modify: `ClipMeta.Core/Write/MetadataMutation.cs` (add `BackupPath`)
- Modify: `ClipMeta.Core/Write/Mp4Writer.cs` (use `BackupPath` in File.Replace)

### Supported CLI surface

```
clipmetascribe "clip.mp4" --set game "Team Fortress 2"
clipmetascribe "clip.mp4" --set tags "rocket jump|headshot"
clipmetascribe "clip.mp4" --append tags "market garden"
clipmetascribe "clip.mp4" --clear tags
clipmetascribe "clip.mp4" --clear-all             # prompts: "Type YES to confirm"
clipmetascribe "clip.mp4" --clear-all --yes       # skip prompt (for scripting)

# Multiple operations in one call:
clipmetascribe "clip.mp4" --set game "TF2" --append tags "headshot"

# Optional flags (any command):
  --dry-run      Preview what would change; don't write
  --backup       Keep .bak copy of original before atomic swap
  --verbose      Verbose logging to log file
  --log "path"   Write log to file at path
  --yes          Skip confirmation prompts
  --version      Print version string, exit 0
```

### Field name convention
Field names are entered as bare names (`game`, `tags`, `rating`). Program.cs prepends `com.peckworkslab.clipmeta:` to produce the full atom key before adding to `MetadataMutation`.

### Exit codes
| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Bad args / file not found / unsupported format |
| 2 | Write failure (original intact) |
| 3 | Verification failure (temp file did not round-trip; original intact) |

---

- [ ] **Step 11.1: Add BackupPath to MetadataMutation**

In `ClipMeta.Core/Write/MetadataMutation.cs`, add one property to the class:

```csharp
/// <summary>
/// When non-null, File.Replace will save the original file here before swapping.
/// Set by callers that pass --backup; null means no backup.
/// </summary>
public string? BackupPath { get; set; }
```

The full file after the addition:

```csharp
namespace ClipMeta.Core.Write;

/// <summary>Describes a set of metadata changes to apply atomically to one file.</summary>
public sealed class MetadataMutation
{
    /// <summary>Fields to set (or delete when value is null or empty string).</summary>
    public Dictionary<string, string?> SetFields { get; } = new();

    /// <summary>Fields to append values to; deduplicates pipe-delimited lists on write.</summary>
    public Dictionary<string, string> AppendFields { get; } = new();

    /// <summary>Field names to delete entirely.</summary>
    public HashSet<string> DeleteFields { get; } = new();

    /// <summary>When true, remove ALL com.peckworkslab.clipmeta atoms from the file.</summary>
    public bool ClearAll { get; set; }

    /// <summary>When true, log what would change without writing anything.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// When non-null, File.Replace will save the original file here before swapping.
    /// Set by callers that pass --backup; null means no backup.
    /// </summary>
    public string? BackupPath { get; set; }
}
```

- [ ] **Step 11.2: Wire BackupPath into Mp4Writer**

In `ClipMeta.Core/Write/Mp4Writer.cs`, find this line in `WriteMetadata`:

```csharp
File.Replace(tempPath, filePath, destinationBackupFileName: null);
```

Change it to:

```csharp
File.Replace(tempPath, filePath, destinationBackupFileName: mutation.BackupPath);
```

No other changes needed in Mp4Writer.

- [ ] **Step 11.3: Build ClipMeta.Core to verify the change compiles**

```powershell
dotnet build ClipMeta.Core/ClipMeta.Core.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 11.4: Update clipmetascribe.csproj**

`clipmetascribe/clipmetascribe.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AssemblyVersion>1.0.0</AssemblyVersion>
    <InformationalVersion>1.0.0</InformationalVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ClipMeta.Core\ClipMeta.Core.csproj" />
  </ItemGroup>
</Project>
```

Create the Commands directory:
```powershell
New-Item -ItemType Directory -Path "clipmetascribe/Commands"
```

- [ ] **Step 11.5: Implement WriteCommand.cs**

`clipmetascribe/Commands/WriteCommand.cs`:
```csharp
using ClipMeta.Core.Abstractions;
using ClipMeta.Core.Write;

namespace ClipMetaScribe.Commands;

/// <summary>Handles all metadata write operations for a single file.</summary>
internal static class WriteCommand
{
    /// <summary>Applies the mutation to the file. Returns exit code 0 on success.</summary>
    internal static int Run(string filePath, MetadataMutation mutation, IClipMetaLogger logger)
    {
        new Mp4Writer().WriteMetadata(filePath, mutation, logger);
        return 0;
    }

    /// <summary>
    /// Removes all com.peckworkslab.clipmeta atoms from the file.
    /// Requires explicit --yes or interactive confirmation.
    /// Returns exit code 0 on success or user-cancelled.
    /// </summary>
    internal static int RunClearAll(string filePath, bool dryRun, bool yes, IClipMetaLogger logger)
    {
        if (!yes && !dryRun)
        {
            Console.Write($"This will remove ALL clipmeta metadata from '{Path.GetFileName(filePath)}'. Type YES to confirm: ");
            string? input = Console.ReadLine();
            if (input?.Trim() != "YES")
            {
                Console.WriteLine("Cancelled.");
                return 0;
            }
        }

        var mutation = new MetadataMutation { ClearAll = true, DryRun = dryRun };
        new Mp4Writer().WriteMetadata(filePath, mutation, logger);
        return 0;
    }
}
```

- [ ] **Step 11.6: Implement Program.cs**

`clipmetascribe/Program.cs`:
```csharp
using ClipMeta.Core;
using ClipMeta.Core.Abstractions;
using ClipMeta.Core.Logging;
using ClipMeta.Core.Schema;
using ClipMeta.Core.Write;
using ClipMetaScribe.Commands;

namespace ClipMetaScribe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        if (args[0] == "--version")
        {
            Console.WriteLine("clipmetascribe 1.0.0 (ClipMeta.Core 1.0.0)");
            return 0;
        }

        bool verbose = args.Contains("--verbose");
        bool dryRun = args.Contains("--dry-run");
        bool yes = args.Contains("--yes");
        bool backup = args.Contains("--backup");
        string? logPath = GetFlag(args, "--log");

        IClipMetaLogger logger = logPath != null
            ? new FileLogger(logPath, verbose ? LogLevel.Verbose : LogLevel.Simple)
            : NullLogger.Instance;

        // First non-flag argument is the file path
        string? filePath = args.FirstOrDefault(
            a => !a.StartsWith("--") && (File.Exists(a) || Path.HasExtension(a)));

        if (filePath == null || !File.Exists(filePath))
        {
            if (filePath != null && Path.HasExtension(filePath))
            {
                Console.Error.WriteLine($"Error: File not found: {filePath}");
                return 1;
            }
            PrintUsage();
            return 1;
        }

        if (!Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Error: Only .mp4 files are supported: {filePath}");
            return 1;
        }

        try
        {
            if (ContainsFlag(args, "--clear-all"))
            {
                return WriteCommand.RunClearAll(filePath, dryRun, yes, logger);
            }

            var mutation = BuildMutation(args, filePath, dryRun, backup);

            if (mutation.SetFields.Count > 0 || mutation.AppendFields.Count > 0 || mutation.DeleteFields.Count > 0)
            {
                return WriteCommand.Run(filePath, mutation, logger);
            }

            Console.Error.WriteLine("Error: No write operation specified. Use --set, --append, --clear, or --clear-all.");
            PrintUsage();
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (UnsupportedFormatException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Verification failed: {ex.Message}");
            return 3;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static MetadataMutation BuildMutation(string[] args, string filePath, bool dryRun, bool backup)
    {
        var mutation = new MetadataMutation
        {
            DryRun = dryRun,
            BackupPath = backup ? filePath + ".bak" : null,
        };

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--set" && i + 2 < args.Length)
            {
                string field = ClipMetaSchema.AtomName(args[i + 1]);
                mutation.SetFields[field] = args[i + 2];
                i += 2;
            }
            else if (args[i] == "--append" && i + 2 < args.Length)
            {
                string field = ClipMetaSchema.AtomName(args[i + 1]);
                mutation.AppendFields[field] = args[i + 2];
                i += 2;
            }
            else if (args[i] == "--clear" && i + 1 < args.Length)
            {
                string field = ClipMetaSchema.AtomName(args[i + 1]);
                mutation.DeleteFields.Add(field);
                i += 1;
            }
        }

        return mutation;
    }

    private static bool ContainsFlag(string[] args, string flag)
        => Array.Exists(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetFlag(string[] args, string flag)
    {
        int idx = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            clipmetascribe — MP4 metadata writer (Peckworks Lab)

            Usage:
              clipmetascribe "clip.mp4" --set <field> <value>
              clipmetascribe "clip.mp4" --append <field> <value>
              clipmetascribe "clip.mp4" --clear <field>
              clipmetascribe "clip.mp4" --clear-all [--yes]

            Fields:  game  players  tags  timecode  rating  notes  (or any custom name)

            Examples:
              clipmetascribe "clip.mp4" --set game "Team Fortress 2"
              clipmetascribe "clip.mp4" --set tags "rocket jump|headshot"
              clipmetascribe "clip.mp4" --append tags "market garden"
              clipmetascribe "clip.mp4" --clear tags
              clipmetascribe "clip.mp4" --clear-all --yes
              clipmetascribe "clip.mp4" --set game "TF2" --append tags "headshot" --set rating "4"

            Options:
              --dry-run      Preview changes without writing
              --backup       Keep .bak copy of original before write
              --verbose      Verbose logging (requires --log)
              --log <path>   Write structured log to file
              --yes          Skip confirmation prompts
              --version      Print version and exit

            Exit codes:  0=success  1=bad args / not found  2=write failure  3=verification failure
            """);
    }
}
```

- [ ] **Step 11.7: Build clipmetascribe**

```powershell
dotnet build clipmetascribe/clipmetascribe.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 11.8: Smoke test — no args shows usage, exits 1**

```powershell
dotnet run --project clipmetascribe
$LASTEXITCODE  # should be 1
```
Expected: usage text is printed, exit code 1.

- [ ] **Step 11.9: Smoke test — --version**

```powershell
dotnet run --project clipmetascribe -- --version
$LASTEXITCODE  # should be 0
```
Expected: `clipmetascribe 1.0.0 (ClipMeta.Core 1.0.0)`, exit code 0.

- [ ] **Step 11.10: Smoke test — missing file exits 1**

```powershell
dotnet run --project clipmetascribe -- "nonexistent.mp4" --set game "TF2"
$LASTEXITCODE  # should be 1
```
Expected: `Error: File not found: nonexistent.mp4`, exit code 1.

- [ ] **Step 11.11: Smoke test — write a field, verify with clipmetaview**

```powershell
# Copy a pristine clip to scratch
$clip = (Get-ChildItem testclips/pristine/*.mp4 | Select-Object -First 1).FullName
$scratch = "testclips/scratch/smoke_test.mp4"
Copy-Item $clip $scratch -Force

# Write fields
dotnet run --project clipmetascribe -- $scratch --set game "Team Fortress 2" --set tags "rocket jump|headshot" --set rating "4"
$LASTEXITCODE  # should be 0
```
Expected: exits 0 with no error output.

```powershell
# Verify via clipmetaview
dotnet run --project clipmetaview -- $scratch
```
Expected: tree shows `----` atoms for game, tags, rating marked `← [EDITABLE]` with the correct values.

- [ ] **Step 11.12: Smoke test — append to existing tag list**

```powershell
dotnet run --project clipmetascribe -- $scratch --append tags "market garden"
dotnet run --project clipmetaview -- $scratch
```
Expected: tags value is `"rocket jump|market garden|headshot"` (rocket jump and headshot from previous write, market garden appended, deduplication preserves order).

Note: the actual order may be `"rocket jump|headshot|market garden"` depending on normalization — what matters is all three tokens are present exactly once.

- [ ] **Step 11.13: Smoke test — clear a field**

```powershell
dotnet run --project clipmetascribe -- $scratch --clear rating
dotnet run --project clipmetaview -- $scratch
```
Expected: `rating` atom is no longer present in the tree.

- [ ] **Step 11.14: Smoke test — dry-run makes no change**

```powershell
$before = (Get-FileHash $scratch -Algorithm MD5).Hash
dotnet run --project clipmetascribe -- $scratch --set notes "dry run test" --dry-run
$after = (Get-FileHash $scratch -Algorithm MD5).Hash
if ($before -eq $after) { Write-Host "PASS: file unchanged" } else { Write-Host "FAIL: file was modified" }
```
Expected: `PASS: file unchanged`

- [ ] **Step 11.15: Smoke test — backup creates .bak file**

```powershell
dotnet run --project clipmetascribe -- $scratch --set notes "backup test" --backup
Test-Path ($scratch + ".bak")  # should be True
```
Expected: `True` — `.bak` file exists next to the written file.

- [ ] **Step 11.16: Smoke test — run against all pristine clips**

```powershell
foreach ($clip in (Get-ChildItem testclips/pristine/*.mp4)) {
    $s = "testclips/scratch/$($clip.Name)"
    Copy-Item $clip.FullName $s -Force
    dotnet run --project clipmetascribe -- $s --set game "Team Fortress 2" --set tags "headshot" --set rating "5"
    if ($LASTEXITCODE -ne 0) { Write-Host "FAIL: $($clip.Name)" }
    else { Write-Host "OK: $($clip.Name)" }
}
```
Expected: all clips exit 0.

- [ ] **Step 11.17: Commit**

```powershell
git add clipmetascribe/ ClipMeta.Core/Write/MetadataMutation.cs ClipMeta.Core/Write/Mp4Writer.cs
git commit -m "feat: clipmetascribe write command with --set/--append/--clear/--clear-all/--backup/--dry-run"
```

---

## Task 12: Final Verification

- [ ] **Step 12.1: Full solution build — zero errors and zero warnings**

```powershell
dotnet build
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` across all projects.

- [ ] **Step 12.2: Full test suite — all tests pass**

```powershell
dotnet test
```
Expected: all tests pass. Test count should be 80 (clipmetaview.Tests) plus all new clipmetascribe.Tests (BigEndianWriterTests + FreeformAtomWriterTests + FileLoggerTests + Mp4WriterTests + Mp4WriterIntegrationTests + NormalizationTests).

- [ ] **Step 12.3: Verify zero NuGet packages in ClipMeta.Core and clipmetascribe**

```powershell
Get-Content ClipMeta.Core/ClipMeta.Core.csproj
Get-Content clipmetascribe/clipmetascribe.csproj
```
Expected: neither file contains a `<PackageReference>` element. Only test projects (`*.Tests.csproj`) may have `PackageReference` entries (for MSTest).

- [ ] **Step 12.4: Verify clipmetaview still works end-to-end**

```powershell
dotnet test clipmetaview.Tests/clipmetaview.Tests.csproj
```
Expected: exactly 80 tests pass. Zero failures. This confirms the Mp4/ move to ClipMeta.Core did not break anything.

- [ ] **Step 12.5: Full round-trip — write then read back with clipmetaview**

```powershell
foreach ($clip in (Get-ChildItem testclips/pristine/*.mp4)) {
    $s = "testclips/scratch/final_$($clip.Name)"
    Copy-Item $clip.FullName $s -Force

    dotnet run --project clipmetascribe -- $s `
        --set game "Team Fortress 2" `
        --set players "Ben|Scott" `
        --set tags "market garden|funny moment" `
        --set timecode "00:01:23" `
        --set rating "4" `
        --set notes "round-trip verification"

    if ($LASTEXITCODE -ne 0) {
        Write-Host "WRITE FAIL: $($clip.Name)"
        continue
    }

    dotnet run --project clipmetaview -- $s | Select-String -Pattern "com\.peckworkslab|EDITABLE|game|tags|rating"
    Write-Host "---"
}
```
Expected: each clip shows the written fields in the tree, all marked `← [EDITABLE]`.

- [ ] **Step 12.6: Verify no orphaned temp files**

```powershell
Get-ChildItem testclips/scratch/*.tmp -ErrorAction SilentlyContinue
```
Expected: no `.tmp` files present. All write operations cleaned up after themselves.

---

## Definition of Done

This plan is complete when ALL of the following are true:

1. `dotnet build` — zero errors, zero warnings across all projects
2. `dotnet test` — all tests pass including all Mp4WriterIntegrationTests against real pristine clips
3. `clipmetaview` still passes all 80 original tests after the Mp4/ → ClipMeta.Core move
4. Round-trip verified: `clipmetascribe --set` → `clipmetaview` shows the written value
5. `clipmetascribe "missing.mp4" --set game "TF2"` → exits 1 with useful error message
6. `clipmetascribe` with no args → exits 1 and prints usage
7. `clipmetascribe --clear-all "clip.mp4"` without `--yes` → prompts for confirmation
8. `--dry-run` → source file byte-for-byte identical before and after
9. `--backup` → `.bak` file exists after write
10. Zero NuGet packages added to `ClipMeta.Core` or `clipmetascribe`
11. All clipmetascribe.Tests unit and integration tests pass

**Not in scope for this plan** (reserved for the next round):
- list, stats, vocab, find, export, index commands
- Directory batch operations
- Search index
- CopyTags
