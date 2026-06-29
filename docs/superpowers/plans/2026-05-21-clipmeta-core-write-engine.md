# ClipMeta.Core + Write Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract ClipMeta.Core as a shared library, implement a safe MP4 metadata write engine, and deliver clipmetascribe as a real CLI that can tag MP4 game clips with searchable metadata stored inside the file.

**Architecture:** ClipMeta.Core is a zero-NuGet-dependency class library that holds all business logic (parsing, writing, schema, search, logging). Both clipmetaview and clipmetascribe are thin CLI shells that reference Core. The write engine uses a temp-file strategy: the source file is never opened for writing; if anything fails the original is untouched.

**Tech Stack:** C# / .NET 10, MSTest 4.x, zero external NuGet packages (MSTest SDK counts as dev dependency only)

---

## File Map

Files created or significantly modified, with one-line purpose per file.

### New: ClipMeta.Core project
| File | Purpose |
|---|---|
| `ClipMeta.Core/ClipMeta.Core.csproj` | Class library project targeting net10.0 |
| `ClipMeta.Core/Exceptions/UnsupportedFormatException.cs` | Thrown when a file format can't be parsed or written |
| `ClipMeta.Core/Abstractions/IMediaParser.cs` | Contract: CanParse + ParseFile returning BoxNode |
| `ClipMeta.Core/Abstractions/IMediaWriter.cs` | Contract: CanWrite + WriteMetadata |
| `ClipMeta.Core/Abstractions/IClipMetaLogger.cs` | Contract: Log + LogVerbose; defines LogLevel enum |
| `ClipMeta.Core/Abstractions/MediaHandlerRegistry.cs` | Routes files to the correct parser/writer by extension |
| `ClipMeta.Core/Schema/ClipMetaSchema.cs` | Constants for field names and domain; AtomName helper |
| `ClipMeta.Core/Write/MetadataMutation.cs` | Describes mutations to apply atomically: set/append/delete |
| `ClipMeta.Core/Write/Normalizer.cs` | Trims, lowercases, deduplicates, canonicalizes timecodes |
| `ClipMeta.Core/Write/FreeformAtomWriter.cs` | Writes the `----` + mean + name + data atom chain to a stream |
| `ClipMeta.Core/Write/Mp4Writer.cs` | Full write pipeline: parse → temp file → verify → File.Replace |
| `ClipMeta.Core/Search/ClipMetaIndex.cs` | Reads/writes clipmeta-index.json per directory |
| `ClipMeta.Core/Search/ClipMetaSearch.cs` | Searches files using index or full scan; AND logic for filters |
| `ClipMeta.Core/Logging/FileLogger.cs` | Writes structured log entries; 3-file rotation at 10 MB each |
| `ClipMeta.Core/Logging/NullLogger.cs` | No-op logger for testing |

### Moved into ClipMeta.Core (from clipmetaview/Mp4/)
| File | Change |
|---|---|
| `ClipMeta.Core/Mp4/BoxHeader.cs` | Namespace `ClipMetaView.Mp4` → `ClipMeta.Core.Mp4` |
| `ClipMeta.Core/Mp4/FullBoxHeader.cs` | Same namespace update |
| `ClipMeta.Core/Mp4/BoxNode.cs` | Same namespace update |
| `ClipMeta.Core/Mp4/BigEndianReader.cs` | Same namespace update |
| `ClipMeta.Core/Mp4/BigEndianWriter.cs` | New: mirror of BigEndianReader for writing |
| `ClipMeta.Core/Mp4/MetadataKeys.cs` | Same namespace update |
| `ClipMeta.Core/Mp4/Mp4Parser.cs` | Namespace update + implement IMediaParser + read `----` freeform atoms |

### Modified: clipmetaview
| File | Change |
|---|---|
| `clipmetaview/clipmetaview.csproj` | Add ProjectReference to ClipMeta.Core; remove Mp4/ folder |
| `clipmetaview/AppRunner.cs` | Update using to `ClipMeta.Core.Mp4` |
| `clipmetaview/Rendering/TreeRenderer.cs` | Update using to `ClipMeta.Core.Mp4` |
| `clipmetaview/Program.cs` | No change needed (uses AppRunner) |

### Modified: clipmetaview.Tests
| File | Change |
|---|---|
| `clipmetaview.Tests/clipmetaview.Tests.csproj` | Add ProjectReference to ClipMeta.Core |
| `clipmetaview.Tests/TestClips.cs` | Update path lookup from `testclips` → `testclips/pristine` |
| `clipmetaview.Tests/BigEndianReaderTests.cs` | Add `using ClipMeta.Core.Mp4;` |
| `clipmetaview.Tests/BoxNodeTests.cs` | Same |
| `clipmetaview.Tests/MetadataKeysTests.cs` | Same |
| `clipmetaview.Tests/XtraBoxParserTests.cs` | Same |
| `clipmetaview.Tests/ProgramIntegrationTests.cs` | Same |

### New: clipmetascribe.Tests
| File | Purpose |
|---|---|
| `clipmetascribe.Tests/clipmetascribe.Tests.csproj` | MSTest project referencing ClipMeta.Core |
| `clipmetascribe.Tests/Helpers/ScratchClips.cs` | Copies pristine clips to scratch/ for write tests |
| `clipmetascribe.Tests/Helpers/MinimalMp4Builder.cs` | Builds valid minimal MP4 byte arrays for unit tests |
| `clipmetascribe.Tests/BigEndianWriterTests.cs` | Writer round-trip tests |
| `clipmetascribe.Tests/FreeformAtomWriterTests.cs` | Verifies `----` atom structure byte-by-byte |
| `clipmetascribe.Tests/FileLoggerTests.cs` | Log format, rotation |
| `clipmetascribe.Tests/Mp4WriterTests.cs` | Unit tests using MinimalMp4Builder |
| `clipmetascribe.Tests/Mp4WriterIntegrationTests.cs` | Round-trip tests using real scratch clips |
| `clipmetascribe.Tests/NormalizationTests.cs` | All normalization rules |
| `clipmetascribe.Tests/BatchOperationTests.cs` | Batch set/append/clear/untagged on scratch directory |
| `clipmetascribe.Tests/SearchIndexTests.cs` | Index build, stale detection, --find |

### New/Modified: clipmetascribe CLI
| File | Purpose |
|---|---|
| `clipmetascribe/Program.cs` | Replace "Hello World" with argument parser and command dispatcher |
| `clipmetascribe/Commands/WriteCommand.cs` | Handles --set, --append, --clear, --clear-all |
| `clipmetascribe/Commands/ListCommand.cs` | Handles --list, --output json/text |
| `clipmetascribe/Commands/StatsCommand.cs` | Handles --stats |
| `clipmetascribe/Commands/VocabCommand.cs` | Handles --vocab |
| `clipmetascribe/Commands/FindCommand.cs` | Handles --find, --since, --before |
| `clipmetascribe/Commands/IndexCommand.cs` | Handles --index |
| `clipmetascribe/Commands/ExportCommand.cs` | Handles --export |
| `clipmetascribe/clipmetascribe.csproj` | Add ProjectReference to ClipMeta.Core |

### testclips reorganization
| Action | Detail |
|---|---|
| Create `testclips/pristine/` at solution root | Move existing clips here; this dir is READ ONLY in tests |
| Create `testclips/scratch/` at solution root | Write tests copy files here before every test |

### Solution file
| File | Change |
|---|---|
| `peckworks-clipmeta.slnx` | Add ClipMeta.Core and clipmetascribe.Tests projects |

---

## Task 1: Create ClipMeta.Core Project

**Files:**
- Create: `ClipMeta.Core/ClipMeta.Core.csproj`
- Modify: `peckworks-clipmeta.slnx`

- [ ] **Step 1.1: Create the project directory and .csproj**

```powershell
New-Item -ItemType Directory -Path "ClipMeta.Core"
```

Write `ClipMeta.Core/ClipMeta.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

- [ ] **Step 1.2: Add ClipMeta.Core to the solution**

Edit `peckworks-clipmeta.slnx`, add the Core project entry:
```xml
<Solution>
  <Project Path="ClipMeta.Core/ClipMeta.Core.csproj" />
  <Project Path="clipmetascribe/clipmetascribe.csproj" Id="3c6076cf-28e6-4e2e-b703-4a397250ea98" />
  <Project Path="clipmetaview.Tests/clipmetaview.Tests.csproj" />
  <Project Path="clipmetaview/clipmetaview.csproj" />
</Solution>
```

- [ ] **Step 1.3: Create the subdirectory structure**

```powershell
"Abstractions","Mp4","Write","Schema","Search","Logging","Exceptions" | ForEach-Object {
    New-Item -ItemType Directory -Path "ClipMeta.Core/$_"
}
```

- [ ] **Step 1.4: Verify the project builds**

```powershell
dotnet build ClipMeta.Core/ClipMeta.Core.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 1.5: Commit**

```powershell
git add ClipMeta.Core/ peckworks-clipmeta.slnx
git commit -m "feat: add ClipMeta.Core class library project to solution"
```

---

## Task 2: Core Abstractions, Schema, and Data Types

**Files:**
- Create: `ClipMeta.Core/Exceptions/UnsupportedFormatException.cs`
- Create: `ClipMeta.Core/Abstractions/IClipMetaLogger.cs`
- Create: `ClipMeta.Core/Abstractions/IMediaParser.cs`
- Create: `ClipMeta.Core/Abstractions/IMediaWriter.cs`
- Create: `ClipMeta.Core/Abstractions/MediaHandlerRegistry.cs`
- Create: `ClipMeta.Core/Schema/ClipMetaSchema.cs`
- Create: `ClipMeta.Core/Write/MetadataMutation.cs`
- Create: `ClipMeta.Core/Logging/NullLogger.cs`

These are data types and contracts, no behavioral tests are needed before writing them.

- [ ] **Step 2.1: Write UnsupportedFormatException**

`ClipMeta.Core/Exceptions/UnsupportedFormatException.cs`:
```csharp
namespace ClipMeta.Core;

/// <summary>Thrown when a file format is not supported for parsing or writing.</summary>
public sealed class UnsupportedFormatException : Exception
{
    /// <inheritdoc/>
    public UnsupportedFormatException(string message) : base(message) { }
    /// <inheritdoc/>
    public UnsupportedFormatException(string message, Exception inner) : base(message, inner) { }
}
```

- [ ] **Step 2.2: Write IClipMetaLogger (includes LogLevel enum)**

`ClipMeta.Core/Abstractions/IClipMetaLogger.cs`:
```csharp
namespace ClipMeta.Core.Abstractions;

/// <summary>Verbosity levels for clipmeta operations.</summary>
public enum LogLevel { Simple, Verbose }

/// <summary>Structured logger for clipmeta operations.</summary>
public interface IClipMetaLogger
{
    /// <summary>Current verbosity level.</summary>
    LogLevel Level { get; }

    /// <summary>Logs a message at Simple level (always written).</summary>
    void Log(string message);

    /// <summary>Logs a message at Verbose level (no-op unless Level == Verbose).</summary>
    void LogVerbose(string message);
}
```

- [ ] **Step 2.3: Write IMediaParser**

`ClipMeta.Core/Abstractions/IMediaParser.cs`:
```csharp
using ClipMeta.Core.Mp4;

namespace ClipMeta.Core.Abstractions;

/// <summary>Reads a media file and returns its box/atom tree.</summary>
public interface IMediaParser
{
    /// <summary>Returns true if this parser can handle the given file extension.</summary>
    bool CanParse(string filePath);

    /// <summary>Parses the file and returns the root node of its structure tree.</summary>
    BoxNode ParseFile(string filePath);
}
```

- [ ] **Step 2.4: Write MetadataMutation**

`ClipMeta.Core/Write/MetadataMutation.cs`:
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
}
```

- [ ] **Step 2.5: Write IMediaWriter**

`ClipMeta.Core/Abstractions/IMediaWriter.cs`:
```csharp
using ClipMeta.Core.Write;

namespace ClipMeta.Core.Abstractions;

/// <summary>Writes metadata mutations into a media file safely.</summary>
public interface IMediaWriter
{
    /// <summary>Returns true if this writer can handle the given file extension.</summary>
    bool CanWrite(string filePath);

    /// <summary>
    /// Applies the mutation to the file using a temp-file strategy.
    /// The original is never opened for writing; on any failure it is untouched.
    /// </summary>
    void WriteMetadata(string filePath, MetadataMutation mutation, IClipMetaLogger logger);
}
```

- [ ] **Step 2.6: Write MediaHandlerRegistry**

`ClipMeta.Core/Abstractions/MediaHandlerRegistry.cs`:
```csharp
namespace ClipMeta.Core.Abstractions;

using ClipMeta.Core;

/// <summary>Selects the correct parser or writer for a given file by extension.</summary>
public sealed class MediaHandlerRegistry
{
    private readonly List<IMediaParser> _parsers = new();
    private readonly List<IMediaWriter> _writers = new();

    /// <summary>Registers a parser. Parsers are evaluated in registration order.</summary>
    public void RegisterParser(IMediaParser parser) => _parsers.Add(parser);

    /// <summary>Registers a writer. Writers are evaluated in registration order.</summary>
    public void RegisterWriter(IMediaWriter writer) => _writers.Add(writer);

    /// <summary>Returns the first parser that can handle the given file.</summary>
    /// <exception cref="UnsupportedFormatException">When no registered parser matches.</exception>
    public IMediaParser GetParser(string filePath)
    {
        return _parsers.FirstOrDefault(p => p.CanParse(filePath))
            ?? throw new UnsupportedFormatException(
                $"No parser registered for '{Path.GetExtension(filePath)}' files.");
    }

    /// <summary>Returns the first writer that can handle the given file.</summary>
    /// <exception cref="UnsupportedFormatException">When no registered writer matches.</exception>
    public IMediaWriter GetWriter(string filePath)
    {
        return _writers.FirstOrDefault(w => w.CanWrite(filePath))
            ?? throw new UnsupportedFormatException(
                $"No writer registered for '{Path.GetExtension(filePath)}' files.");
    }
}
```

- [ ] **Step 2.7: Write ClipMetaSchema**

`ClipMeta.Core/Schema/ClipMetaSchema.cs`:
```csharp
namespace ClipMeta.Core.Schema;

/// <summary>Constants for the com.peckworkslab.clipmeta metadata schema.</summary>
public static class ClipMetaSchema
{
    /// <summary>Reverse-domain namespace written into every ---- freeform atom.</summary>
    public const string Domain = "com.peckworkslab.clipmeta";

    /// <summary>Schema version field. Written on every write to enable future migrations.</summary>
    public const string Schema = "schema";

    /// <summary>Current schema version value.</summary>
    public const string SchemaVersion = "1";

    /// <summary>Game title field.</summary>
    public const string Game = "game";

    /// <summary>Pipe-separated list of player names.</summary>
    public const string Players = "players";

    /// <summary>Pipe-separated list of tags (lowercase).</summary>
    public const string Tags = "tags";

    /// <summary>Pipe-separated list of timecodes in HH:MM:SS format.</summary>
    public const string Timecode = "timecode";

    /// <summary>Integer rating 1–5.</summary>
    public const string Rating = "rating";

    /// <summary>Freeform notes field.</summary>
    public const string Notes = "notes";

    /// <summary>Pipe-separated fields (values are lists of items).</summary>
    public static readonly IReadOnlySet<string> PipeFields =
        new HashSet<string> { Players, Tags, Timecode };

    /// <summary>Returns the full atom name for a field: "com.peckworkslab.clipmeta:fieldname".</summary>
    public static string AtomName(string field) => $"{Domain}:{field}";
}
```

- [ ] **Step 2.8: Write NullLogger**

`ClipMeta.Core/Logging/NullLogger.cs`:
```csharp
using ClipMeta.Core.Abstractions;

namespace ClipMeta.Core.Logging;

/// <summary>No-op logger for use in tests and dry-run scenarios.</summary>
public sealed class NullLogger : IClipMetaLogger
{
    /// <summary>Singleton instance.</summary>
    public static readonly NullLogger Instance = new();

    /// <inheritdoc/>
    public LogLevel Level => LogLevel.Simple;

    /// <inheritdoc/>
    public void Log(string message) { }

    /// <inheritdoc/>
    public void LogVerbose(string message) { }
}
```

- [ ] **Step 2.9: Verify build**

```powershell
dotnet build ClipMeta.Core/ClipMeta.Core.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 2.10: Commit**

```powershell
git add ClipMeta.Core/
git commit -m "feat: add Core abstractions, schema constants, MetadataMutation, NullLogger"
```

---

## Task 3: Move Mp4 Code into ClipMeta.Core

**Files:**
- Create: `ClipMeta.Core/Mp4/*.cs` (6 files moved from `clipmetaview/Mp4/`)
- Modify: `clipmetaview/clipmetaview.csproj`
- Modify: `clipmetaview/AppRunner.cs`
- Modify: `clipmetaview/Rendering/TreeRenderer.cs`
- Modify: `clipmetaview.Tests/clipmetaview.Tests.csproj`
- Modify: `clipmetaview.Tests/*.cs` (namespace using updates)
- Delete: `clipmetaview/Mp4/` (after files are confirmed moved and tests pass)

The only behavioral change in this task: `Mp4Parser` gains the `IMediaParser` interface, and it gains the ability to read `----` freeform atoms (so the write engine can later read back what it wrote).

- [ ] **Step 3.1: Copy Mp4 files into ClipMeta.Core with updated namespace**

Copy each file from `clipmetaview/Mp4/` to `ClipMeta.Core/Mp4/`, changing only the namespace declaration from `ClipMetaView.Mp4` to `ClipMeta.Core.Mp4`.

Files to copy (change namespace header in each):
- `BoxHeader.cs` → `namespace ClipMeta.Core.Mp4;`
- `FullBoxHeader.cs` → `namespace ClipMeta.Core.Mp4;`
- `BoxNode.cs` → `namespace ClipMeta.Core.Mp4;`
- `BigEndianReader.cs` → `using System.Text;` stays, `namespace ClipMeta.Core.Mp4;`
- `MetadataKeys.cs` → `namespace ClipMeta.Core.Mp4;`

For `Mp4Parser.cs`, copy it, update namespace to `ClipMeta.Core.Mp4`, add the interface implementation and `----` atom reading (see Steps 3.2–3.3).

- [ ] **Step 3.2: Add IMediaParser to Mp4Parser**

In `ClipMeta.Core/Mp4/Mp4Parser.cs`, add these lines after the opening `namespace ClipMeta.Core.Mp4;`:
```csharp
using ClipMeta.Core.Abstractions;
using ClipMeta.Core.Schema;
```

Change the class declaration:
```csharp
// Before:
public class Mp4Parser

// After:
public class Mp4Parser : IMediaParser
```

Add these two members anywhere inside the class (explicit interface implementation so existing static call sites are unaffected):
```csharp
/// <inheritdoc/>
bool IMediaParser.CanParse(string filePath) =>
    Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

/// <inheritdoc/>
BoxNode IMediaParser.ParseFile(string filePath) => ParseFile(filePath);
```

- [ ] **Step 3.3: Add `----` freeform atom reading to Mp4Parser**

In `ParseBoxes`, the `if (inIlst)` branch currently marks everything editable and looks for a `data` child. Extend it to handle `----` atoms specifically so the write engine can read back what it writes:

Find the `if (inIlst)` block. Replace the inner body with:
```csharp
if (inIlst)
{
    node.IsEditable = true;
    node.EditableKey = header.Type;

    if (header.Type == "----" && contentStart < boxEnd)
    {
        // Freeform atom: parse mean, name, data children to build full key.
        var freeformChildren = ParseBoxes(reader, contentStart, boxEnd, inIlst: false);
        node.Children.AddRange(freeformChildren);

        string domain = string.Empty, fieldName = string.Empty;
        foreach (var child in freeformChildren)
        {
            if (child.Type == "mean" && child.DisplayValue != null)
                domain = child.DisplayValue;
            else if (child.Type == "name" && child.DisplayValue != null)
                fieldName = child.DisplayValue;
        }
        if (domain.Length > 0 && fieldName.Length > 0)
            node.EditableKey = $"{domain}:{fieldName}";

        var dataChild = freeformChildren.Find(c => c.Type == "data");
        if (dataChild != null)
            ExtractValueFromDataNode(reader, dataChild, node);
    }
    else if (contentStart < boxEnd)
    {
        var itemChildren = ParseBoxes(reader, contentStart, boxEnd, inIlst: false);
        node.Children.AddRange(itemChildren);
        var dataChild = itemChildren.Find(c => c.Type == "data");
        if (dataChild != null)
            ExtractValueFromDataNode(reader, dataChild, node);
    }
}
```

`case "mean":` and an updated `case "name":` (which detects the four-zero FullBox prefix and handles both the QuickTime udta and freeform-child formats) were fixed directly in `clipmetaview/Mp4/Mp4Parser.cs` and will be carried into `ClipMeta.Core/Mp4/Mp4Parser.cs` when that file is copied in Step 3.1. No additional changes to `ExtractLeafValue` are needed here.

Do NOT add `"mean"` or `"name"` to `FullBoxTypes` in Mp4Parser. The global `FullBoxTypes` set must remain:
```csharp
private static readonly HashSet<string> FullBoxTypes = new(StringComparer.Ordinal)
{
    "meta", "mvhd", "tkhd", "mdhd", "hdlr", "stsd", "stts", "stsc", "stsz",
    "stco", "co64", "elst", "dref", "smhd", "vmhd", "nmhd",
};
```

- [ ] **Step 3.4: Update clipmetaview to reference Core**

Edit `clipmetaview/clipmetaview.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ClipMeta.Core\ClipMeta.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3.5: Update usings in clipmetaview source files**

In `clipmetaview/AppRunner.cs`, change `using ClipMetaView.Mp4;` to `using ClipMeta.Core.Mp4;`.

In `clipmetaview/Rendering/TreeRenderer.cs`, change `using ClipMetaView.Mp4;` to `using ClipMeta.Core.Mp4;`.

- [ ] **Step 3.6: Update clipmetaview.Tests to reference Core**

Edit `clipmetaview.Tests/clipmetaview.Tests.csproj`, add a second ProjectReference:
```xml
<ItemGroup>
  <ProjectReference Include="..\clipmetaview\clipmetaview.csproj" />
  <ProjectReference Include="..\ClipMeta.Core\ClipMeta.Core.csproj" />
</ItemGroup>
```

- [ ] **Step 3.7: Update using statements in all test files**

In each of these files, add or change the Mp4 namespace import:
- `BigEndianReaderTests.cs`: add `using ClipMeta.Core.Mp4;`
- `BoxNodeTests.cs`: add `using ClipMeta.Core.Mp4;`
- `MetadataKeysTests.cs`: add `using ClipMeta.Core.Mp4;`
- `XtraBoxParserTests.cs`: add `using ClipMeta.Core.Mp4;`
- `ProgramIntegrationTests.cs`: add `using ClipMeta.Core.Mp4;`
- `TreeRendererTests.cs`: change `using ClipMetaView.Mp4;` to `using ClipMeta.Core.Mp4;`

**Important:** `TreeRendererTests.cs` must be updated here. After Step 3.9 deletes `clipmetaview/Mp4/`, any file still importing `ClipMetaView.Mp4` will fail to compile.

- [ ] **Step 3.8: Build both projects and run existing tests**

```powershell
dotnet build
dotnet test clipmetaview.Tests/clipmetaview.Tests.csproj
```
Expected: all 80 existing tests pass. If any fail, the namespace change broke a static reference, fix by checking the using directive in the failing test file.

- [ ] **Step 3.9: Delete the now-redundant Mp4/ folder from clipmetaview**

Only do this after Step 3.8 passes:
```powershell
Remove-Item -Recurse -Force clipmetaview/Mp4
```

Run tests again to confirm nothing broke:
```powershell
dotnet test clipmetaview.Tests/clipmetaview.Tests.csproj
```

- [ ] **Step 3.10: Commit**

```powershell
git add ClipMeta.Core/Mp4/ clipmetaview/ clipmetaview.Tests/
git commit -m "refactor: move Mp4 code into ClipMeta.Core; add IMediaParser to Mp4Parser; read ---- atoms"
```

---

## Task 4: Reorganize testclips into pristine/scratch

**Files:**
- Create: `testclips/pristine/` at solution root
- Create: `testclips/scratch/` at solution root
- Move: existing clips from `clipmetaview/testclips/` → `testclips/pristine/`
- Modify: `clipmetaview.Tests/TestClips.cs`

- [ ] **Step 4.1: Create solution-root testclips directories**

```powershell
New-Item -ItemType Directory -Path "testclips/pristine"
New-Item -ItemType Directory -Path "testclips/scratch"
```

- [ ] **Step 4.2: Move existing clips to pristine**

```powershell
Get-ChildItem "clipmetaview/testclips" -Filter "*.mp4" |
    Move-Item -Destination "testclips/pristine/"
```

Remove the now-empty old directory:
```powershell
Remove-Item -Recurse -Force "clipmetaview/testclips"
```

**Important:** Steps 4.2 and 4.3 must be treated as a single atomic operation. Do NOT run tests between these two steps, the test suite will fail because `TestClips.cs` still references the old path (`testclips`) while the clips have already been moved. Complete both steps before running tests.

- [ ] **Step 4.3: Update TestClips.cs to look for testclips/pristine**

Replace `clipmetaview.Tests/TestClips.cs` entirely:
```csharp
namespace ClipMetaView.Tests;

/// <summary>Helpers for locating the solution-level testclips directory.</summary>
internal static class TestClips
{
    /// <summary>
    /// Returns all .mp4 files in testclips/pristine/ at the solution root.
    /// </summary>
    public static IEnumerable<string> All()
    {
        string pristinePath = FindPristinePath();
        return Directory.EnumerateFiles(pristinePath, "*.mp4");
    }

    /// <summary>
    /// Walks up from the test assembly's bin folder to find testclips/pristine/.
    /// </summary>
    public static string FindPristinePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "testclips", "pristine");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "testclips/pristine folder not found. Walk up from: " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Returns the solution-root testclips/scratch/ path, creating it if absent.
    /// </summary>
    public static string FindScratchPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "testclips", "scratch");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "testclips/scratch folder not found. Walk up from: " + AppContext.BaseDirectory);
    }
}
```

- [ ] **Step 4.4: Verify all existing tests still pass**

```powershell
dotnet test clipmetaview.Tests/clipmetaview.Tests.csproj
```
Expected: all tests pass. The test runner walks up looking for `testclips/pristine` and finds the solution-root directory.

- [ ] **Step 4.5: Add a .gitkeep to testclips/scratch and a .gitignore rule for scratch MP4s**

```powershell
New-Item -ItemType File -Path "testclips/scratch/.gitkeep"
```

Add to the solution root `.gitignore` (create it if it doesn't exist):
```
testclips/scratch/*.mp4
```

Write tests produce MP4 files in `testclips/scratch/` that must not be accidentally committed.

- [ ] **Step 4.6: Commit**

```powershell
git add testclips/ clipmetaview.Tests/TestClips.cs
git commit -m "refactor: move testclips to solution root with pristine/scratch split"
```

---

## Task 5: Create clipmetascribe.Tests Project

**Files:**
- Create: `clipmetascribe.Tests/clipmetascribe.Tests.csproj`
- Create: `clipmetascribe.Tests/Helpers/ScratchClips.cs`
- Create: `clipmetascribe.Tests/Helpers/MinimalMp4Builder.cs`
- Modify: `peckworks-clipmeta.slnx`

- [ ] **Step 5.1: Create the test project**

```powershell
New-Item -ItemType Directory -Path "clipmetascribe.Tests/Helpers"
```

Write `clipmetascribe.Tests/clipmetascribe.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <NoWarn>$(NoWarn);MSTEST0037;MSTEST0044;MSTEST0052</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MSTest" Version="4.0.2" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\ClipMeta.Core\ClipMeta.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5.2: Add clipmetascribe.Tests to solution**

Edit `peckworks-clipmeta.slnx`:
```xml
<Solution>
  <Project Path="ClipMeta.Core/ClipMeta.Core.csproj" />
  <Project Path="clipmetascribe/clipmetascribe.csproj" Id="3c6076cf-28e6-4e2e-b703-4a397250ea98" />
  <Project Path="clipmetascribe.Tests/clipmetascribe.Tests.csproj" />
  <Project Path="clipmetaview.Tests/clipmetaview.Tests.csproj" />
  <Project Path="clipmetaview/clipmetaview.csproj" />
</Solution>
```

- [ ] **Step 5.3: Write ScratchClips helper**

First write `clipmetascribe.Tests/Helpers/TestClipsLocator.cs` (self-contained locator, no cross-project dependency):

`clipmetascribe.Tests/Helpers/TestClipsLocator.cs`:
```csharp
namespace ClipMetaScribe.Tests.Helpers;

/// <summary>Locates the solution-level testclips directories for the scribe test project.</summary>
internal static class TestClipsLocator
{
    public static string FindPristinePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "testclips", "pristine");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("testclips/pristine not found from " + AppContext.BaseDirectory);
    }

    public static string FindScratchPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "testclips", "scratch");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("testclips/scratch not found from " + AppContext.BaseDirectory);
    }

    public static IEnumerable<string> AllPristine()
        => Directory.EnumerateFiles(FindPristinePath(), "*.mp4");
}
```

Then write `clipmetascribe.Tests/Helpers/ScratchClips.cs`:

`clipmetascribe.Tests/Helpers/ScratchClips.cs`:
```csharp
namespace ClipMetaScribe.Tests.Helpers;

/// <summary>
/// Manages scratch copies of pristine test clips for write tests.
/// Each write test must work on a scratch copy so pristine clips are never modified.
/// </summary>
internal static class ScratchClips
{
    /// <summary>
    /// Copies a pristine clip to testclips/scratch/ and returns the scratch path.
    /// The scratch copy is overwritten if it already exists.
    /// </summary>
    public static string Prepare(string pristineFilePath)
    {
        string scratchDir = TestClipsLocator.FindScratchPath();
        string scratchPath = Path.Combine(scratchDir, Path.GetFileName(pristineFilePath));
        File.Copy(pristineFilePath, scratchPath, overwrite: true);
        return scratchPath;
    }

    /// <summary>Returns scratch paths for all pristine clips (copies all).</summary>
    public static IEnumerable<string> PrepareAll()
        => TestClipsLocator.AllPristine().Select(Prepare);

    /// <summary>Returns all .mp4 files currently in the scratch directory.</summary>
    public static IEnumerable<string> AllScratch()
        => Directory.EnumerateFiles(TestClipsLocator.FindScratchPath(), "*.mp4");
}
```

Note: `ScratchClips` uses `TestClipsLocator` from the same project, no cross-project `ProjectReference` needed. Do NOT use `TestClips` from `clipmetaview.Tests`.

The `TestClipsLocator` file shown below is no longer needed as a separate addition, it is written above.

Add `clipmetascribe.Tests/Helpers/TestClipsLocator.cs`:

```csharp
namespace ClipMetaScribe.Tests.Helpers;

/// <summary>Locates the solution-level testclips directories for the scribe test project.</summary>
internal static class TestClipsLocator
{
    public static string FindPristinePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "testclips", "pristine");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("testclips/pristine not found from " + AppContext.BaseDirectory);
    }

    public static string FindScratchPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "testclips", "scratch");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("testclips/scratch not found from " + AppContext.BaseDirectory);
    }

    public static IEnumerable<string> AllPristine()
        => Directory.EnumerateFiles(FindPristinePath(), "*.mp4");
}
```

- [ ] **Step 5.4: Write MinimalMp4Builder**

`clipmetascribe.Tests/Helpers/MinimalMp4Builder.cs`:
```csharp
using System.Text;

namespace ClipMetaScribe.Tests.Helpers;

/// <summary>
/// Builds minimal but structurally valid MP4 byte arrays for write engine unit tests.
/// All sizes are calculated and all integers are big-endian.
/// </summary>
internal static class MinimalMp4Builder
{
    // ── Low-level primitives ──────────────────────────────────────────────────

    private static void WriteBE32(BinaryWriter bw, uint v)
    {
        bw.Write((byte)(v >> 24));
        bw.Write((byte)(v >> 16));
        bw.Write((byte)(v >> 8));
        bw.Write((byte)v);
    }

    private static byte[] Box(string type, byte[] payload)
    {
        uint size = (uint)(8 + payload.Length);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteBE32(bw, size);
        bw.Write(Encoding.Latin1.GetBytes(type.PadRight(4)[..4]));
        bw.Write(payload);
        return ms.ToArray();
    }

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload)
    {
        byte[] header = new byte[4];
        header[0] = version;
        header[1] = (byte)(flags >> 16);
        header[2] = (byte)(flags >> 8);
        header[3] = (byte)flags;
        return Box(type, header.Concat(payload).ToArray());
    }

    // ── Atom builders ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a ---- freeform atom with mean (domain), name (field), and data (UTF-8 value).
    /// Both mean and name are FullBoxes, version+flags are 4 bytes each.
    /// </summary>
    public static byte[] FreeformAtom(string domain, string fieldName, string value)
    {
        byte[] mean = FullBox("mean", 0, 0, Encoding.UTF8.GetBytes(domain));
        byte[] name = FullBox("name", 0, 0, Encoding.UTF8.GetBytes(fieldName));

        // data: version=0, type=1 (UTF-8), locale=0000 (4 bytes), then value bytes
        byte[] dataPayload = new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }
            .Concat(Encoding.UTF8.GetBytes(value))
            .ToArray();
        byte[] data = Box("data", dataPayload);

        return Box("----", mean.Concat(name).Concat(data).ToArray());
    }

    /// <summary>
    /// Builds a minimal ilst box containing zero or more freeform atoms.
    /// </summary>
    public static byte[] IlstBox(params byte[][] atoms)
        => Box("ilst", atoms.SelectMany(a => a).ToArray());

    /// <summary>
    /// Builds a minimal meta FullBox (handler_type="mdir") containing an ilst.
    /// </summary>
    public static byte[] MetaBox(byte[] ilstBox)
    {
        byte[] hdlrPayload = new byte[20];  // pre_defined(4) + handler_type(4) + reserved(12)
        Encoding.Latin1.GetBytes("mdir").CopyTo(hdlrPayload, 4);
        byte[] hdlr = FullBox("hdlr", 0, 0, hdlrPayload);
        return FullBox("meta", 0, 0, hdlr.Concat(ilstBox).ToArray());
    }

    /// <summary>Builds a minimal udta box wrapping a meta box.</summary>
    public static byte[] UdtaBox(byte[] metaBox) => Box("udta", metaBox);

    /// <summary>
    /// Builds a minimal stco FullBox with the given chunk offsets (big-endian uint32 each).
    /// </summary>
    public static byte[] StcoBox(params uint[] offsets)
    {
        byte[] entryCount = new byte[4];
        entryCount[0] = (byte)(offsets.Length >> 24);
        entryCount[1] = (byte)(offsets.Length >> 16);
        entryCount[2] = (byte)(offsets.Length >> 8);
        entryCount[3] = (byte)offsets.Length;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(entryCount);
        foreach (uint o in offsets) WriteBE32(bw, o);
        return FullBox("stco", 0, 0, ms.ToArray());
    }

    /// <summary>
    /// Builds a minimal stbl box wrapping a stco box.
    /// (stts, stsc, stsz are omitted since the write engine only touches stco/co64.)
    /// </summary>
    public static byte[] StblBox(byte[] stcoBox) => Box("stbl", stcoBox);

    /// <summary>Wraps stbl in minf, minf in mdia, mdia in trak, minimal valid track chain.</summary>
    public static byte[] TrakBox(byte[] stcoBox)
    {
        byte[] stbl = StblBox(stcoBox);
        byte[] minf = Box("minf", stbl);
        byte[] mdia = Box("mdia", minf);
        return Box("trak", mdia);
    }

    /// <summary>
    /// Builds a complete moov box with optional udta and one or two tracks.
    /// mvhd is minimal (all zeros except size+type), which is sufficient for the write engine.
    /// Pass <c>null</c> for <paramref name="udtaBox"/> when no udta is needed.
    /// </summary>
    public static byte[] MoovBox(byte[]? udtaBox, params byte[][] trakBoxes)
    {
        byte[] mvhd = FullBox("mvhd", 0, 0, new byte[96]); // v0 mvhd body = 96 bytes
        var children = new List<byte[]> { mvhd };
        children.AddRange(trakBoxes);
        if (udtaBox != null) children.Add(udtaBox);
        return Box("moov", children.SelectMany(b => b).ToArray());
    }

    /// <summary>Builds a minimal mdat box with N bytes of filler.</summary>
    public static byte[] MdatBox(int fillerBytes = 64)
        => Box("mdat", new byte[fillerBytes]);

    /// <summary>
    /// Assembles a complete moov-before-mdat MP4 file useful for stco adjustment tests.
    /// Returns the raw bytes as a MemoryStream.
    /// </summary>
    /// <param name="chunkOffset">The single stco entry; must point past end of moov.</param>
    public static MemoryStream BuildMp4WithStco(uint chunkOffset, string domain, string fieldName, string value)
    {
        byte[] freeform = FreeformAtom(domain, fieldName, value);
        byte[] ilst = IlstBox(freeform);
        byte[] meta = MetaBox(ilst);
        byte[] udta = UdtaBox(meta);
        byte[] stco = StcoBox(chunkOffset);
        byte[] trak = TrakBox(stco);
        byte[] moov = MoovBox(udta, trak);
        byte[] mdat = MdatBox();

        var ms = new MemoryStream();
        ms.Write(moov);
        ms.Write(mdat);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Saves a byte stream to a temp file, returns the file path.
    /// Caller is responsible for deleting the file.
    /// </summary>
    public static string SaveToTempFile(MemoryStream ms, string extension = ".mp4")
    {
        string path = Path.ChangeExtension(Path.GetTempFileName(), extension);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }
}
```

- [ ] **Step 5.5: Verify the test project builds**

```powershell
dotnet build clipmetascribe.Tests/clipmetascribe.Tests.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5.6: Commit**

```powershell
git add clipmetascribe.Tests/ peckworks-clipmeta.slnx
git commit -m "feat: add clipmetascribe.Tests project with ScratchClips and MinimalMp4Builder helpers"
```

---

## Task 6: BigEndianWriter (TDD)

**Files:**
- Create: `ClipMeta.Core/Mp4/BigEndianWriter.cs`
- Create: `clipmetascribe.Tests/BigEndianWriterTests.cs`

- [ ] **Step 6.1: Write the failing tests**

`clipmetascribe.Tests/BigEndianWriterTests.cs`:
```csharp
using ClipMeta.Core.Mp4;
using System.Text;

namespace ClipMetaScribe.Tests;

[TestClass]
public class BigEndianWriterTests
{
    [TestMethod]
    public void WriteUInt16_ThenReadBack_Matches()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteUInt16(bw, 0x1234);
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        Assert.AreEqual((ushort)0x1234, BigEndianReader.ReadUInt16(br)); // cast required: MSTest checks type
    }

    [TestMethod]
    public void WriteUInt32_ThenReadBack_Matches()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteUInt32(bw, 0x00B4AF20);
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        Assert.AreEqual(0x00B4AF20u, BigEndianReader.ReadUInt32(br));
    }

    [TestMethod]
    public void WriteUInt64_ThenReadBack_Matches()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteUInt64(bw, 0x0000000100000020UL);
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        Assert.AreEqual(0x0000000100000020UL, BigEndianReader.ReadUInt64(br));
    }

    [TestMethod]
    public void WriteFourCC_ThenReadBack_Matches()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteFourCC(bw, "moov");
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        Assert.AreEqual("moov", BigEndianReader.ReadFourCC(br));
    }

    [TestMethod]
    public void WriteFourCC_CopyrightPrefix_RoundTrips()
    {
        // © = 0xA9. FourCC must use Latin-1 so the byte round-trips.
        string fourCC = "©nam";
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteFourCC(bw, fourCC);
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        Assert.AreEqual(fourCC, BigEndianReader.ReadFourCC(br));
    }

    [TestMethod]
    public void WriteUInt32_BigEndianByteOrder_CorrectBytes()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteUInt32(bw, 0x00000020); // = 32 decimal
        byte[] bytes = ms.ToArray();
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x00, 0x00, 0x20 }, bytes);
    }
}
```

- [ ] **Step 6.2: Run tests, expect compile failure (BigEndianWriter not yet written)**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "BigEndianWriterTests"
```
Expected: build fails with `CS0103: The name 'BigEndianWriter' does not exist`

- [ ] **Step 6.3: Implement BigEndianWriter**

`ClipMeta.Core/Mp4/BigEndianWriter.cs`:
```csharp
using System.Text;

namespace ClipMeta.Core.Mp4;

/// <summary>
/// Static utility for writing big-endian integers and MP4 structural types to a <see cref="BinaryWriter"/>.
/// Mirrors <see cref="BigEndianReader"/>, every write is the exact inverse of a read.
/// </summary>
public static class BigEndianWriter
{
    /// <summary>Writes a 2-byte unsigned integer in big-endian order.</summary>
    public static void WriteUInt16(BinaryWriter writer, ushort value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        writer.Write(bytes);
    }

    /// <summary>Writes a 4-byte unsigned integer in big-endian order.</summary>
    public static void WriteUInt32(BinaryWriter writer, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        writer.Write(bytes);
    }

    /// <summary>Writes an 8-byte unsigned integer in big-endian order.</summary>
    public static void WriteUInt64(BinaryWriter writer, ulong value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        writer.Write(bytes);
    }

    /// <summary>
    /// Writes a 4-byte FourCC string using Latin-1 encoding so the © prefix (0xA9) round-trips.
    /// </summary>
    public static void WriteFourCC(BinaryWriter writer, string fourCC)
    {
        writer.Write(Encoding.Latin1.GetBytes(fourCC.PadRight(4)[..4]));
    }

    /// <summary>
    /// Writes an MP4 box header: 4-byte size (big-endian) + 4-byte FourCC.
    /// </summary>
    public static void WriteBoxHeader(BinaryWriter writer, uint size, string type)
    {
        WriteUInt32(writer, size);
        WriteFourCC(writer, type);
    }

    /// <summary>
    /// Writes a FullBox prefix: 1-byte version + 3-byte flags (big-endian).
    /// Always call this immediately after WriteBoxHeader for FullBox types.
    /// </summary>
    public static void WriteFullBoxPrefix(BinaryWriter writer, byte version, uint flags)
    {
        writer.Write(version);
        writer.Write((byte)(flags >> 16));
        writer.Write((byte)(flags >> 8));
        writer.Write((byte)flags);
    }
}
```

- [ ] **Step 6.4: Run tests, expect all pass**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "BigEndianWriterTests"
```
Expected: `6 passing`

- [ ] **Step 6.5: Commit**

```powershell
git add ClipMeta.Core/Mp4/BigEndianWriter.cs clipmetascribe.Tests/BigEndianWriterTests.cs
git commit -m "feat: add BigEndianWriter with round-trip tests"
```

---

## Task 7: FreeformAtomWriter (TDD)

**Files:**
- Create: `ClipMeta.Core/Write/FreeformAtomWriter.cs`
- Create: `clipmetascribe.Tests/FreeformAtomWriterTests.cs`

The `----` atom structure is the most failure-prone part of this entire implementation. The critical constraint: both `mean` and `name` children ARE FullBoxes and MUST have a 4-byte version+flags prefix. Omitting those 4 bytes shifts all bytes after them and produces a malformed atom that cannot be read back.

- [ ] **Step 7.1: Write the failing tests**

`clipmetascribe.Tests/FreeformAtomWriterTests.cs`:
```csharp
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;
using ClipMeta.Core.Write;
using System.Text;

namespace ClipMetaScribe.Tests;

[TestClass]
public class FreeformAtomWriterTests
{
    private static byte[] WriteFreeform(string fieldName, string value)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        FreeformAtomWriter.Write(bw, ClipMetaSchema.Domain, fieldName, value);
        return ms.ToArray();
    }

    [TestMethod]
    public void Write_OuterBox_HasDashDashDashDashFourCC()
    {
        byte[] bytes = WriteFreeform("tags", "headshot");
        string fourCC = Encoding.Latin1.GetString(bytes, 4, 4);
        Assert.AreEqual("----", fourCC);
    }

    [TestMethod]
    public void Write_OuterSize_MatchesActualLength()
    {
        byte[] bytes = WriteFreeform("tags", "headshot");
        uint size = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        Assert.AreEqual((uint)bytes.Length, size, "Outer ---- box size must equal byte array length.");
    }

    [TestMethod]
    public void Write_MeanBox_HasFullBoxPrefix()
    {
        byte[] bytes = WriteFreeform("tags", "headshot");

        // After 8 bytes (---- header), mean box starts.
        // mean box: 4 size + 4 "mean" + 4 version+flags + domain bytes
        int meanStart = 8;
        uint meanSize = (uint)((bytes[meanStart] << 24) | (bytes[meanStart+1] << 16)
                               | (bytes[meanStart+2] << 8) | bytes[meanStart+3]);
        string meanFourCC = Encoding.Latin1.GetString(bytes, meanStart + 4, 4);
        byte meanVersion = bytes[meanStart + 8];
        byte meanFlag1 = bytes[meanStart + 9];
        byte meanFlag2 = bytes[meanStart + 10];
        byte meanFlag3 = bytes[meanStart + 11];

        Assert.AreEqual("mean", meanFourCC, "First child must be 'mean'");
        Assert.AreEqual(0, meanVersion, "mean version must be 0 (FullBox)");
        Assert.AreEqual(0, meanFlag1 | meanFlag2 | meanFlag3, "mean flags must be 0 (FullBox)");

        // Domain content starts at meanStart+12; verify it matches
        int domainLen = (int)meanSize - 12; // 4 size + 4 type + 4 version+flags = 12 header bytes
        string domain = Encoding.UTF8.GetString(bytes, meanStart + 12, domainLen);
        Assert.AreEqual(ClipMetaSchema.Domain, domain);
    }

    [TestMethod]
    public void Write_NameBox_HasFullBoxPrefix()
    {
        byte[] bytes = WriteFreeform("tags", "headshot");

        // Locate name box: starts after mean box
        int meanStart = 8;
        uint meanSize = (uint)((bytes[meanStart] << 24) | (bytes[meanStart+1] << 16)
                               | (bytes[meanStart+2] << 8) | bytes[meanStart+3]);
        int nameStart = meanStart + (int)meanSize;

        string nameFourCC = Encoding.Latin1.GetString(bytes, nameStart + 4, 4);
        byte nameVersion = bytes[nameStart + 8];

        Assert.AreEqual("name", nameFourCC, "Second child must be 'name'");
        Assert.AreEqual(0, nameVersion, "name version must be 0 (FullBox)");

        uint nameSize = (uint)((bytes[nameStart] << 24) | (bytes[nameStart+1] << 16)
                               | (bytes[nameStart+2] << 8) | bytes[nameStart+3]);
        int fieldLen = (int)nameSize - 12;
        string field = Encoding.UTF8.GetString(bytes, nameStart + 12, fieldLen);
        Assert.AreEqual("tags", field);
    }

    [TestMethod]
    public void Write_DataBox_HasCorrectTypeIndicatorAndValue()
    {
        byte[] bytes = WriteFreeform("tags", "headshot");

        // Parse to data box position
        int meanStart = 8;
        uint meanSize = (uint)((bytes[meanStart] << 24) | (bytes[meanStart+1] << 16)
                               | (bytes[meanStart+2] << 8) | bytes[meanStart+3]);
        int nameStart = meanStart + (int)meanSize;
        uint nameSize = (uint)((bytes[nameStart] << 24) | (bytes[nameStart+1] << 16)
                               | (bytes[nameStart+2] << 8) | bytes[nameStart+3]);
        int dataStart = nameStart + (int)nameSize;

        string dataFourCC = Encoding.Latin1.GetString(bytes, dataStart + 4, 4);
        // data payload: 1 version + 3 type_indicator + 4 locale + value
        byte version = bytes[dataStart + 8];
        int typeIndicator = (bytes[dataStart + 9] << 16) | (bytes[dataStart + 10] << 8) | bytes[dataStart + 11];

        Assert.AreEqual("data", dataFourCC);
        Assert.AreEqual(0, version);
        Assert.AreEqual(1, typeIndicator, "Type indicator 1 = UTF-8 text");

        uint dataSize = (uint)((bytes[dataStart] << 24) | (bytes[dataStart+1] << 16)
                               | (bytes[dataStart+2] << 8) | bytes[dataStart+3]);
        // value starts at dataStart + 8 (header) + 4 (version+type) + 4 (locale) = dataStart + 16
        int valueLen = (int)dataSize - 16;
        string value = Encoding.UTF8.GetString(bytes, dataStart + 16, valueLen);
        Assert.AreEqual("headshot", value);
    }

    [TestMethod]
    public void Write_ParseBack_AtomReadableByMp4Parser()
    {
        // Write only the ---- atom bytes (no ilst wrapper) and call ParseBoxes with inIlst:true.
        // Wrapping in an ilst box first would cause the parser to see "ilst" as nodes[0],
        // not the "----" atom, the assertion on node.Type == "----" would always fail.
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        FreeformAtomWriter.Write(bw, ClipMetaSchema.Domain, "tags", "headshot");
        ms.Position = 0;

        using var reader = new BinaryReader(ms, System.Text.Encoding.Latin1, leaveOpen: true);
        var nodes = ClipMeta.Core.Mp4.Mp4Parser.ParseBoxes(reader, 0, ms.Length, inIlst: true);

        Assert.AreEqual(1, nodes.Count, "Expected exactly one item");
        var node = nodes[0];
        Assert.AreEqual("----", node.Type);
        Assert.AreEqual($"{ClipMetaSchema.Domain}:tags", node.EditableKey);
        Assert.IsTrue(node.IsEditable);
        Assert.IsNotNull(node.DisplayValue);
        Assert.IsTrue(node.DisplayValue!.Contains("headshot"), $"DisplayValue was: {node.DisplayValue}");
    }
}
```

- [ ] **Step 7.2: Run tests, expect compile failure**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "FreeformAtomWriterTests"
```
Expected: build fails with `CS0103: The name 'FreeformAtomWriter' does not exist`

- [ ] **Step 7.3: Implement FreeformAtomWriter**

`ClipMeta.Core/Write/FreeformAtomWriter.cs`:
```csharp
using System.Text;
using ClipMeta.Core.Mp4;

namespace ClipMeta.Core.Write;

/// <summary>
/// Writes a single <c>----</c> (freeform) MP4 atom to a stream.
/// Structure: <c>----</c> contains <c>mean</c> (domain), <c>name</c> (field), <c>data</c> (value).
/// Both <c>mean</c> and <c>name</c> are FullBoxes and carry a mandatory 4-byte version+flags prefix.
/// </summary>
public static class FreeformAtomWriter
{
    private const int DataOverhead = 16; // 8 box header + 4 (version+type) + 4 locale

    /// <summary>
    /// Writes a complete <c>----</c> atom to <paramref name="writer"/>.
    /// </summary>
    /// <param name="writer">Destination stream positioned at the write location.</param>
    /// <param name="domain">The reverse-domain namespace (e.g. "com.peckworkslab.clipmeta").</param>
    /// <param name="fieldName">The field name (e.g. "tags").</param>
    /// <param name="value">The UTF-8 value to store.</param>
    public static void Write(BinaryWriter writer, string domain, string fieldName, string value)
    {
        byte[] domainBytes = Encoding.UTF8.GetBytes(domain);
        byte[] nameBytes = Encoding.UTF8.GetBytes(fieldName);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);

        uint meanSize = (uint)(12 + domainBytes.Length);  // 8 box header + 4 FullBox prefix + domain
        uint nameSize = (uint)(12 + nameBytes.Length);
        uint dataSize = (uint)(DataOverhead + valueBytes.Length);
        uint totalSize = (uint)(8 + meanSize + nameSize + dataSize); // 8 = ---- box header

        // ---- outer box
        BigEndianWriter.WriteBoxHeader(writer, totalSize, "----");

        // mean (FullBox: version=0, flags=0, then domain string)
        BigEndianWriter.WriteBoxHeader(writer, meanSize, "mean");
        BigEndianWriter.WriteFullBoxPrefix(writer, 0, 0);
        writer.Write(domainBytes);

        // name (FullBox: version=0, flags=0, then field name string)
        BigEndianWriter.WriteBoxHeader(writer, nameSize, "name");
        BigEndianWriter.WriteFullBoxPrefix(writer, 0, 0);
        writer.Write(nameBytes);

        // data (NOT a FullBox in the traditional sense, but has a similar header)
        // version=0, type indicator=1 (UTF-8), locale=0
        BigEndianWriter.WriteBoxHeader(writer, dataSize, "data");
        writer.Write((byte)0);           // version
        writer.Write((byte)0);           // type indicator high byte
        writer.Write((byte)0);           // type indicator mid byte
        writer.Write((byte)1);           // type indicator low byte = 1 (UTF-8)
        writer.Write((byte)0);           // locale bytes (4 × 0)
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(valueBytes);
    }

    /// <summary>
    /// Calculates the byte size a <c>----</c> atom will occupy for the given inputs.
    /// Useful for pre-calculating size deltas before writing.
    /// </summary>
    public static uint CalculateSize(string domain, string fieldName, string value)
    {
        uint meanSize = (uint)(12 + Encoding.UTF8.GetByteCount(domain));
        uint nameSize = (uint)(12 + Encoding.UTF8.GetByteCount(fieldName));
        uint dataSize = (uint)(DataOverhead + Encoding.UTF8.GetByteCount(value));
        return 8 + meanSize + nameSize + dataSize;
    }
}
```

- [ ] **Step 7.4: Run tests, expect all pass**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "FreeformAtomWriterTests"
```
Expected: `5 passing`

- [ ] **Step 7.5: Commit**

```powershell
git add ClipMeta.Core/Write/FreeformAtomWriter.cs clipmetascribe.Tests/FreeformAtomWriterTests.cs
git commit -m "feat: FreeformAtomWriter writes ---- atoms with correct FullBox prefixes on mean/name"
```

---

## Task 8: FileLogger (TDD)

**Files:**
- Create: `ClipMeta.Core/Logging/FileLogger.cs`
- Create: `clipmetascribe.Tests/FileLoggerTests.cs`

- [ ] **Step 8.1: Write failing tests**

`clipmetascribe.Tests/FileLoggerTests.cs`:
```csharp
using ClipMeta.Core.Abstractions;
using ClipMeta.Core.Logging;

namespace ClipMetaScribe.Tests;

[TestClass]
public class FileLoggerTests
{
    private string _logDir = string.Empty;

    [TestInitialize]
    public void Setup() => _logDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true);
    }

    [TestMethod]
    public void Log_SimpleMessage_WrittenToFile()
    {
        string logPath = Path.Combine(_logDir, "clipmeta.log");
        Directory.CreateDirectory(_logDir);
        var logger = new FileLogger(logPath, LogLevel.Simple);

        logger.Log("WRITE clip001.mp4 OK");

        string content = File.ReadAllText(logPath);
        Assert.IsTrue(content.Contains("WRITE clip001.mp4 OK"));
    }

    [TestMethod]
    public void LogVerbose_WhenSimpleLevel_NotWritten()
    {
        string logPath = Path.Combine(_logDir, "clipmeta.log");
        Directory.CreateDirectory(_logDir);
        var logger = new FileLogger(logPath, LogLevel.Simple);

        logger.LogVerbose("[V] stco adjusted");

        string content = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        Assert.IsFalse(content.Contains("[V] stco adjusted"));
    }

    [TestMethod]
    public void LogVerbose_WhenVerboseLevel_WrittenWithPrefix()
    {
        string logPath = Path.Combine(_logDir, "clipmeta.log");
        Directory.CreateDirectory(_logDir);
        var logger = new FileLogger(logPath, LogLevel.Verbose);

        logger.LogVerbose("stco adjusted");

        string content = File.ReadAllText(logPath);
        Assert.IsTrue(content.Contains("[V]"));
        Assert.IsTrue(content.Contains("stco adjusted"));
    }

    [TestMethod]
    public void Log_EntryIncludesTimestamp()
    {
        string logPath = Path.Combine(_logDir, "clipmeta.log");
        Directory.CreateDirectory(_logDir);
        var logger = new FileLogger(logPath, LogLevel.Simple);

        logger.Log("test entry");

        string content = File.ReadAllText(logPath);
        // Timestamp format: [2026-05-21 14:32:01]
        Assert.IsTrue(content.Contains("[202"), $"Expected timestamp in log, got: {content}");
    }

    [TestMethod]
    public void Rotation_WhenFileTooLarge_RotatesFile()
    {
        string logPath = Path.Combine(_logDir, "clipmeta.log");
        Directory.CreateDirectory(_logDir);
        // Pre-create an oversized log file (just over 10 MB)
        File.WriteAllBytes(logPath, new byte[10 * 1024 * 1024 + 1]);

        var logger = new FileLogger(logPath, LogLevel.Simple);
        logger.Log("trigger rotation");

        Assert.IsTrue(File.Exists(logPath + ".1"), "Previous log should be rotated to .1");
        Assert.IsTrue(new FileInfo(logPath).Length < 10 * 1024 * 1024,
            "Active log file should be smaller than 10 MB after rotation");
    }
}
```

- [ ] **Step 8.2: Run tests, expect compile failure**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "FileLoggerTests"
```
Expected: build fails with `CS0246: The type or namespace name 'FileLogger' could not be found`

- [ ] **Step 8.3: Implement FileLogger**

`ClipMeta.Core/Logging/FileLogger.cs`:
```csharp
using ClipMeta.Core.Abstractions;

namespace ClipMeta.Core.Logging;

/// <summary>
/// Writes structured log entries to a file.
/// Rotates at 10 MB; keeps at most 3 log files (oldest deleted when limit is reached).
/// </summary>
public sealed class FileLogger : IClipMetaLogger
{
    private readonly string _logPath;
    private readonly object _lock = new();

    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const int MaxLogFiles = 3;

    /// <inheritdoc/>
    public LogLevel Level { get; }

    /// <summary>Creates a FileLogger that writes to the given path.</summary>
    public FileLogger(string logPath, LogLevel level = LogLevel.Simple)
    {
        _logPath = logPath;
        Level = level;
        // Path.GetDirectoryName("clipmeta.log") returns "" not null, guard before CreateDirectory.
        string? dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <inheritdoc/>
    public void Log(string message) => Write(message);

    /// <inheritdoc/>
    public void LogVerbose(string message)
    {
        if (Level == LogLevel.Verbose)
            Write($"[V] {message}");
    }

    private void Write(string message)
    {
        string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        lock (_lock)
        {
            RotateIfNeeded();
            File.AppendAllText(_logPath, entry);
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logPath)) return;
        var fi = new FileInfo(_logPath);
        if (fi.Length < MaxFileSizeBytes) return;

        // Shift .log → .log.1, .log.1 → .log.2, delete .log.(MaxLogFiles-1)
        for (int i = MaxLogFiles - 1; i >= 1; i--)
        {
            string old = $"{_logPath}.{i}";
            string newer = i == 1 ? _logPath : $"{_logPath}.{i - 1}";
            if (File.Exists(old)) File.Delete(old);
            if (File.Exists(newer)) File.Move(newer, old);
        }
    }
}
```

- [ ] **Step 8.4: Run tests, expect all pass**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "FileLoggerTests"
```
Expected: `5 passing`

- [ ] **Step 8.5: Commit**

```powershell
git add ClipMeta.Core/Logging/FileLogger.cs clipmetascribe.Tests/FileLoggerTests.cs
git commit -m "feat: add FileLogger with rotation and verbose level support"
```

---

## Task 9: Normalization (TDD)

**Files:**
- Create: `ClipMeta.Core/Write/Normalizer.cs`
- Create: `clipmetascribe.Tests/NormalizationTests.cs`

- [ ] **Step 9.1: Write failing tests**

`clipmetascribe.Tests/NormalizationTests.cs`:
```csharp
using ClipMeta.Core.Write;

namespace ClipMetaScribe.Tests;

[TestClass]
public class NormalizationTests
{
    [TestMethod]
    public void NormalizeTag_Lowercase()
        => Assert.AreEqual("market garden", Normalizer.NormalizeTag("Market Garden"));

    [TestMethod]
    public void NormalizeTag_Trims()
        => Assert.AreEqual("market garden", Normalizer.NormalizeTag("  market garden  "));

    [TestMethod]
    public void NormalizePipeList_Deduplicates()
    {
        string result = Normalizer.NormalizePipeList("headshot|funny|headshot");
        Assert.AreEqual("headshot|funny", result);
    }

    [TestMethod]
    public void NormalizePipeList_Lowercases()
    {
        string result = Normalizer.NormalizePipeList("Market Garden|Funny Moment");
        Assert.AreEqual("market garden|funny moment", result);
    }

    [TestMethod]
    public void NormalizePipeList_Trims()
    {
        string result = Normalizer.NormalizePipeList(" headshot | funny ");
        Assert.AreEqual("headshot|funny", result);
    }

    [TestMethod]
    public void AppendToPipeList_NewItem_Appended()
    {
        string result = Normalizer.AppendToPipeList("headshot|funny", "rocket jump");
        Assert.AreEqual("headshot|funny|rocket jump", result);
    }

    [TestMethod]
    public void AppendToPipeList_Duplicate_NotAdded()
    {
        string result = Normalizer.AppendToPipeList("headshot|funny", "headshot");
        Assert.AreEqual("headshot|funny", result);
    }

    [TestMethod]
    public void NormalizeTimecode_SecondsOnly_ExpandsToHHMMSS()
        => Assert.AreEqual("00:00:45", Normalizer.NormalizeTimecode("45"));

    [TestMethod]
    public void NormalizeTimecode_MMSS_ExpandsToHHMMSS()
        => Assert.AreEqual("00:00:45", Normalizer.NormalizeTimecode("0:45"));

    [TestMethod]
    public void NormalizeTimecode_AlreadyHHMMSS_Unchanged()
        => Assert.AreEqual("00:00:45", Normalizer.NormalizeTimecode("00:00:45"));

    [TestMethod]
    public void NormalizeTimecode_WithHours_Preserved()
        => Assert.AreEqual("01:23:45", Normalizer.NormalizeTimecode("1:23:45"));

    [TestMethod]
    public void NormalizeRating_Valid_Unchanged()
        => Assert.AreEqual("4", Normalizer.NormalizeRating("4"));

    [TestMethod]
    public void NormalizeRating_OutOfRange_Clamped()
        => Assert.AreEqual("5", Normalizer.NormalizeRating("9"));

    [TestMethod]
    public void ApplyToMutation_EmptyValue_TreatedAsDelete()
    {
        var mutation = new MetadataMutation();
        mutation.SetFields["tags"] = "";
        Normalizer.ApplyToMutation(mutation);
        Assert.IsTrue(mutation.DeleteFields.Contains("tags"),
            "Empty set value should move to DeleteFields");
        Assert.IsFalse(mutation.SetFields.ContainsKey("tags"),
            "tags should be removed from SetFields after normalization");
    }
}
```

- [ ] **Step 9.2: Run tests, expect compile failure**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "NormalizationTests"
```
Expected: build fails, `Normalizer` not found.

- [ ] **Step 9.3: Implement Normalizer**

`ClipMeta.Core/Write/Normalizer.cs`:
```csharp
using ClipMeta.Core.Schema;

namespace ClipMeta.Core.Write;

/// <summary>
/// Applies canonical normalization rules before writing any metadata.
/// Rules: trim whitespace, lowercase tag values, deduplicate pipe lists,
/// canonicalize timecodes to HH:MM:SS, treat empty string as delete.
/// </summary>
public static class Normalizer
{
    /// <summary>Lowercases and trims a single tag value.</summary>
    public static string NormalizeTag(string value) => value.Trim().ToLowerInvariant();

    /// <summary>
    /// Normalizes a pipe-separated list: trims each item, lowercases, deduplicates
    /// while preserving first-occurrence order.
    /// </summary>
    public static string NormalizePipeList(string value)
    {
        var seen = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string part in value.Split('|'))
        {
            string normalized = part.Trim().ToLowerInvariant();
            if (normalized.Length > 0 && set.Add(normalized))
                seen.Add(normalized);
        }
        return string.Join("|", seen);
    }

    /// <summary>
    /// Appends <paramref name="newItem"/> to an existing pipe-separated list,
    /// normalizing and deduplicating the result.
    /// </summary>
    public static string AppendToPipeList(string existing, string newItem)
    {
        string combined = existing.Length > 0 ? $"{existing}|{newItem}" : newItem;
        return NormalizePipeList(combined);
    }

    /// <summary>
    /// Normalizes a timecode string to HH:MM:SS.
    /// Accepts: "45", "0:45", "00:00:45", "1:23:45".
    /// </summary>
    /// <exception cref="ArgumentException">When any segment is not a valid integer.</exception>
    public static string NormalizeTimecode(string value)
    {
        string[] parts = value.Trim().Split(':');
        int h = 0, m = 0, s = 0;
        if (parts.Length == 1)
        {
            if (!int.TryParse(parts[0], out s))
                throw new ArgumentException($"Invalid timecode segment: '{parts[0]}'");
        }
        else if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], out m) || !int.TryParse(parts[1], out s))
                throw new ArgumentException($"Invalid timecode format: '{value}'");
        }
        else
        {
            if (!int.TryParse(parts[0], out h) || !int.TryParse(parts[1], out m) || !int.TryParse(parts[2], out s))
                throw new ArgumentException($"Invalid timecode format: '{value}'");
        }
        return $"{h:D2}:{m:D2}:{s:D2}";
    }

    /// <summary>Normalizes a timecode pipe list (each individual timecode).</summary>
    public static string NormalizeTimecodePipeList(string value)
    {
        var parts = value.Split('|')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(NormalizeTimecode)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return string.Join("|", parts);
    }

    /// <summary>Clamps rating to 1–5. Throws <see cref="ArgumentException"/> for non-integer input.</summary>
    public static string NormalizeRating(string value)
    {
        if (int.TryParse(value.Trim(), out int r))
            return Math.Clamp(r, 1, 5).ToString();
        throw new ArgumentException($"Rating must be an integer 1–5, got: '{value.Trim()}'");
    }

    /// <summary>
    /// Applies normalization to a <see cref="MetadataMutation"/> in place:
    /// normalizes values, moves empty sets to DeleteFields.
    /// </summary>
    public static void ApplyToMutation(MetadataMutation mutation)
    {
        var toDelete = new List<string>();
        var toUpdate = new Dictionary<string, string?>();

        foreach (var (field, value) in mutation.SetFields)
        {
            if (string.IsNullOrEmpty(value))
            {
                toDelete.Add(field);
                continue;
            }
            toUpdate[field] = NormalizeValue(field, value);
        }

        foreach (string field in toDelete)
        {
            mutation.SetFields.Remove(field);
            mutation.DeleteFields.Add(field);
        }
        foreach (var (field, value) in toUpdate)
            mutation.SetFields[field] = value;

        var appendKeys = mutation.AppendFields.Keys.ToList();
        foreach (string field in appendKeys)
            mutation.AppendFields[field] = NormalizeValue(field, mutation.AppendFields[field]);
    }

    private static string NormalizeValue(string field, string value)
    {
        // Keys in mutation.SetFields are domain-qualified ("com.peckworkslab.clipmeta:tags").
        // PipeFields contains bare names ("tags"). Strip the domain prefix before comparing.
        string bareName = field;
        int colonIdx = field.IndexOf(':');
        if (colonIdx >= 0) bareName = field[(colonIdx + 1)..];

        if (ClipMetaSchema.PipeFields.Contains(bareName))
        {
            if (bareName == ClipMetaSchema.Timecode)
                return NormalizeTimecodePipeList(value);
            return NormalizePipeList(value);
        }
        if (bareName == ClipMetaSchema.Rating)
            return NormalizeRating(value);
        return value.Trim();
    }
}
```

- [ ] **Step 9.4: Run tests, expect all pass**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "NormalizationTests"
```
Expected: `13 passing`

- [ ] **Step 9.5: Commit**

```powershell
git add ClipMeta.Core/Write/Normalizer.cs clipmetascribe.Tests/NormalizationTests.cs
git commit -m "feat: add Normalizer with trim/lowercase/deduplicate/timecode canonicalization"
```

---

## Task 10: Mp4Writer, Core Pipeline (TDD, 3 Scenarios)

**Files:**
- Create: `ClipMeta.Core/Write/Mp4Writer.cs`
- Create: `clipmetascribe.Tests/Mp4WriterTests.cs`
- Create: `clipmetascribe.Tests/Mp4WriterIntegrationTests.cs`

This is the most complex task. The write engine handles three scenarios, stco/co64 adjustment, free padding, and atomic file replacement. Build it incrementally in the order of the scenarios.

- [ ] **Step 10.1: Write unit tests for Scenario 1 (update existing `----` atom)**

`clipmetascribe.Tests/Mp4WriterTests.cs`, start with just Scenario 1 tests:
```csharp
using ClipMeta.Core.Logging;
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;
using ClipMeta.Core.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class Mp4WriterTests
{
    private string _tempFile = string.Empty;

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        string tmp = _tempFile + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
    }

    // ── Scenario 1: Update existing ---- atom ─────────────────────────────────

    [TestMethod]
    public void Write_UpdateExistingAtom_ValueChanged()
    {
        // Build a file with our domain:tags atom already present
        using var ms = MinimalMp4Builder.BuildMp4WithStco(
            chunkOffset: 9999, // doesn't matter for this test
            ClipMetaSchema.Domain, "tags", "old value");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:tags"] = "new value";

        var writer = new Mp4Writer();
        writer.WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        // Re-parse and verify
        var root = Mp4Parser.ParseFile(_tempFile);
        var tagsNode = FindFreeformAtom(root, "tags");
        Assert.IsNotNull(tagsNode, "tags atom should still exist after update");
        Assert.IsTrue(tagsNode.DisplayValue?.Contains("new value"),
            $"Expected 'new value', got: {tagsNode.DisplayValue}");
    }

    [TestMethod]
    public void Write_DryRun_FileUnchanged()
    {
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "tags", "original");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);
        byte[] before = File.ReadAllBytes(_tempFile);

        var mutation = new MetadataMutation { DryRun = true };
        mutation.SetFields[$"{ClipMetaSchema.Domain}:tags"] = "changed";

        var writer = new Mp4Writer();
        writer.WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        byte[] after = File.ReadAllBytes(_tempFile);
        CollectionAssert.AreEqual(before, after, "Dry run must not modify the file.");
    }

    [TestMethod]
    public void Write_TempFileCleanedUp_OnSuccess()
    {
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "tags", "v");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:tags"] = "v2";

        new Mp4Writer().WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        Assert.IsFalse(File.Exists(_tempFile + ".tmp"), "Temp file should be deleted after success.");
    }

    // ── Scenario 2: Append to existing ilst ───────────────────────────────────

    [TestMethod]
    public void Write_AppendToExistingIlst_NewAtomPresent()
    {
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "game", "TF2");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:tags"] = "headshot";  // new field

        new Mp4Writer().WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(_tempFile);
        var gameNode = FindFreeformAtom(root, "game");
        var tagsNode = FindFreeformAtom(root, "tags");

        Assert.IsNotNull(gameNode, "Original 'game' atom should be preserved");
        Assert.IsTrue(gameNode.DisplayValue?.Contains("TF2"), "game value unchanged");
        Assert.IsNotNull(tagsNode, "New 'tags' atom should be present");
        Assert.IsTrue(tagsNode.DisplayValue?.Contains("headshot"), "tags value correct");
    }

    // ── Scenario 3: Create from scratch ───────────────────────────────────────

    [TestMethod]
    public void Write_CreateFromScratch_IlstAndHdlrCreated()
    {
        // Build a file with NO udta/meta/ilst at all
        byte[] moov = MinimalMp4Builder.MoovBox(null); // no udta
        byte[] mdat = MinimalMp4Builder.MdatBox();
        _tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".mp4");
        File.WriteAllBytes(_tempFile, moov.Concat(mdat).ToArray());

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:game"] = "Team Fortress 2";

        new Mp4Writer().WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(_tempFile);
        var moovNode = root.Children.First(c => c.Type == "moov");
        var udtaNode = moovNode.Children.FirstOrDefault(c => c.Type == "udta");
        Assert.IsNotNull(udtaNode, "udta box must be created");

        var metaNode = udtaNode.Children.FirstOrDefault(c => c.Type == "meta");
        Assert.IsNotNull(metaNode, "meta box must be created");

        var hdlrNode = metaNode!.Children.FirstOrDefault(c => c.Type == "hdlr");
        Assert.IsNotNull(hdlrNode, "hdlr box must be created inside meta (required for QuickTime/Final Cut)");

        var ilstNode = metaNode.Children.FirstOrDefault(c => c.Type == "ilst");
        Assert.IsNotNull(ilstNode, "ilst box must be created");

        var gameNode = FindFreeformAtom(root, "game");
        Assert.IsNotNull(gameNode, "game atom should be present");
        Assert.IsTrue(gameNode!.DisplayValue?.Contains("Team Fortress 2"), "game value correct");
    }

    // ── stco/co64 adjustment ──────────────────────────────────────────────────

    [TestMethod]
    public void Write_AfterWrite_FileStillParseable()
    {
        // Structural smoke test: if stco adjustment corrupted the file, ParseFile will throw.
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "game", "TF2");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:notes"] = "testing stco paths";

        new Mp4Writer().WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(_tempFile);
        Assert.IsNotNull(root);
        Assert.IsTrue(root.Children.Count > 0);
    }

    [TestMethod]
    public void Write_SchemaVersionStamped_OnEveryWrite()
    {
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "game", "TF2");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:tags"] = "headshot";
        new Mp4Writer().WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(_tempFile);
        var schemaNode = FindFreeformAtom(root, ClipMetaSchema.Schema);
        Assert.IsNotNull(schemaNode, "schema version atom must be present after write");
        Assert.IsTrue(schemaNode!.DisplayValue?.Contains(ClipMetaSchema.SchemaVersion),
            $"schema value should be '1', got: {schemaNode.DisplayValue}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BoxNode? FindFreeformAtom(BoxNode root, string fieldName)
    {
        string key = $"{ClipMetaSchema.Domain}:{fieldName}";
        return FindNode(root, n => n.EditableKey == key);
    }

    private static BoxNode? FindNode(BoxNode node, Func<BoxNode, bool> predicate)
    {
        if (predicate(node)) return node;
        foreach (var child in node.Children)
        {
            var found = FindNode(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

}
```

- [ ] **Step 10.2: Run tests, expect compile failure (Mp4Writer not yet written)**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "Mp4WriterTests"
```
Expected: build fails, `Mp4Writer` not found.

- [ ] **Step 10.3: Implement Mp4Writer**

`ClipMeta.Core/Write/Mp4Writer.cs`, this is the largest class in the project. Implement it in full:

```csharp
using System.Text;
using ClipMeta.Core.Abstractions;
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;

namespace ClipMeta.Core.Write;

/// <summary>
/// Writes clipmeta metadata mutations into MP4 files using a safe temp-file strategy.
/// The source file is NEVER opened for writing. If any step fails, the original is untouched.
/// </summary>
public sealed class Mp4Writer : IMediaWriter
{
    private const int FreePaddingSize = 512; // bytes of 'free' box appended on first write

    /// <inheritdoc/>
    public bool CanWrite(string filePath) =>
        Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void WriteMetadata(string filePath, MetadataMutation mutation, IClipMetaLogger logger)
    {
        if (mutation.DryRun)
        {
            logger.Log($"DRY RUN, no files will be modified: {filePath}");
            return;
        }

        Normalizer.ApplyToMutation(mutation);

        // Stamp schema version on every write
        mutation.SetFields.TryAdd(ClipMetaSchema.AtomName(ClipMetaSchema.Schema), ClipMetaSchema.SchemaVersion);

        string tempPath = filePath + ".tmp";
        try
        {
            // Detect file lock before committing to the operation
            using (var lockTest = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"'{Path.GetFileName(filePath)}' is currently open by another process. " +
                $"Close the file and try again.", ex);
        }

        logger.Log($"WRITE {Path.GetFileName(filePath)} begin");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Parse source
            var root = Mp4Parser.ParseFile(filePath);
            DetectFragmented(root, filePath);
            logger.LogVerbose($"PARSE {CountBoxes(root)} boxes");

            // Pre-process AppendFields into SetFields using current atom values from the parsed file.
            // Must happen BEFORE DetermineScenario so the append values participate in scenario routing.
            foreach (var (key, appendValue) in mutation.AppendFields.ToList())
            {
                var existingNode = FindEditableNode(root, key);
                string current = existingNode?.DisplayValue is { } dv ? dv[1..^1] : string.Empty;
                string combined = string.IsNullOrEmpty(current)
                    ? appendValue  // already normalized by ApplyToMutation above
                    : Normalizer.AppendToPipeList(current, appendValue);
                mutation.SetFields[key] = combined;
            }
            mutation.AppendFields.Clear();

            // Determine write scenario and build new ilst contents
            var (scenario, ilstChildren, newFields) = DetermineScenario(root, mutation);
            logger.LogVerbose($"WRITE scenario={scenario}");

            // Calculate moov size delta
            long originalMoovSize = GetMoovSize(root);
            long newMoovSize = CalculateNewMoovSize(root, scenario, ilstChildren, newFields, mutation);
            long delta = newMoovSize - originalMoovSize;
            logger.LogVerbose($"WRITE delta={delta:+#;-#;0} bytes");

            // Check whether mdat follows moov (determines if stco adjustment needed)
            bool mdatFollowsMoov = MdatFollowsMoov(root);

            // Write temp file
            WriteToTemp(filePath, tempPath, root, mutation, scenario, ilstChildren, newFields,
                        delta, mdatFollowsMoov, logger);

            // Verify round-trip
            var verifyRoot = Mp4Parser.ParseFile(tempPath);
            VerifyWrite(verifyRoot, mutation, filePath);
            logger.LogVerbose($"VERIFY temp file re-parsed OK {CountBoxes(verifyRoot)} boxes intact");

            // Atomic swap
            File.Replace(tempPath, filePath, destinationBackupFileName: null);
            logger.LogVerbose($"SWAP {Path.GetFileName(filePath)} ← {Path.GetFileName(tempPath)}");

            sw.Stop();
            logger.Log($"WRITE {Path.GetFileName(filePath)} OK {sw.ElapsedMilliseconds}ms");
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
            throw;
        }
    }

    // ── Scenario determination ─────────────────────────────────────────────────

    private enum WriteScenario { Update, Append, Create }

    private static (WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields)
        DetermineScenario(BoxNode root, MetadataMutation mutation)
    {
        var ilst = FindIlst(root);
        var newFields = CollectNewFields(mutation);

        if (ilst == null)
            return (WriteScenario.Create, new(), newFields);

        var existingChildren = ilst.Children.ToList();
        bool anyUpdate = newFields.Keys.Any(k => existingChildren.Any(c => c.EditableKey == k))
                      || mutation.DeleteFields.Any(k => existingChildren.Any(c => c.EditableKey == k));

        return anyUpdate
            ? (WriteScenario.Update, existingChildren, newFields)
            : (WriteScenario.Append, existingChildren, newFields);
    }

    private static Dictionary<string, string> CollectNewFields(MetadataMutation mutation)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in mutation.SetFields)
            if (!string.IsNullOrEmpty(v)) fields[k] = v!;
        return fields;
    }

    // ── Core write ─────────────────────────────────────────────────────────────

    private static void WriteToTemp(
        string sourcePath, string tempPath, BoxNode root, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields,
        long delta, bool mdatFollowsMoov, IClipMetaLogger logger)
    {
        using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var srcReader = new BinaryReader(src, Encoding.Latin1, leaveOpen: true);
        using var dstWriter = new BinaryWriter(dst, Encoding.Latin1, leaveOpen: true);

        foreach (var topBox in root.Children)
        {
            if (topBox.Type == "moov")
                WriteMoov(srcReader, dstWriter, topBox, mutation, scenario,
                          existingIlstChildren, newFields, delta, mdatFollowsMoov, logger);
            else
                CopyBoxVerbatim(srcReader, dstWriter, topBox);
        }
    }

    private static void WriteMoov(
        BinaryReader src, BinaryWriter dst, BoxNode moov, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields,
        long delta, bool mdatFollowsMoov, IClipMetaLogger logger)
    {
        // We need to know moov's new size before writing its header.
        // Serialize moov content to a temp buffer, then prepend the correct size.
        using var moovBuf = new MemoryStream();
        using var moovWriter = new BinaryWriter(moovBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var child in moov.Children)
        {
            if (child.Type == "trak")
                WriteTrak(src, moovWriter, child, delta, mdatFollowsMoov, logger);
            else if (child.Type == "udta")
                WriteUdta(src, moovWriter, child, mutation, scenario,
                          existingIlstChildren, newFields);
            else if (child.Type == "mvhd")
                CopyBoxVerbatim(src, moovWriter, child);
            else
                CopyBoxVerbatim(src, moovWriter, child);
        }

        // Handle Scenario 3: create udta/meta/ilst from scratch when no udta exists
        if (scenario == WriteScenario.Create && !moov.Children.Any(c => c.Type == "udta"))
            WriteNewUdtaChain(moovWriter, newFields);

        uint newMoovSize = (uint)(8 + moovBuf.Length);
        BigEndianWriter.WriteBoxHeader(dst, newMoovSize, "moov");
        moovBuf.Position = 0;
        moovBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteTrak(
        BinaryReader src, BinaryWriter dst, BoxNode trak,
        long delta, bool mdatFollowsMoov, IClipMetaLogger logger)
    {
        using var trakBuf = new MemoryStream();
        using var trakWriter = new BinaryWriter(trakBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var child in trak.Children)
        {
            if (child.Type == "mdia")
                WriteMdia(src, trakWriter, child, delta, mdatFollowsMoov, logger);
            else
                CopyBoxVerbatim(src, trakWriter, child);
        }

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + trakBuf.Length), "trak");
        trakBuf.Position = 0;
        trakBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteMdia(
        BinaryReader src, BinaryWriter dst, BoxNode mdia,
        long delta, bool mdatFollowsMoov, IClipMetaLogger logger)
    {
        using var mdiaBuf = new MemoryStream();
        using var mdiaWriter = new BinaryWriter(mdiaBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var child in mdia.Children)
        {
            if (child.Type == "minf")
                WriteMinf(src, mdiaWriter, child, delta, mdatFollowsMoov, logger);
            else
                CopyBoxVerbatim(src, mdiaWriter, child);
        }

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + mdiaBuf.Length), "mdia");
        mdiaBuf.Position = 0;
        mdiaBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteMinf(
        BinaryReader src, BinaryWriter dst, BoxNode minf,
        long delta, bool mdatFollowsMoov, IClipMetaLogger logger)
    {
        using var minfBuf = new MemoryStream();
        using var minfWriter = new BinaryWriter(minfBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var child in minf.Children)
        {
            if (child.Type == "stbl")
                WriteStbl(src, minfWriter, child, delta, mdatFollowsMoov, logger);
            else
                CopyBoxVerbatim(src, minfWriter, child);
        }

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + minfBuf.Length), "minf");
        minfBuf.Position = 0;
        minfBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteStbl(
        BinaryReader src, BinaryWriter dst, BoxNode stbl,
        long delta, bool mdatFollowsMoov, IClipMetaLogger logger)
    {
        using var stblBuf = new MemoryStream();
        using var stblWriter = new BinaryWriter(stblBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var child in stbl.Children)
        {
            if (child.Type == "stco" && delta != 0 && mdatFollowsMoov)
                WriteAdjustedStco(src, stblWriter, child, delta, logger);
            else if (child.Type == "co64" && delta != 0 && mdatFollowsMoov)
                WriteAdjustedCo64(src, stblWriter, child, delta, logger);
            else
                CopyBoxVerbatim(src, stblWriter, child);
        }

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + stblBuf.Length), "stbl");
        stblBuf.Position = 0;
        stblBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteAdjustedStco(
        BinaryReader src, BinaryWriter dst, BoxNode stco, long delta, IClipMetaLogger logger)
    {
        // stco FullBox: version(1) + flags(3) + entry_count(4) + entries(4 each)
        src.BaseStream.Position = stco.FileOffset + stco.HeaderSize;
        byte ver = src.ReadByte();
        byte f1 = src.ReadByte(), f2 = src.ReadByte(), f3 = src.ReadByte();
        uint count = BigEndianReader.ReadUInt32(src);

        using var content = new MemoryStream();
        using var cw = new BinaryWriter(content, Encoding.Latin1, leaveOpen: true);
        cw.Write(ver); cw.Write(f1); cw.Write(f2); cw.Write(f3);
        BigEndianWriter.WriteUInt32(cw, count);

        for (uint i = 0; i < count; i++)
        {
            uint original = BigEndianReader.ReadUInt32(src);
            long adjusted = (long)original + delta;
            if (adjusted > uint.MaxValue)
                throw new InvalidOperationException(
                    $"stco offset overflow at entry {i}: {adjusted} > UInt32.MaxValue.");
            if (adjusted < 0)
                throw new InvalidOperationException(
                    $"stco offset underflow at entry {i}: {adjusted} < 0. Metadata shrink produced negative offset.");
            BigEndianWriter.WriteUInt32(cw, (uint)adjusted);
        }

        logger.LogVerbose($"STCO {count} entries += {delta}");
        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + content.Length), "stco");
        content.Position = 0;
        content.CopyTo(dst.BaseStream);
    }
```

```csharp
    private static void WriteAdjustedCo64(
        BinaryReader src, BinaryWriter dst, BoxNode co64, long delta, IClipMetaLogger logger)
    {
        src.BaseStream.Position = co64.FileOffset + co64.HeaderSize;
        byte ver = src.ReadByte();
        byte f1 = src.ReadByte(), f2 = src.ReadByte(), f3 = src.ReadByte();
        uint count = BigEndianReader.ReadUInt32(src);

        using var content = new MemoryStream();
        using var cw = new BinaryWriter(content, Encoding.Latin1, leaveOpen: true);
        cw.Write(ver); cw.Write(f1); cw.Write(f2); cw.Write(f3);
        BigEndianWriter.WriteUInt32(cw, count);

        for (uint i = 0; i < count; i++)
        {
            ulong original = BigEndianReader.ReadUInt64(src);
            // Cast through long to detect underflow, (ulong)(-1) would silently wrap.
            long adjusted = (long)original + delta;
            if (adjusted < 0)
                throw new InvalidOperationException(
                    $"co64 offset underflow at entry {i}: {adjusted} < 0. Metadata shrink produced negative offset.");
            BigEndianWriter.WriteUInt64(cw, (ulong)adjusted);
        }

        logger.LogVerbose($"CO64 {count} entries += {delta}");
        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + content.Length), "co64");
        content.Position = 0;
        content.CopyTo(dst.BaseStream);
    }

    // ── ilst writing (Scenarios 1, 2, 3) ─────────────────────────────────────

    private static void WriteUdta(
        BinaryReader src, BinaryWriter dst, BoxNode udta, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields)
    {
        using var udtaBuf = new MemoryStream();
        using var udtaWriter = new BinaryWriter(udtaBuf, Encoding.Latin1, leaveOpen: true);

        bool hasMeta = udta.Children.Any(c => c.Type == "meta");
        foreach (var child in udta.Children)
        {
            if (child.Type == "meta")
                WriteMeta(src, udtaWriter, child, mutation, scenario, existingIlstChildren, newFields);
            else
                CopyBoxVerbatim(src, udtaWriter, child);
        }

        // If udta exists but has no meta child (and therefore no ilst), the Create scenario
        // dispatched here via WriteMoov without entering WriteNewUdtaChain. Synthesize meta chain.
        if (!hasMeta && scenario == WriteScenario.Create)
            WriteNewMetaChain(udtaWriter, newFields);

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + udtaBuf.Length), "udta");
        udtaBuf.Position = 0;
        udtaBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteNewMetaChain(BinaryWriter dst, Dictionary<string, string> newFields)
    {
        // Write meta (FullBox) → hdlr + ilst, the inner portion of WriteNewUdtaChain.
        using var ilstBuf = new MemoryStream();
        using var ilstWriter = new BinaryWriter(ilstBuf, Encoding.Latin1, leaveOpen: true);
        foreach (var (key, value) in newFields)
        {
            int colonIdx = key.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx < 0) continue;
            FreeformAtomWriter.Write(ilstWriter, key[..colonIdx], key[(colonIdx + 1)..], value);
        }
        byte[] ilstBytes = ilstBuf.ToArray();
        uint ilstSize = (uint)(8 + ilstBytes.Length);

        byte[] hdlrBody = new byte[20];
        Encoding.Latin1.GetBytes("mdir").CopyTo(hdlrBody, 4);
        byte[] hdlrBytes = BuildFullBox("hdlr", 0, 0, hdlrBody);

        using var metaBuf = new MemoryStream();
        using var metaWriter = new BinaryWriter(metaBuf, Encoding.Latin1, leaveOpen: true);
        metaWriter.Write((byte)0); metaWriter.Write((byte)0); metaWriter.Write((byte)0); metaWriter.Write((byte)0);
        metaWriter.Write(hdlrBytes);
        BigEndianWriter.WriteBoxHeader(metaWriter, ilstSize, "ilst");
        metaWriter.Write(ilstBytes);
        byte[] metaBytes = metaBuf.ToArray();

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + metaBytes.Length), "meta");
        dst.Write(metaBytes);
    }

    private static void WriteMeta(
        BinaryReader src, BinaryWriter dst, BoxNode meta, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields)
    {
        using var metaBuf = new MemoryStream();
        using var metaWriter = new BinaryWriter(metaBuf, Encoding.Latin1, leaveOpen: true);

        // meta is a FullBox, write version+flags first
        metaWriter.Write(meta.Version);
        metaWriter.Write((byte)(meta.Flags >> 16));
        metaWriter.Write((byte)(meta.Flags >> 8));
        metaWriter.Write((byte)meta.Flags);

        foreach (var child in meta.Children)
        {
            if (child.Type == "ilst")
                WriteIlst(src, metaWriter, child, mutation, scenario,
                          existingIlstChildren, newFields);
            else
                CopyBoxVerbatim(src, metaWriter, child);
        }

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + metaBuf.Length), "meta");
        metaBuf.Position = 0;
        metaBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteIlst(
        BinaryReader src, BinaryWriter dst, BoxNode ilst, MetadataMutation mutation,
        WriteScenario scenario, List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields)
    {
        using var ilstBuf = new MemoryStream();
        using var ilstWriter = new BinaryWriter(ilstBuf, Encoding.Latin1, leaveOpen: true);

        var writtenKeys = new HashSet<string>(StringComparer.Ordinal);

        // Stream-copy existing atoms; replace matching keys with updated value.
        // Skip "free" padding boxes, they are re-added at the end as fresh padding.
        foreach (var child in ilst.Children)
        {
            if (child.Type == "free") continue; // skip old padding; will be re-added below

            string key = child.EditableKey ?? string.Empty;

            if (mutation.DeleteFields.Contains(key))
                continue; // omit deleted atoms

            if (mutation.ClearAll && key.StartsWith(ClipMetaSchema.Domain + ":", StringComparison.Ordinal))
                continue; // omit all our atoms when clearing

            if (newFields.TryGetValue(key, out string? newValue))
            {
                // This is an atom we're updating (Scenario 1)
                if (child.Type == "----")
                {
                    // Extract domain and field from the key "domain:field"
                    int colonIdx = key.IndexOf(':', StringComparison.Ordinal);
                    string domain = key[..colonIdx];
                    string field = key[(colonIdx + 1)..];
                    FreeformAtomWriter.Write(ilstWriter, domain, field, newValue);
                }
                else
                {
                    // Unknown non-freeform editable atom, copy verbatim (don't corrupt it)
                    CopyBoxVerbatim(src, ilstWriter, child);
                }
                writtenKeys.Add(key);
            }
            else
            {
                CopyBoxVerbatim(src, ilstWriter, child);
            }
        }

        // Append any new keys not already written (Scenario 2)
        foreach (var (key, value) in newFields)
        {
            if (writtenKeys.Contains(key)) continue;
            int colonIdx = key.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx < 0) continue;
            string domain = key[..colonIdx];
            string field = key[(colonIdx + 1)..];
            FreeformAtomWriter.Write(ilstWriter, domain, field, value);
        }

        // AppendFields are pre-processed into SetFields in WriteMetadata before this method is called.
        // No append handling is needed here.

        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + ilstBuf.Length), "ilst");
        ilstBuf.Position = 0;
        ilstBuf.CopyTo(dst.BaseStream);
    }

    private static void WriteNewUdtaChain(BinaryWriter dst, Dictionary<string, string> newFields)
    {
        // Build: udta → meta (FullBox) → hdlr + ilst
        using var ilstBuf = new MemoryStream();
        using var ilstWriter = new BinaryWriter(ilstBuf, Encoding.Latin1, leaveOpen: true);

        foreach (var (key, value) in newFields)
        {
            int colonIdx = key.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx < 0) continue;
            string domain = key[..colonIdx];
            string field = key[(colonIdx + 1)..];
            FreeformAtomWriter.Write(ilstWriter, domain, field, value);
        }

        byte[] ilstBytes = ilstBuf.ToArray();
        uint ilstSize = (uint)(8 + ilstBytes.Length);

        // hdlr: FullBox, handler_type="mdir", 20 bytes body (pre_defined + handler_type + 12 reserved)
        byte[] hdlrBody = new byte[20];
        Encoding.Latin1.GetBytes("mdir").CopyTo(hdlrBody, 4);
        uint hdlrSize = (uint)(8 + 4 + hdlrBody.Length); // 8 box header + 4 FullBox prefix + body
        byte[] hdlrBytes = BuildFullBox("hdlr", 0, 0, hdlrBody);

        uint metaContentSize = (uint)(4 + hdlrBytes.Length + ilstSize); // 4 = FullBox prefix
        uint metaSize = (uint)(8 + metaContentSize);

        // Write udta → meta → hdlr + ilst
        using var metaBuf = new MemoryStream();
        using var metaWriter = new BinaryWriter(metaBuf, Encoding.Latin1, leaveOpen: true);
        metaWriter.Write((byte)0); // version
        metaWriter.Write((byte)0); metaWriter.Write((byte)0); metaWriter.Write((byte)0); // flags
        metaWriter.Write(hdlrBytes);
        BigEndianWriter.WriteBoxHeader(metaWriter, ilstSize, "ilst");
        metaWriter.Write(ilstBytes);

        byte[] metaBytes = metaBuf.ToArray();
        uint udtaSize = (uint)(8 + 8 + metaBytes.Length); // 8 outer + 8 meta header + meta content

        BigEndianWriter.WriteBoxHeader(dst, udtaSize, "udta");
        BigEndianWriter.WriteBoxHeader(dst, (uint)(8 + metaBytes.Length), "meta");
        dst.Write(metaBytes);
    }

    private static byte[] BuildFullBox(string type, byte version, uint flags, byte[] body)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true);
        uint size = (uint)(8 + 4 + body.Length);
        BigEndianWriter.WriteBoxHeader(bw, size, type);
        BigEndianWriter.WriteFullBoxPrefix(bw, version, flags);
        bw.Write(body);
        return ms.ToArray();
    }

    // ── Verbatim copy ─────────────────────────────────────────────────────────

    private static void CopyBoxVerbatim(BinaryReader src, BinaryWriter dst, BoxNode box)
    {
        src.BaseStream.Position = box.FileOffset;
        long bytesToCopy = (long)box.Size;
        const int ChunkSize = 65536;
        byte[] buffer = new byte[ChunkSize];
        while (bytesToCopy > 0)
        {
            int read = src.Read(buffer, 0, (int)Math.Min(bytesToCopy, ChunkSize));
            if (read == 0) break;
            dst.Write(buffer, 0, read);
            bytesToCopy -= read;
        }
    }

    // ── Size calculation helpers ───────────────────────────────────────────────

    private static long GetMoovSize(BoxNode root)
        => (long)(root.Children.FirstOrDefault(c => c.Type == "moov")?.Size ?? 0);

    private static long CalculateNewMoovSize(
        BoxNode root, WriteScenario scenario,
        List<BoxNode> existingIlstChildren, Dictionary<string, string> newFields,
        MetadataMutation mutation)
    {
        long oldIlstSize = FindIlst(root)?.Size is ulong s ? (long)s : 0;
        long newIlstSize = CalculateNewIlstSize(existingIlstChildren, newFields, mutation);
        long oldMoovSize = GetMoovSize(root);
        long delta = newIlstSize - oldIlstSize;

        if (scenario == WriteScenario.Create && FindIlst(root) == null)
        {
            // Create from scratch adds the full udta+meta+hdlr chain that is not captured by
            // ilst size alone. Overhead: udta header (8) + meta header (8) + meta FullBox
            // prefix (4) + hdlr full box header+prefix+body (8+4+20=32) = 52 bytes.
            delta += 52;
        }

        return oldMoovSize + delta;
    }

    private static long CalculateNewIlstSize(
        List<BoxNode> existing, Dictionary<string, string> newFields, MetadataMutation mutation)
    {
        long size = 8; // box header
        foreach (var child in existing)
        {
            if (child.Type == "free") continue; // WriteIlst skips free boxes; exclude from delta
            string key = child.EditableKey ?? string.Empty;
            if (mutation.DeleteFields.Contains(key)) continue;
            if (mutation.ClearAll && key.StartsWith(ClipMetaSchema.Domain + ":")) continue;

            if (newFields.TryGetValue(key, out string? newVal) && child.Type == "----")
            {
                int colon = key.IndexOf(':');
                if (colon < 0) { size += (long)child.Size; continue; } // no-domain key: keep original size
                size += FreeformAtomWriter.CalculateSize(key[..colon], key[(colon + 1)..], newVal!);
            }
            else
            {
                size += (long)child.Size;
            }
        }
        foreach (var (key, val) in newFields)
        {
            if (existing.Any(c => c.EditableKey == key)) continue;
            int colon = key.IndexOf(':');
            if (colon < 0) continue;
            size += FreeformAtomWriter.CalculateSize(key[..colon], key[(colon + 1)..], val);
        }
        return size;
    }

    // ── Fragmented MP4 detection ───────────────────────────────────────────────

    private static void DetectFragmented(BoxNode root, string filePath)
    {
        if (root.Children.Any(c => c.Type == "moof"))
            throw new UnsupportedFormatException(
                $"'{Path.GetFileName(filePath)}' uses fragmented MP4 format (contains moof boxes). " +
                $"Write is not supported for fragmented files. " +
                $"This format is common with Xbox Game Bar captures.");
    }

    // ── mdat position detection ────────────────────────────────────────────────

    private static bool MdatFollowsMoov(BoxNode root)
    {
        var moov = root.Children.FirstOrDefault(c => c.Type == "moov");
        var mdat = root.Children.FirstOrDefault(c => c.Type == "mdat");
        if (moov == null || mdat == null) return false;
        return mdat.FileOffset > moov.FileOffset;
    }

    // ── Verification ──────────────────────────────────────────────────────────

    private static void VerifyWrite(BoxNode root, MetadataMutation mutation, string originalPath)
    {
        if (!root.Children.Any(c => c.Type == "moov"))
            throw new InvalidDataException(
                $"Verification failed: moov box missing in written file for '{originalPath}'.");

        foreach (var (key, value) in mutation.SetFields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            var node = FindEditableNode(root, key);
            if (node == null)
                throw new InvalidDataException(
                    $"Verification failed: atom '{key}' not found after write of '{originalPath}'.");
        }
    }

    // ── Tree search helpers ───────────────────────────────────────────────────

    private static BoxNode? FindIlst(BoxNode root)
        => FindNode(root, n => n.Type == "ilst");

    private static BoxNode? FindEditableNode(BoxNode root, string editableKey)
        => FindNode(root, n => n.EditableKey == editableKey);

    private static BoxNode? FindNode(BoxNode node, Func<BoxNode, bool> predicate)
    {
        if (predicate(node)) return node;
        foreach (var child in node.Children)
        {
            var found = FindNode(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    private static int CountBoxes(BoxNode root)
    {
        int count = 1;
        foreach (var child in root.Children) count += CountBoxes(child);
        return count;
    }
}
```

- [ ] **Step 10.4: Run unit tests, expect pass**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "Mp4WriterTests"
```
Expected: all unit tests pass. Fix any compilation errors before continuing.

- [ ] **Step 10.5: Write integration tests using real scratch clips**

`clipmetascribe.Tests/Mp4WriterIntegrationTests.cs`:
```csharp
using ClipMeta.Core.Logging;
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;
using ClipMeta.Core.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class Mp4WriterIntegrationTests
{
    public static IEnumerable<object[]> PristineClips()
        => TestClipsLocator.AllPristine().Select(p => new object[] { p });

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_SetGameField_RoundTrips(string pristinePath)
    {
        string scratchPath = ScratchClips.Prepare(pristinePath);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2";

        new Mp4Writer().WriteMetadata(scratchPath, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(scratchPath);
        var gameNode = FindFreeformAtom(root, ClipMetaSchema.Game);
        Assert.IsNotNull(gameNode, $"game atom not found after write in {pristinePath}");
        Assert.IsTrue(gameNode!.DisplayValue?.Contains("Team Fortress 2"),
            $"game value wrong in {pristinePath}: {gameNode.DisplayValue}");
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_SetTagsField_RoundTrips(string pristinePath)
    {
        string scratchPath = ScratchClips.Prepare(pristinePath);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "rocket jump|headshot";

        new Mp4Writer().WriteMetadata(scratchPath, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(scratchPath);
        var tagsNode = FindFreeformAtom(root, ClipMetaSchema.Tags);
        Assert.IsNotNull(tagsNode, $"tags atom not found in {pristinePath}");
        Assert.IsTrue(tagsNode!.DisplayValue?.Contains("rocket jump"),
            $"tags value wrong: {tagsNode.DisplayValue}");
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_WriteAllFields_AllRoundTrip(string pristinePath)
    {
        string scratchPath = ScratchClips.Prepare(pristinePath);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Players)] = "Ben|Scott";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "market garden|funny";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Timecode)] = "00:00:45";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Rating)] = "4";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Notes)] = "Ben gets the kill";

        new Mp4Writer().WriteMetadata(scratchPath, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(scratchPath);
        foreach (string field in new[] { ClipMetaSchema.Game, ClipMetaSchema.Players,
                                          ClipMetaSchema.Tags, ClipMetaSchema.Rating, ClipMetaSchema.Notes })
        {
            var node = FindFreeformAtom(root, field);
            Assert.IsNotNull(node, $"Field '{field}' not found after write in {pristinePath}");
        }
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_ForeignAtoms_Preserved(string pristinePath)
    {
        // Check if there are any non-clipmeta editable atoms before write
        var rootBefore = Mp4Parser.ParseFile(pristinePath);
        var ilst = FindNode(rootBefore, n => n.Type == "ilst");
        var foreignAtomsBefore = ilst?.Children
            .Where(c => c.Type != "----" || !c.EditableKey!.StartsWith(ClipMetaSchema.Domain))
            .ToList() ?? new();

        if (foreignAtomsBefore.Count == 0) return; // nothing to verify

        string scratchPath = ScratchClips.Prepare(pristinePath);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "test";

        new Mp4Writer().WriteMetadata(scratchPath, mutation, NullLogger.Instance);

        var rootAfter = Mp4Parser.ParseFile(scratchPath);
        var ilstAfter = FindNode(rootAfter, n => n.Type == "ilst");
        var foreignAtomsAfter = ilstAfter?.Children
            .Where(c => c.Type != "----" || !c.EditableKey!.StartsWith(ClipMetaSchema.Domain))
            .ToList() ?? new();

        Assert.AreEqual(foreignAtomsBefore.Count, foreignAtomsAfter.Count,
            $"Foreign atom count changed. Before: {foreignAtomsBefore.Count}, After: {foreignAtomsAfter.Count}");
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_OriginalUnchanged_WhenDryRun(string pristinePath)
    {
        byte[] before = File.ReadAllBytes(pristinePath);
        var mutation = new MetadataMutation { DryRun = true };
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";

        new Mp4Writer().WriteMetadata(pristinePath, mutation, NullLogger.Instance);

        byte[] after = File.ReadAllBytes(pristinePath);
        CollectionAssert.AreEqual(before, after, $"Dry run modified {pristinePath}");
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_NoTempFileLeft_AfterSuccess(string pristinePath)
    {
        string scratchPath = ScratchClips.Prepare(pristinePath);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";

        new Mp4Writer().WriteMetadata(scratchPath, mutation, NullLogger.Instance);

        Assert.IsFalse(File.Exists(scratchPath + ".tmp"),
            $"Temp file not cleaned up for {Path.GetFileName(pristinePath)}");
    }

    [TestMethod]
    public void Write_FragmentedMp4_ThrowsUnsupportedFormatException()
    {
        // Build a fake "fragmented" MP4 by injecting a moof box at top level.
        string fragPath = Path.ChangeExtension(Path.GetTempFileName(), ".mp4");
        try
        {
            byte[] moov = MinimalMp4Builder.MoovBox(null);
            byte[] moof = MinimalMp4Builder.MoovBox(null); // Reuse MoovBox shape; rename to moof
            // Write raw bytes with "moof" FourCC
            using var ms = new MemoryStream();
            ms.Write(moov);
            // Manually write a moof-shaped box
            byte[] moofBox = new byte[8 + 8]; // minimal non-zero box
            moofBox[0] = 0; moofBox[1] = 0; moofBox[2] = 0; moofBox[3] = 16;
            System.Text.Encoding.Latin1.GetBytes("moof").CopyTo(moofBox, 4);
            ms.Write(moofBox);
            ms.Write(MinimalMp4Builder.MdatBox());
            File.WriteAllBytes(fragPath, ms.ToArray());

            var mutation = new MetadataMutation();
            mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";

            Assert.ThrowsException<UnsupportedFormatException>(() =>
                new Mp4Writer().WriteMetadata(fragPath, mutation, NullLogger.Instance));
        }
        finally
        {
            if (File.Exists(fragPath)) File.Delete(fragPath);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static BoxNode? FindFreeformAtom(BoxNode root, string fieldName)
        => FindNode(root, n => n.EditableKey == ClipMetaSchema.AtomName(fieldName));

    private static BoxNode? FindNode(BoxNode node, Func<BoxNode, bool> predicate)
    {
        if (predicate(node)) return node;
        foreach (var child in node.Children)
        {
            var found = FindNode(child, predicate);
            if (found != null) return found;
        }
        return null;
    }
}
```

- [ ] **Step 10.6: Run integration tests**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "Mp4WriterIntegrationTests"
```
Expected: all tests pass. If `Write_AllFields_RoundTrip` fails for any clip, check:
1. Is `mean`/`name` in `FullBoxTypes` in Mp4Parser? (Task 3.3)
2. Is `----` atom parsing working? (Task 3.3)
3. Does `FreeformAtomWriter.Write` produce correct FullBox prefixes? (Task 7.3)

- [ ] **Step 10.7: Run all tests to ensure nothing regressed**

```powershell
dotnet test
```
Expected: all tests pass (80 from clipmetaview.Tests + new tests from clipmetascribe.Tests).

- [ ] **Step 10.8: Commit**

```powershell
git add ClipMeta.Core/Write/Mp4Writer.cs clipmetascribe.Tests/Mp4WriterTests.cs clipmetascribe.Tests/Mp4WriterIntegrationTests.cs
git commit -m "feat: implement Mp4Writer with 3 write scenarios, stco adjustment, and fragmentation detection"
```

---

## Task 11: Search Index (TDD)

**Files:**
- Create: `ClipMeta.Core/Search/ClipMetaIndex.cs`
- Create: `ClipMeta.Core/Search/ClipMetaSearch.cs`
- Create: `clipmetascribe.Tests/SearchIndexTests.cs`

- [ ] **Step 11.1: Write failing tests**

`clipmetascribe.Tests/SearchIndexTests.cs`:
```csharp
using ClipMeta.Core.Schema;
using ClipMeta.Core.Search;
using ClipMeta.Core.Write;
using ClipMeta.Core.Logging;
using ClipMeta.Core.Mp4;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class SearchIndexTests
{
    private string _scratchDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_scratchDir);
        // Prepare scratch copies of all pristine clips in our temp dir
        foreach (string p in TestClipsLocator.AllPristine())
        {
            string dest = Path.Combine(_scratchDir, Path.GetFileName(p));
            File.Copy(p, dest, overwrite: true);
            // Tag each clip with game name so search has something to find
            var mutation = new MetadataMutation();
            mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2";
            mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "headshot";
            new ClipMeta.Core.Write.Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true);
    }

    [TestMethod]
    public void BuildIndex_CreatesIndexFile()
    {
        ClipMetaIndex.Build(_scratchDir);
        Assert.IsTrue(File.Exists(Path.Combine(_scratchDir, "clipmeta-index.json")));
    }

    [TestMethod]
    public void BuildIndex_ContainsAllTaggedFiles()
    {
        ClipMetaIndex.Build(_scratchDir);
        var index = ClipMetaIndex.Load(_scratchDir);
        int mp4Count = Directory.EnumerateFiles(_scratchDir, "*.mp4").Count();
        Assert.AreEqual(mp4Count, index.Entries.Count);
    }

    [TestMethod]
    public void Search_ByGame_ReturnsMatchingFiles()
    {
        ClipMetaIndex.Build(_scratchDir);
        var results = ClipMetaSearch.Find(_scratchDir,
            new Dictionary<string, string> { [ClipMetaSchema.Game] = "Team Fortress 2" });
        Assert.IsTrue(results.Any(), "Expected at least one result for game='Team Fortress 2'");
        Assert.IsTrue(results.All(File.Exists), "All result paths should be valid file paths");
    }

    [TestMethod]
    public void Search_AndLogic_BothConditionsMustMatch()
    {
        ClipMetaIndex.Build(_scratchDir);
        var results = ClipMetaSearch.Find(_scratchDir, new Dictionary<string, string>
        {
            [ClipMetaSchema.Game] = "Team Fortress 2",
            [ClipMetaSchema.Tags] = "headshot",
        });
        // Both conditions match → should return results
        Assert.IsTrue(results.Any());

        var noResults = ClipMetaSearch.Find(_scratchDir, new Dictionary<string, string>
        {
            [ClipMetaSchema.Game] = "Team Fortress 2",
            [ClipMetaSchema.Tags] = "DOES_NOT_EXIST_ANYWHERE",
        });
        Assert.AreEqual(0, noResults.Count(), "AND logic: unmatched tag should return 0 results");
    }

    [TestMethod]
    public void Search_WithoutIndex_FallsBackToFileScan()
    {
        // No index built, should still return results via scan
        var results = ClipMetaSearch.Find(_scratchDir,
            new Dictionary<string, string> { [ClipMetaSchema.Game] = "Team Fortress 2" });
        Assert.IsTrue(results.Any());
    }
}
```

- [ ] **Step 11.2: Run tests, expect compile failure**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "SearchIndexTests"
```

- [ ] **Step 11.3: Implement ClipMetaIndex**

`ClipMeta.Core/Search/ClipMetaIndex.cs`:
```csharp
using System.Text.Json;
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;

namespace ClipMeta.Core.Search;

/// <summary>Represents a single file's entry in the search index.</summary>
/// <remarks>
/// Uses <see cref="Dictionary{TKey,TValue}"/> (not IReadOnlyDictionary) because
/// System.Text.Json cannot deserialize abstract interface-typed constructor parameters.
/// </remarks>
public sealed record IndexEntry(
    string File,
    DateTime Mtime,
    Dictionary<string, string> Fields);

/// <summary>In-memory representation of a clipmeta-index.json file.</summary>
public sealed class ClipMetaIndexData
{
    public int Schema { get; init; } = 1;
    public DateTime Generated { get; init; }
    public List<IndexEntry> Entries { get; init; } = new();
}

/// <summary>Builds and loads the per-directory clipmeta-index.json file.</summary>
public static class ClipMetaIndex
{
    private const string IndexFileName = "clipmeta-index.json";

    /// <summary>Builds or rebuilds the index for the given directory.</summary>
    public static void Build(string directory)
    {
        var entries = new List<IndexEntry>();
        foreach (string mp4 in Directory.EnumerateFiles(directory, "*.mp4"))
        {
            var fields = ReadClipMetaFields(mp4);
            var mtime = File.GetLastWriteTimeUtc(mp4);
            entries.Add(new IndexEntry(Path.GetFileName(mp4), mtime, fields));
        }

        var data = new ClipMetaIndexData
        {
            Generated = DateTime.UtcNow,
            Entries = entries,
        };

        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, IndexFileName), json);
    }

    /// <summary>Loads an existing index file from a directory.</summary>
    public static ClipMetaIndexData Load(string directory)
    {
        string path = Path.Combine(directory, IndexFileName);
        if (!File.Exists(path)) return new ClipMetaIndexData { Entries = new() };
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ClipMetaIndexData>(json) ?? new();
    }

    /// <summary>Returns true if an index exists, covers all current MP4s, and all mtimes match.</summary>
    public static bool IsValid(string directory)
    {
        string path = Path.Combine(directory, IndexFileName);
        if (!File.Exists(path)) return false;
        var data = Load(directory);

        // If new files were added after the index was built they won't have entries, stale.
        int currentCount = Directory.EnumerateFiles(directory, "*.mp4").Count();
        if (currentCount != data.Entries.Count) return false;

        foreach (var entry in data.Entries)
        {
            string fullPath = Path.Combine(directory, entry.File);
            if (!File.Exists(fullPath)) return false;
            var currentMtime = File.GetLastWriteTimeUtc(fullPath);
            if (Math.Abs((currentMtime - entry.Mtime).TotalSeconds) > 1) return false;
        }
        return true;
    }

    private static Dictionary<string, string> ReadClipMetaFields(string filePath)
    {
        try
        {
            var root = Mp4Parser.ParseFile(filePath);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            CollectFields(root, fields);
            return fields;
        }
        catch { return new Dictionary<string, string>(); }
    }

    private static void CollectFields(BoxNode node, Dictionary<string, string> fields)
    {
        if (node.EditableKey?.StartsWith(ClipMetaSchema.Domain + ":", StringComparison.Ordinal) == true
            && node.DisplayValue != null)
        {
            string field = node.EditableKey[(ClipMetaSchema.Domain.Length + 1)..];
            fields[field] = node.DisplayValue[1..^1];
        }
        foreach (var child in node.Children)
            CollectFields(child, fields);
    }
}
```

- [ ] **Step 11.4: Implement ClipMetaSearch**

`ClipMeta.Core/Search/ClipMetaSearch.cs`:
```csharp
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;

namespace ClipMeta.Core.Search;

/// <summary>Searches a directory for clips matching given field filters.</summary>
public static class ClipMetaSearch
{
    /// <summary>
    /// Returns file paths where ALL supplied field conditions match (AND logic).
    /// Uses the index when valid; falls back to a full file scan.
    /// </summary>
    /// <param name="directory">Directory to search.</param>
    /// <param name="filters">Map of field name → required value (substring match).</param>
    /// <param name="since">Optional: only files modified on or after this date.</param>
    /// <param name="before">Optional: only files modified before this date.</param>
    public static IEnumerable<string> Find(
        string directory,
        IReadOnlyDictionary<string, string> filters,
        DateTime? since = null,
        DateTime? before = null)
    {
        if (ClipMetaIndex.IsValid(directory))
        {
            var data = ClipMetaIndex.Load(directory);
            return SearchIndex(directory, data, filters, since, before);
        }
        return SearchFiles(directory, filters, since, before);
    }

    private static IEnumerable<string> SearchIndex(
        string directory, ClipMetaIndexData data,
        IReadOnlyDictionary<string, string> filters,
        DateTime? since, DateTime? before)
    {
        foreach (var entry in data.Entries)
        {
            if (since.HasValue && entry.Mtime < since.Value) continue;
            if (before.HasValue && entry.Mtime >= before.Value) continue;
            if (MatchesAll(entry.Fields, filters))
                yield return Path.Combine(directory, entry.File);
        }
    }

    private static IEnumerable<string> SearchFiles(
        string directory,
        IReadOnlyDictionary<string, string> filters,
        DateTime? since, DateTime? before)
    {
        foreach (string mp4 in Directory.EnumerateFiles(directory, "*.mp4"))
        {
            var mtime = File.GetLastWriteTimeUtc(mp4);
            if (since.HasValue && mtime < since.Value) continue;
            if (before.HasValue && mtime >= before.Value) continue;

            try
            {
                var root = Mp4Parser.ParseFile(mp4);
                var fields = CollectFields(root);
                if (MatchesAll(fields, filters))
                    yield return mp4;
            }
            catch { /* skip unreadable files */ }
        }
    }

    private static bool MatchesAll(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> filters)
    {
        foreach (var (field, required) in filters)
        {
            if (!fields.TryGetValue(field, out string? actual)) return false;
            // Pipe-separated fields: whole-token match against each pipe-delimited item.
            // Substring matching would cause "--find tags head" to match "headshot", unexpected.
            bool found = actual.Split('|')
                .Any(item => item.Trim().Equals(required, StringComparison.OrdinalIgnoreCase));
            if (!found) return false;
        }
        return true;
    }

    private static IReadOnlyDictionary<string, string> CollectFields(BoxNode root)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectFieldsFromNode(root, fields);
        return fields;
    }

    private static void CollectFieldsFromNode(BoxNode node, Dictionary<string, string> fields)
    {
        if (node.EditableKey?.StartsWith(ClipMetaSchema.Domain + ":", StringComparison.Ordinal) == true
            && node.DisplayValue != null)
        {
            string field = node.EditableKey[(ClipMetaSchema.Domain.Length + 1)..];
            fields[field] = node.DisplayValue[1..^1];
        }
        foreach (var child in node.Children)
            CollectFieldsFromNode(child, fields);
    }
}
```

- [ ] **Step 11.5: Run tests**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "SearchIndexTests"
```
Expected: all pass.

- [ ] **Step 11.6: Commit**

```powershell
git add ClipMeta.Core/Search/ clipmetascribe.Tests/SearchIndexTests.cs
git commit -m "feat: add ClipMetaIndex and ClipMetaSearch with index/scan fallback and AND filter logic"
```

---

## Task 12: Batch Operations (TDD)

**Files:**
- Create: `ClipMeta.Core/Search/BatchOperation.cs`
- Create: `clipmetascribe.Tests/BatchOperationTests.cs`

- [ ] **Step 12.1: Write failing tests**

`clipmetascribe.Tests/BatchOperationTests.cs`:
```csharp
using ClipMeta.Core.Logging;
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;
using ClipMeta.Core.Search;
using ClipMeta.Core.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class BatchOperationTests
{
    private string _batchDir = string.Empty;
    private List<string> _scratchPaths = new();

    [TestInitialize]
    public void Setup()
    {
        _batchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_batchDir);
        foreach (string p in TestClipsLocator.AllPristine())
        {
            string dest = Path.Combine(_batchDir, Path.GetFileName(p));
            File.Copy(p, dest);
            _scratchPaths.Add(dest);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_batchDir)) Directory.Delete(_batchDir, recursive: true);
    }

    [TestMethod]
    public void BatchSet_AllFilesGainField()
    {
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2";

        BatchOperation.Apply(_batchDir, mutation, NullLogger.Instance);

        foreach (string path in _scratchPaths)
        {
            var root = Mp4Parser.ParseFile(path);
            var gameNode = FindNode(root, n => n.EditableKey == ClipMetaSchema.AtomName(ClipMetaSchema.Game));
            Assert.IsNotNull(gameNode, $"game field missing in {Path.GetFileName(path)}");
        }
    }

    [TestMethod]
    public void BatchSet_ProgressCountReachesTotal()
    {
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";
        int progressCount = 0;
        int total = 0;

        BatchOperation.Apply(_batchDir, mutation, NullLogger.Instance,
            progress: (current, fileTotal) => { progressCount = current; total = fileTotal; });

        Assert.AreEqual(total, progressCount, "Progress count should reach total on completion.");
        Assert.AreEqual(_scratchPaths.Count, total, "Total should equal number of MP4 files.");
    }

    [TestMethod]
    public void FindUntagged_ReturnsFilesWithNoClipMetaFields()
    {
        // Before any tagging, all files are untagged
        var untagged = BatchOperation.FindUntagged(_batchDir).ToList();
        Assert.AreEqual(_scratchPaths.Count, untagged.Count,
            "All files should be untagged before any write.");
    }

    [TestMethod]
    public void FindUntagged_AfterPartialTag_ReturnsRemaining()
    {
        // Tag only first file
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";
        new Mp4Writer().WriteMetadata(_scratchPaths[0], mutation, NullLogger.Instance);

        var untagged = BatchOperation.FindUntagged(_batchDir).ToList();
        Assert.AreEqual(_scratchPaths.Count - 1, untagged.Count,
            "Untagged count should decrease after one file is tagged.");
    }

    private static BoxNode? FindNode(BoxNode node, Func<BoxNode, bool> predicate)
    {
        if (predicate(node)) return node;
        foreach (var child in node.Children)
        {
            var found = FindNode(child, predicate);
            if (found != null) return found;
        }
        return null;
    }
}
```

- [ ] **Step 12.2: Run tests, expect compile failure**

- [ ] **Step 12.3: Implement BatchOperation**

`ClipMeta.Core/Search/BatchOperation.cs`:
```csharp
using ClipMeta.Core.Abstractions;
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;
using ClipMeta.Core.Write;

namespace ClipMeta.Core.Search;

/// <summary>Applies metadata mutations to all MP4 files in a directory.</summary>
public static class BatchOperation
{
    /// <summary>
    /// Applies <paramref name="mutation"/> to every .mp4 file in <paramref name="directory"/>.
    /// </summary>
    /// <param name="progress">Optional callback: (currentFileIndex, totalFiles).</param>
    public static void Apply(
        string directory, MetadataMutation mutation, IClipMetaLogger logger,
        bool recursive = false,
        Action<int, int>? progress = null)
    {
        var files = EnumerateMp4s(directory, recursive).ToList();
        var writer = new Mp4Writer();

        for (int i = 0; i < files.Count; i++)
        {
            try
            {
                writer.WriteMetadata(files[i], mutation, logger);
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR {Path.GetFileName(files[i])} {ex.Message}");
            }
            progress?.Invoke(i + 1, files.Count);
        }
    }

    /// <summary>Returns paths of MP4 files that have no clipmeta fields at all.</summary>
    public static IEnumerable<string> FindUntagged(string directory, bool recursive = false,
        string? specificField = null)
    {
        foreach (string mp4 in EnumerateMp4s(directory, recursive))
        {
            try
            {
                var root = Mp4Parser.ParseFile(mp4);
                bool hasClipMeta = HasClipMetaFields(root, specificField);
                if (!hasClipMeta) yield return mp4;
            }
            catch { /* skip unreadable */ }
        }
    }

    private static bool HasClipMetaFields(BoxNode node, string? specificField)
    {
        string prefix = ClipMetaSchema.Domain + ":";
        if (node.EditableKey?.StartsWith(prefix, StringComparison.Ordinal) == true)
        {
            if (specificField == null) return true;
            string field = node.EditableKey[prefix.Length..];
            if (field == specificField) return true;
        }
        foreach (var child in node.Children)
            if (HasClipMetaFields(child, specificField)) return true;
        return false;
    }

    private static IEnumerable<string> EnumerateMp4s(string directory, bool recursive)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directory, "*.mp4", option);
    }
}
```

- [ ] **Step 12.4: Run tests**

```powershell
dotnet test clipmetascribe.Tests/clipmetascribe.Tests.csproj --filter "BatchOperationTests"
```
Expected: all pass.

- [ ] **Step 12.5: Commit**

```powershell
git add ClipMeta.Core/Search/BatchOperation.cs clipmetascribe.Tests/BatchOperationTests.cs
git commit -m "feat: add BatchOperation for directory-wide set/append and untagged file detection"
```

---

## Task 13: clipmetascribe CLI

**Files:**
- Modify: `clipmetascribe/clipmetascribe.csproj`
- Modify: `clipmetascribe/Program.cs`
- Create: `clipmetascribe/Commands/WriteCommand.cs`
- Create: `clipmetascribe/Commands/ListCommand.cs`
- Create: `clipmetascribe/Commands/StatsCommand.cs`
- Create: `clipmetascribe/Commands/VocabCommand.cs`
- Create: `clipmetascribe/Commands/FindCommand.cs`
- Create: `clipmetascribe/Commands/IndexCommand.cs`
- Create: `clipmetascribe/Commands/ExportCommand.cs`

- [ ] **Step 13.1: Update clipmetascribe.csproj to reference Core**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyVersion>1.0.0</AssemblyVersion>
    <InformationalVersion>1.0.0</InformationalVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\ClipMeta.Core\ClipMeta.Core.csproj" />
  </ItemGroup>
</Project>
```

Create `clipmetascribe/Commands/` directory:
```powershell
New-Item -ItemType Directory -Path "clipmetascribe/Commands"
```

- [ ] **Step 13.2: Implement Program.cs**

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
            Console.WriteLine($"clipmetascribe 1.0.0 (ClipMeta.Core 1.0.0)");
            return 0;
        }

        // Shared flags
        bool verbose = args.Contains("--verbose");
        bool dryRun = args.Contains("--dry-run");
        bool yes = args.Contains("--yes");
        bool recursive = args.Contains("--recursive");
        string? logPath = GetFlag(args, "--log");
        string outputFormat = GetFlag(args, "--output") ?? "text";

        IClipMetaLogger logger = logPath != null
            ? new FileLogger(logPath, verbose ? LogLevel.Verbose : LogLevel.Simple)
            : NullLogger.Instance;

        // Scan for orphaned temp files on startup (single-file operations only)
        string? singleFilePath = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null;
        if (singleFilePath != null)
            WarnOrphanedTempFiles(Path.GetDirectoryName(singleFilePath) ?? ".");

        // Dispatch, top-level catch maps unhandled exceptions to documented exit codes.
        try
        {
            if (ContainsFlag(args, "--dir"))
            {
                string dir = GetFlag(args, "--dir") ?? ".";
                return HandleDirectoryCommand(args, dir, logger, dryRun, verbose, yes, recursive, outputFormat);
            }

            if (singleFilePath != null)
                return HandleSingleFileCommand(args, singleFilePath, logger, dryRun, verbose, yes, outputFormat);

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

    private static int HandleSingleFileCommand(
        string[] args, string filePath, IClipMetaLogger logger,
        bool dryRun, bool verbose, bool yes, string outputFormat)
    {
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"Error: File not found: {filePath}");
            return 1;
        }

        if (ContainsFlag(args, "--list"))
            return ListCommand.Run(filePath, outputFormat);

        if (ContainsFlag(args, "--clear-all"))
            return WriteCommand.RunClearAll(filePath, dryRun, yes, logger);

        var mutation = BuildMutation(args, dryRun);
        if (ContainsFlag(args, "--copy-tags"))
            return WriteCommand.RunCopyTags(filePath, args, logger, dryRun);

        if (mutation.SetFields.Count > 0 || mutation.AppendFields.Count > 0
            || mutation.DeleteFields.Count > 0)
            return WriteCommand.Run(filePath, mutation, logger);

        Console.Error.WriteLine($"Error: No operation specified for {filePath}");
        PrintUsage();
        return 1;
    }

    private static int HandleDirectoryCommand(
        string[] args, string dir, IClipMetaLogger logger,
        bool dryRun, bool verbose, bool yes, bool recursive, string outputFormat)
    {
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"Error: Directory not found: {dir}");
            return 1;
        }

        if (ContainsFlag(args, "--stats"))
            return StatsCommand.Run(dir, recursive, outputFormat);
        if (ContainsFlag(args, "--vocab"))
            return VocabCommand.Run(dir, GetFlag(args, "--vocab") ?? string.Empty, recursive);
        if (ContainsFlag(args, "--find"))
            return FindCommand.Run(dir, args, recursive);
        if (ContainsFlag(args, "--index"))
            return IndexCommand.Run(dir);
        if (ContainsFlag(args, "--export"))
            return ExportCommand.Run(dir, GetFlag(args, "--export") ?? "library.csv", recursive);
        if (ContainsFlag(args, "--untagged"))
            return ListCommand.RunUntagged(dir, GetFlag(args, "--untagged"), recursive);
        if (ContainsFlag(args, "--clear-all"))
            return WriteCommand.RunBatchClearAll(dir, dryRun, yes, recursive, logger);

        var mutation = BuildMutation(args, dryRun);
        if (mutation.SetFields.Count > 0 || mutation.AppendFields.Count > 0)
            return WriteCommand.RunBatch(dir, mutation, recursive, logger);

        Console.Error.WriteLine("Error: No operation specified for --dir");
        return 1;
    }

    private static MetadataMutation BuildMutation(string[] args, bool dryRun)
    {
        var mutation = new MetadataMutation { DryRun = dryRun };
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--set" && i + 2 < args.Length)
            {
                string field = args[i + 1];
                string value = args[i + 2];
                mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
                i += 2;
            }
            else if (args[i] == "--append" && i + 2 < args.Length)
            {
                string field = args[i + 1];
                string value = args[i + 2];
                mutation.AppendFields[ClipMetaSchema.AtomName(field)] = value;
                i += 2;
            }
            else if (args[i] == "--clear" && i + 1 < args.Length)
            {
                mutation.DeleteFields.Add(ClipMetaSchema.AtomName(args[i + 1]));
                i++;
            }
        }
        return mutation;
    }

    private static void WarnOrphanedTempFiles(string dir)
    {
        var orphans = Directory.EnumerateFiles(dir, "*.mp4.tmp").ToList();
        if (orphans.Count == 0) return;
        Console.Error.WriteLine($"Warning: found {orphans.Count} orphaned temp file(s) from a previous interrupted write:");
        foreach (string o in orphans) Console.Error.WriteLine($"  {o}");
        Console.Error.WriteLine("These can be safely deleted.");
    }

    private static bool ContainsFlag(string[] args, string flag)
        => args.Any(a => a == flag);

    private static string? GetFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            clipmetascribe, MP4 metadata tagger for game clips

            Usage:
              clipmetascribe <file.mp4> --list
              clipmetascribe <file.mp4> --set <field> <value>
              clipmetascribe <file.mp4> --append <field> <value>
              clipmetascribe <file.mp4> --clear <field>
              clipmetascribe <file.mp4> --clear-all [--yes]
              clipmetascribe --dir <folder> --set <field> <value>
              clipmetascribe --dir <folder> --stats
              clipmetascribe --dir <folder> --find <field> <value>
              clipmetascribe --dir <folder> --export <output.csv>
              clipmetascribe --dir <folder> --index
              clipmetascribe --version

            Fields: game, players, tags, timecode, rating, notes
            Flags:  --dry-run  --backup  --verbose  --log <path>  --yes  --recursive
            """);
    }
}
```

- [ ] **Step 13.3: Implement WriteCommand.cs**

`clipmetascribe/Commands/WriteCommand.cs`:
```csharp
using ClipMeta.Core.Abstractions;
using ClipMeta.Core.Search;
using ClipMeta.Core.Write;

namespace ClipMetaScribe.Commands;

internal static class WriteCommand
{
    public static int Run(string filePath, MetadataMutation mutation, IClipMetaLogger logger)
    {
        try
        {
            new Mp4Writer().WriteMetadata(filePath, mutation, logger);
            return 0;
        }
        catch (UnsupportedFormatException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Verification failed: {ex.Message}");
            return 3;
        }
    }

    public static int RunClearAll(string filePath, bool dryRun, bool yes, IClipMetaLogger logger)
    {
        if (!yes && !dryRun)
        {
            Console.Write($"Type YES to clear all clipmeta fields from {Path.GetFileName(filePath)}: ");
            if (Console.ReadLine()?.Trim() != "YES") { Console.WriteLine("Aborted."); return 0; }
        }
        var mutation = new MetadataMutation { ClearAll = true, DryRun = dryRun };
        return Run(filePath, mutation, logger);
    }

    public static int RunBatch(string dir, MetadataMutation mutation, bool recursive, IClipMetaLogger logger)
    {
        BatchOperation.Apply(dir, mutation, logger, recursive,
            progress: (i, total) => Console.Write($"\rProgress: {i}/{total}  "));
        Console.WriteLine();
        return 0;
    }

    public static int RunBatchClearAll(string dir, bool dryRun, bool yes, bool recursive, IClipMetaLogger logger)
    {
        if (!yes && !dryRun)
        {
            Console.Write($"Type YES to clear all clipmeta fields from every MP4 in {dir}: ");
            if (Console.ReadLine()?.Trim() != "YES") { Console.WriteLine("Aborted."); return 0; }
        }
        var mutation = new MetadataMutation { ClearAll = true, DryRun = dryRun };
        return RunBatch(dir, mutation, recursive, logger);
    }

    public static int RunCopyTags(string filePath, string[] args, IClipMetaLogger logger, bool dryRun)
    {
        // Usage: clipmetascribe <file.mp4> --copy-tags <dest.mp4>
        // filePath is the source (already resolved by HandleSingleFileCommand).
        // args[idx+1] is the destination.
        int idx = Array.IndexOf(args, "--copy-tags");
        if (idx < 0 || idx + 1 >= args.Length)
        {
            Console.Error.WriteLine("Usage: clipmetascribe <source.mp4> --copy-tags <dest.mp4>");
            return 1;
        }
        string source = filePath;
        string dest = args[idx + 1];

        var root = ClipMeta.Core.Mp4.Mp4Parser.ParseFile(source);
        var mutation = new MetadataMutation { DryRun = dryRun };
        CollectEditableFields(root, mutation);
        return Run(dest, mutation, logger);
    }

    private static void CollectEditableFields(ClipMeta.Core.Mp4.BoxNode node, MetadataMutation mutation)
    {
        if (node.IsEditable && node.EditableKey != null && node.DisplayValue != null)
            mutation.SetFields[node.EditableKey] = node.DisplayValue[1..^1];
        foreach (var child in node.Children) CollectEditableFields(child, mutation);
    }
}
```

- [ ] **Step 13.4: Implement ListCommand.cs**

`clipmetascribe/Commands/ListCommand.cs`:
```csharp
using System.Text.Json;
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;
using ClipMeta.Core.Search;

namespace ClipMetaScribe.Commands;

internal static class ListCommand
{
    public static int Run(string filePath, string format)
    {
        var root = Mp4Parser.ParseFile(filePath);
        var fields = CollectFields(root);

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(fields, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"{Path.GetFileName(filePath)}:");
            if (fields.Count == 0)
                Console.WriteLine("  (no clipmeta fields)");
            foreach (var (field, value) in fields)
                Console.WriteLine($"  {field}: {value}");
        }
        return 0;
    }

    public static int RunUntagged(string dir, string? specificField, bool recursive)
    {
        foreach (string path in BatchOperation.FindUntagged(dir, recursive, specificField))
            Console.WriteLine(path);
        return 0;
    }

    private static Dictionary<string, string> CollectFields(BoxNode node)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectFromNode(node, fields);
        return fields;
    }

    private static void CollectFromNode(BoxNode node, Dictionary<string, string> fields)
    {
        if (node.EditableKey?.StartsWith(ClipMetaSchema.Domain + ":", StringComparison.Ordinal) == true
            && node.DisplayValue != null)
        {
            string field = node.EditableKey[(ClipMetaSchema.Domain.Length + 1)..];
            fields[field] = node.DisplayValue[1..^1];
        }
        foreach (var child in node.Children) CollectFromNode(child, fields);
    }
}
```

- [ ] **Step 13.5: Implement StatsCommand, VocabCommand, FindCommand, IndexCommand, ExportCommand**

`clipmetascribe/Commands/StatsCommand.cs`:
```csharp
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;

namespace ClipMetaScribe.Commands;

internal static class StatsCommand
{
    public static int Run(string dir, bool recursive, string format)
    {
        var allFields = new List<Dictionary<string, string>>();
        int total = 0;
        foreach (string mp4 in EnumerateMp4s(dir, recursive))
        {
            total++;
            try
            {
                var root = Mp4Parser.ParseFile(mp4);
                var fields = Collect(root);
                if (fields.Count > 0) allFields.Add(fields);
            }
            catch { /* skip */ }
        }

        int tagged = allFields.Count;
        Console.WriteLine($"{total:N0} clips total");
        Console.WriteLine($"{tagged:N0} tagged  /  {total - tagged:N0} untagged");

        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var gameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in allFields)
        {
            if (f.TryGetValue(ClipMetaSchema.Tags, out string? tags))
                foreach (string tag in tags.Split('|'))
                    tagCounts[tag.Trim()] = tagCounts.GetValueOrDefault(tag.Trim()) + 1;
            if (f.TryGetValue(ClipMetaSchema.Game, out string? game))
                gameCounts[game] = gameCounts.GetValueOrDefault(game) + 1;
        }

        var topTags = tagCounts.OrderByDescending(x => x.Value).Take(5);
        Console.Write("Top tags:  ");
        Console.WriteLine(string.Join("  ", topTags.Select(t => $"{t.Key} ({t.Value})")));
        Console.Write("Games:     ");
        Console.WriteLine(string.Join("  ", gameCounts.OrderByDescending(x => x.Value).Select(g => $"{g.Key} ({g.Value})")));
        return 0;
    }

    private static Dictionary<string, string> Collect(BoxNode node)
    {
        var d = new Dictionary<string, string>();
        CollectFromNode(node, d);
        return d;
    }

    private static void CollectFromNode(BoxNode node, Dictionary<string, string> d)
    {
        if (node.EditableKey?.StartsWith(ClipMetaSchema.Domain + ":") == true && node.DisplayValue != null)
            d[node.EditableKey[(ClipMetaSchema.Domain.Length + 1)..]] = node.DisplayValue[1..^1];
        foreach (var c in node.Children) CollectFromNode(c, d);
    }

    private static IEnumerable<string> EnumerateMp4s(string dir, bool recursive)
        => Directory.EnumerateFiles(dir, "*.mp4",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
}
```

`clipmetascribe/Commands/VocabCommand.cs`:
```csharp
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;

namespace ClipMetaScribe.Commands;

internal static class VocabCommand
{
    public static int Run(string dir, string field, bool recursive)
    {
        string atomKey = ClipMetaSchema.AtomName(field);
        var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string mp4 in Directory.EnumerateFiles(dir, "*.mp4",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            try
            {
                var root = Mp4Parser.ParseFile(mp4);
                var node = FindNode(root, n => n.EditableKey == atomKey);
                if (node?.DisplayValue is { } v)
                    foreach (string item in v[1..^1].Split('|'))
                        values.Add(item.Trim());
            }
            catch { /* skip */ }
        }
        foreach (string v in values) Console.WriteLine(v);
        return 0;
    }

    private static BoxNode? FindNode(BoxNode node, Func<BoxNode, bool> pred)
    {
        if (pred(node)) return node;
        foreach (var c in node.Children) { var f = FindNode(c, pred); if (f != null) return f; }
        return null;
    }
}
```

`clipmetascribe/Commands/FindCommand.cs`:
```csharp
using ClipMeta.Core.Schema;
using ClipMeta.Core.Search;

namespace ClipMetaScribe.Commands;

internal static class FindCommand
{
    public static int Run(string dir, string[] args, bool recursive)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTime? since = null, before = null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--find" && i + 2 < args.Length)
                { filters[args[i + 1]] = args[i + 2]; i += 2; }
            else if (args[i] == "--since" && DateTime.TryParse(args[i + 1], out var s))
                { since = s; i++; }
            else if (args[i] == "--before" && DateTime.TryParse(args[i + 1], out var b))
                { before = b; i++; }
        }

        foreach (var result in ClipMetaSearch.Find(dir, filters, since, before))
            Console.WriteLine(result);
        return 0;
    }
}
```

`clipmetascribe/Commands/IndexCommand.cs`:
```csharp
using ClipMeta.Core.Search;

namespace ClipMetaScribe.Commands;

internal static class IndexCommand
{
    public static int Run(string dir)
    {
        Console.Write($"Indexing {dir}...");
        ClipMetaIndex.Build(dir);
        Console.WriteLine(" done.");
        return 0;
    }
}
```

`clipmetascribe/Commands/ExportCommand.cs`:
```csharp
using ClipMeta.Core.Mp4;
using ClipMeta.Core.Schema;

namespace ClipMetaScribe.Commands;

internal static class ExportCommand
{
    private static readonly string[] KnownFields =
    {
        ClipMetaSchema.Game, ClipMetaSchema.Players, ClipMetaSchema.Tags,
        ClipMetaSchema.Timecode, ClipMetaSchema.Rating, ClipMetaSchema.Notes,
    };

    public static int Run(string dir, string outputPath, bool recursive)
    {
        var rows = new List<Dictionary<string, string>>();
        var allFields = new HashSet<string>(KnownFields, StringComparer.Ordinal);

        foreach (string mp4 in Directory.EnumerateFiles(dir, "*.mp4",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            var row = new Dictionary<string, string> { ["file"] = mp4 };
            try
            {
                var root = Mp4Parser.ParseFile(mp4);
                CollectFromNode(root, row, allFields);
            }
            catch { /* skip unreadable */ }
            rows.Add(row);
        }

        var columns = new[] { "file" }.Concat(allFields.Order()).ToList();
        using var sw = new StreamWriter(outputPath);
        sw.WriteLine(string.Join(",", columns.Select(CsvEscape)));
        foreach (var row in rows)
        {
            sw.WriteLine(string.Join(",",
                columns.Select(col => CsvEscape(row.TryGetValue(col, out var v) ? v : string.Empty))));
        }

        Console.WriteLine($"Exported {rows.Count} rows to {outputPath}");
        return 0;
    }

    private static void CollectFromNode(BoxNode node, Dictionary<string, string> row, HashSet<string> allFields)
    {
        if (node.EditableKey?.StartsWith(ClipMetaSchema.Domain + ":") == true && node.DisplayValue != null)
        {
            string field = node.EditableKey[(ClipMetaSchema.Domain.Length + 1)..];
            row[field] = node.DisplayValue[1..^1];
            allFields.Add(field);
        }
        foreach (var c in node.Children) CollectFromNode(c, row, allFields);
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
```

- [ ] **Step 13.6: Build clipmetascribe and verify it compiles**

```powershell
dotnet build clipmetascribe/clipmetascribe.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 13.7: Smoke test, run against a real clip**

```powershell
$clip = Get-ChildItem "testclips/pristine" -Filter "*.mp4" | Select-Object -First 1 -ExpandProperty FullName
dotnet run --project clipmetascribe -- $clip --list
```
Expected: prints `(no clipmeta fields)` or existing metadata.

```powershell
dotnet run --project clipmetascribe -- $clip --set game "Team Fortress 2"
dotnet run --project clipmetascribe -- $clip --list
```
Expected: second run shows `game: Team Fortress 2`.

**Important:** These commands modify testclips/pristine, run only against a scratch copy. Update the smoke test to use scratch:
```powershell
$pristine = Get-ChildItem "testclips/pristine" -Filter "*.mp4" | Select-Object -First 1 -ExpandProperty FullName
$scratch = "testclips/scratch/smoke_test.mp4"
Copy-Item $pristine $scratch
dotnet run --project clipmetascribe -- $scratch --set game "Team Fortress 2"
dotnet run --project clipmetascribe -- $scratch --list
Remove-Item $scratch
```

- [ ] **Step 13.8: Test --version, no-args, and help output**

```powershell
dotnet run --project clipmetascribe -- --version
```
Expected: `clipmetascribe 1.0.0 (ClipMeta.Core 1.0.0)`

```powershell
dotnet run --project clipmetascribe
```
Expected: usage text printed, exit code 1.

- [ ] **Step 13.9: Commit**

```powershell
git add clipmetascribe/ 
git commit -m "feat: implement clipmetascribe CLI with write/list/stats/vocab/find/index/export commands"
```

---

## Task 14: Final Verification (Definition of Done)

- [ ] **Step 14.1: Full build, zero errors, zero warnings**

```powershell
dotnet build
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` across all projects.

- [ ] **Step 14.2: Full test run, all tests pass**

```powershell
dotnet test --logger "console;verbosity=normal"
```
Expected: all tests pass. Record the count. It should be 80+ from clipmetaview.Tests and 40+ from clipmetascribe.Tests.

- [ ] **Step 14.3: Verify clipmetaview still works after Core migration**

```powershell
$clip = Get-ChildItem "testclips/pristine" -Filter "*.mp4" | Select-Object -First 1 -ExpandProperty FullName
dotnet run --project clipmetaview -- $clip
```
Expected: full tree output with box hierarchy, no errors.

- [ ] **Step 14.4: Round-trip verification (write → view)**

```powershell
$pristine = Get-ChildItem "testclips/pristine" -Filter "*.mp4" | Select-Object -First 1 -ExpandProperty FullName
$scratch = "testclips/scratch/roundtrip_test.mp4"
Copy-Item $pristine $scratch

dotnet run --project clipmetascribe -- $scratch --set game "Team Fortress 2" --set tags "market garden|funny moment" --set rating "4"
dotnet run --project clipmetaview -- $scratch
```
Expected: tree output shows `game`, `tags`, `rating` nodes marked `[EDITABLE]` with correct values.

- [ ] **Step 14.5: Verify fragmented MP4 refusal**

If you have a fragmented MP4 (Xbox Game Bar capture) in testclips/pristine, test:
```powershell
dotnet run --project clipmetascribe -- "testclips/pristine/fragmented.mp4" --set game "TF2"
```
Expected: error message mentioning "fragmented MP4" and "Xbox Game Bar", exit code 1.

If no fragmented clip is available, the unit test in Task 10 covers this.

- [ ] **Step 14.6: Verify exit codes**

```powershell
dotnet run --project clipmetascribe -- "nonexistent.mp4" --set game "TF2"
echo "Exit: $LASTEXITCODE"
```
Expected: exit code 1.

```powershell
dotnet run --project clipmetascribe
echo "Exit: $LASTEXITCODE"
```
Expected: exit code 1.

- [ ] **Step 14.7: Check for NuGet package references beyond MSTest**

```powershell
Get-ChildItem -Recurse -Filter "*.csproj" | Select-String -Pattern "PackageReference" | Where-Object { $_ -notmatch "MSTest" }
```
Expected: no results (zero non-MSTest NuGet packages in any project).

Note: `Select-String -Path "*/*.csproj"` only searches one level deep and misses nested project folders. Use `Get-ChildItem -Recurse` instead.

- [ ] **Step 14.8: Final commit**

```powershell
git add -A
git commit -m "feat: Round 2 complete, ClipMeta.Core, Mp4Writer, clipmetascribe CLI all shipping"
```

---

## Self-Review Against Spec

Sections checked against the spec (`2026-05-21-clipmeta-core-write-engine-design.md`):

| Spec section | Covered by |
|---|---|
| 1. Solution structure | Tasks 1, 3, 4, 5 |
| 2. IMediaParser, IMediaWriter, IClipMetaLogger, MediaHandlerRegistry | Task 2 |
| 3. Tag schema, domain, fields, pipe delimiter, normalization | Tasks 2, 9 |
| 4. Write pipeline (temp file → verify → File.Replace) | Task 10 |
| 4. Three write scenarios (update/append/create) | Task 10 |
| 4. free box padding | Not yet a task, **see below** |
| 4. stco/co64 ALL tracks adjusted | Task 10 (Mp4Writer walks all trak) |
| 4. Fragmented MP4 detection | Task 10 |
| 4. File lock detection | Task 10 |
| 4. Orphaned temp file cleanup | Task 13 (Program.cs) |
| 4. --set "" = delete | Task 9 (Normalizer.ApplyToMutation) |
| 4. Foreign atom preservation | Task 10 (WriteIlst copies non-matching atoms) |
| 4. hdlr required when creating meta | Task 10 (WriteNewUdtaChain) |
| 4. mean/name FullBox prefix | Task 7 (FreeformAtomWriter) |
| 5. MetadataMutation | Task 2 |
| 6. Logging (FileLogger, NullLogger, rotation) | Task 8 |
| 7. CLI commands | Task 13 |
| 8. Search index | Task 11 |
| 9. Two-directory test clip strategy | Task 4, 5 |
| 9. BigEndianWriterTests | Task 6 |
| 9. FreeformAtomWriterTests | Task 7 |
| 9. Mp4WriterTests unit | Task 10 |
| 9. Mp4WriterIntegrationTests | Task 10 |
| 9. NormalizationTests | Task 9 |
| 9. BatchOperationTests | Task 12 |
| 9. SearchIndexTests | Task 11 |
| 10. Critical implementation notes (8 risks) | Tasks 7, 10, 9 |

**Gap identified:** `free` box padding (512 bytes after ilst on first write), spec section 4, paragraph "free Box Padding". This was omitted from the plan tasks. Adding it here:

### Supplemental: free Box Padding

After writing ilst content on the first clipmeta write, append a 512-byte `free` box. Future writes that fit within this padding can update in-place without adjusting stco.

**Two code paths require this change:**

**Path 1, Append/Update scenario (WriteIlst):** After writing all ilst atoms, check if this is a first write:
```csharp
// Append free box padding on first clipmeta write to avoid future stco adjustments
bool firstWrite = !existingIlstChildren.Any(c =>
    c.EditableKey?.StartsWith(ClipMetaSchema.Domain + ":", StringComparison.Ordinal) == true);
if (firstWrite)
{
    const int freePaddingPayload = FreePaddingSize - 8; // 8 = box header
    BigEndianWriter.WriteBoxHeader(ilstWriter, FreePaddingSize, "free");
    ilstWriter.Write(new byte[freePaddingPayload]);
}
```

**Path 2, Create scenario (WriteNewUdtaChain):** Always a first write; add free box after ilst and account for it in `ilstSize`:
```csharp
byte[] ilstBytes = ilstBuf.ToArray();
uint ilstSize = (uint)(8 + ilstBytes.Length + FreePaddingSize); // include free box in ilst size

// ... (build hdlr, write meta header as before) ...

BigEndianWriter.WriteBoxHeader(metaWriter, ilstSize, "ilst");
metaWriter.Write(ilstBytes);
// Append 512-byte free box inside ilst
BigEndianWriter.WriteBoxHeader(metaWriter, FreePaddingSize, "free");
metaWriter.Write(new byte[FreePaddingSize - 8]);
```

Also update `CalculateNewMoovSize`, the Create scenario constant already adds 52 bytes for the udta+meta+hdlr chain; add `FreePaddingSize` to it as well:
```csharp
if (scenario == WriteScenario.Create && FindIlst(root) == null)
{
    delta += 52 + FreePaddingSize; // chain overhead + free padding
}
```

Add a test in `Mp4WriterTests.cs`:
```csharp
[TestMethod]
public void Write_FirstWrite_FreePaddingAddedAfterIlst()
{
    byte[] moov = MinimalMp4Builder.MoovBox(null); // no udta/ilst
    byte[] mdat = MinimalMp4Builder.MdatBox();
    _tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".mp4");
    File.WriteAllBytes(_tempFile, moov.Concat(mdat).ToArray());

    var mutation = new MetadataMutation();
    mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";
    new Mp4Writer().WriteMetadata(_tempFile, mutation, NullLogger.Instance);

    var root = Mp4Parser.ParseFile(_tempFile);
    var ilst = FindNode(root, n => n.Type == "ilst");
    Assert.IsNotNull(ilst);
    var freeNode = ilst!.Children.LastOrDefault(c => c.Type == "free");
    Assert.IsNotNull(freeNode, "free padding box should be present after first write");
    Assert.AreEqual(512UL, freeNode!.Size, "free padding box should be exactly 512 bytes");
}
```

Note: `free` boxes inside ilst are parsed by `Mp4Parser` with `IsEditable = true` and `EditableKey = "free"`. The `WriteIlst` method already skips `free` children when re-copying (added in Step 10.3), so old padding is discarded and fresh padding is written on each pass.

Implement both code paths, run the test, commit with message: `feat: add 512-byte free box padding on first clipmeta write`.

---

## Critical Implementation Notes Reference

These correspond directly to the 8 risks in spec section 10:

| Risk | Where addressed | Test |
|---|---|---|
| Fragmented MP4 (moof boxes) | Mp4Writer.DetectFragmented | Mp4WriterIntegrationTests.Write_FragmentedMp4_Throws |
| Only one stco/co64 adjusted | Mp4Writer walks ALL trak → mdia → minf → stbl | Mp4WriterIntegrationTests multi-track round-trip |
| mean/name FullBox prefix omitted | FreeformAtomWriter.Write | FreeformAtomWriterTests.Write_MeanBox_HasFullBoxPrefix |
| hdlr missing when creating meta | Mp4Writer.WriteNewUdtaChain | Mp4WriterTests.Write_CreateFromScratch_IlstAndHdlrCreated |
| Foreign ilst atoms corrupted | Mp4Writer.WriteIlst (copies non-clipmeta atoms verbatim) | Mp4WriterIntegrationTests.Write_ForeignAtoms_Preserved |
| stco adjusted when mdat precedes moov | Mp4Writer.MdatFollowsMoov check | Mp4WriterTests (moov-after-mdat file) |
| co64 values exceed 32-bit boundary | Mp4Writer.WriteAdjustedStco overflow check | Manual/edge case |
| Temp file not deleted on exception | Mp4Writer catch block | Mp4WriterTests.Write_TempFileCleanedUp |
