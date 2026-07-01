# Stats Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `--stats` flag to clipmetascribe that prints the file size and a summary of which clipmeta fields are set/unset for a single MP4 file.

**Architecture:** `StatsCommand` is an internal static class in `clipmetascribe/Commands/` following the exact same pattern as `ListCommand`, it calls `Mp4Parser.ParseFile`, then `ClipMetaReader.GetFields`, formats output to a `TextWriter`, and returns 0. Program.cs wires it in one line after the `--list` check.

**Tech Stack:** C# / .NET 10, MSTest 4, no new NuGet packages

---

## File Structure

| Action | Path | Purpose |
|--------|------|---------|
| Create | `clipmetascribe/Commands/StatsCommand.cs` | Stats formatting logic |
| Modify | `clipmetascribe/Program.cs` | Wire `--stats` flag, update error msg + usage |
| Create | `clipmetascribe.Tests/StatsCommandTests.cs` | Unit tests for StatsCommand output |

---

### Task 1: StatsCommand + Program.cs wiring + tests

**Files:**
- Create: `clipmetascribe/Commands/StatsCommand.cs`
- Modify: `clipmetascribe/Program.cs`
- Create: `clipmetascribe.Tests/StatsCommandTests.cs`

#### Context you need

**ClipMetaSchema known user fields (game, players, tags, timecode, rating, notes):**
The schema also has an internal `ClipMetaSchema.Schema` field (value `"schema"`) that clipmetascribe writes as a version marker on every write. Stats must exclude it, users never set it directly. The 6 user-facing fields are the public constants: `Game`, `Players`, `Tags`, `Timecode`, `Rating`, `Notes`.

**ClipMetaReader.GetFields**: Returns `IReadOnlyList<(string Field, string Value)>` where Field is the bare name (e.g. `"game"`) and Value is already unquoted. Located at `clipmeta.core/Read/ClipMetaReader.cs`.

**ListCommand pattern** (follow this exactly):
```csharp
internal static int Run(string filePath, TextWriter? output = null)
{
    output ??= Console.Out;
    var root   = Mp4Parser.ParseFile(filePath);
    var fields = ClipMetaReader.GetFields(root);
    // ... format to output ...
    return 0;
}
```

**Existing Program.cs `--list` check** (add `--stats` immediately after this block):
```csharp
if (ContainsFlag(args, "--list"))
{
    return ListCommand.Run(filePath);
}
```

**Existing error message** (update to include `--stats`):
```csharp
Console.Error.WriteLine("Error: No write operation specified. Use --set, --append, --clear, --clear-all, or --list.");
```

**Existing InternalsVisibleTo**: `clipmetascribe/AssemblyInfo.cs` already has `[assembly: InternalsVisibleTo("clipmetascribe.Tests")]`. `clipmetascribe.Tests.csproj` already references `clipmetascribe.csproj`. No changes needed there.

**Test helpers you can use**:
- `TestClipsLocator.AllPristine()`, returns paths to pristine clips with no clipmeta metadata
- `ScratchClips.Prepare(pristinePath)`, copies a pristine clip to `testclips/scratch/` with a unique name, returns the scratch path
- `Mp4Writer().WriteMetadata(scratchPath, mutation, NullLogger.Instance)`, writes metadata to the scratch clip
- `ClipMetaSchema.AtomName("game")` returns `"com.peckworkslab.clipmeta:game"`
- ConcurrentBag + ClassCleanup pattern for scratch file cleanup (follow ListCommandTests.cs pattern exactly)

**Namespaces used in test file**: `ClipMetaCore.Mp4`, `ClipMetaCore.Read`, `ClipMetaCore.Schema`, `ClipMetaCore.Write`, `ClipMetaCore.Logging`, `ClipMetaScribe.Commands`, `ClipMetaScribe.Tests.Helpers`

#### Output format

**Clip with no metadata:**
```
clip.mp4  (14.2 MB)
  (no clipmeta metadata)
```

**Clip with some fields set (game + tags, timecode + players + rating + notes are unset):**
```
clip.mp4  (14.2 MB)
  Fields set:    game, tags
  Fields unset:  players, timecode, rating, notes
```

**Clip with all 6 known fields set:**
```
clip.mp4  (14.2 MB)
  Fields set:    game, players, tags, timecode, rating, notes
```
(No "Fields unset:" line when all known fields are set.)

**Clip with a custom (non-schema) field in addition to known fields:**
```
clip.mp4  (14.2 MB)
  Fields set:    game
  Fields unset:  players, tags, timecode, rating, notes
  Custom fields: event
```

**Note:** The internal `schema` field (written automatically by the write engine) must be excluded from all of the above. Filter it out with `!f.Field.Equals(ClipMetaSchema.Schema, StringComparison.Ordinal)`.

**Size formatting:**
```csharp
private static string FormatBytes(long bytes)
{
    if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
    if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
    if (bytes >= 1_024)         return $"{bytes / 1_024.0:F1} KB";
    return $"{bytes} B";
}
```

#### Step-by-step

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/StatsCommandTests.cs`:

```csharp
using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class StatsCommandTests
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<string> _scratchFiles = new();

    [ClassCleanup]
    public static void Cleanup()
    {
        foreach (string path in _scratchFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string PrepareScratch(string pristine)
    {
        string scratch = ScratchClips.Prepare(pristine);
        _scratchFiles.Add(scratch);
        return scratch;
    }

    private static string WriteFields(string pristine, Dictionary<string, string> fields)
    {
        string scratch = PrepareScratch(pristine);
        var mutation = new MetadataMutation();
        foreach (var (atomName, value) in fields)
            mutation.SetFields[atomName] = value;
        new Mp4Writer().WriteMetadata(scratch, mutation, NullLogger.Instance);
        return scratch;
    }

    // ── Pristine clip (no metadata) ──────────────────────────────────────────

    [TestMethod]
    public void Run_PristineClip_FirstLineContainsFilename()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        using var writer = new StringWriter();

        StatsCommand.Run(pristine, writer);

        string firstLine = writer.ToString().Split(Environment.NewLine)[0];
        StringAssert.Contains(firstLine, Path.GetFileName(pristine));
    }

    [TestMethod]
    public void Run_PristineClip_FirstLineContainsFileSize()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        using var writer = new StringWriter();

        StatsCommand.Run(pristine, writer);

        string firstLine = writer.ToString().Split(Environment.NewLine)[0];
        // Size label appears in parentheses, e.g. "(14.2 MB)" or "(512 KB)"
        StringAssert.Contains(firstLine, "(");
        StringAssert.Contains(firstLine, ")");
    }

    [TestMethod]
    public void Run_PristineClip_PrintsNoMetadataMessage()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        using var writer = new StringWriter();

        StatsCommand.Run(pristine, writer);

        StringAssert.Contains(writer.ToString(), "(no clipmeta metadata)");
    }

    [TestMethod]
    public void Run_AlwaysReturnsZero()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        using var writer = new StringWriter();

        int exitCode = StatsCommand.Run(pristine, writer);

        Assert.AreEqual(0, exitCode);
    }

    // ── Clip with metadata ───────────────────────────────────────────────────

    [TestMethod]
    public void Run_AllKnownFieldsSet_ShowsAllFieldNames()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        string scratch = WriteFields(pristine, new()
        {
            [ClipMetaSchema.AtomName(ClipMetaSchema.Game)]     = "Team Fortress 2",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Players)]  = "Ben|Scott",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Tags)]     = "headshot",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Timecode)] = "00:01:23",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Rating)]   = "4",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Notes)]    = "great clip",
        });
        using var writer = new StringWriter();

        StatsCommand.Run(scratch, writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "game");
        StringAssert.Contains(output, "players");
        StringAssert.Contains(output, "tags");
        StringAssert.Contains(output, "timecode");
        StringAssert.Contains(output, "rating");
        StringAssert.Contains(output, "notes");
    }

    [TestMethod]
    public void Run_AllKnownFieldsSet_NoUnsetLine()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        string scratch = WriteFields(pristine, new()
        {
            [ClipMetaSchema.AtomName(ClipMetaSchema.Game)]     = "TF2",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Players)]  = "Ben",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Tags)]     = "headshot",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Timecode)] = "00:01:23",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Rating)]   = "4",
            [ClipMetaSchema.AtomName(ClipMetaSchema.Notes)]    = "notes",
        });
        using var writer = new StringWriter();

        StatsCommand.Run(scratch, writer);

        Assert.IsFalse(writer.ToString().Contains("Fields unset"),
            "Should not print 'Fields unset' when all known fields are set");
    }

    [TestMethod]
    public void Run_PartialFieldsSet_ShowsUnsetKnownFields()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        // Write only game, the other 5 should appear as unset
        string scratch = WriteFields(pristine, new()
        {
            [ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2",
        });
        using var writer = new StringWriter();

        StatsCommand.Run(scratch, writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "Fields unset");
        StringAssert.Contains(output, "players");
        StringAssert.Contains(output, "timecode");
    }

    [TestMethod]
    public void Run_CustomFieldSet_ShowsCustomFieldsLine()
    {
        string pristine = TestClipsLocator.AllPristine().First();
        string scratch = WriteFields(pristine, new()
        {
            [ClipMetaSchema.AtomName("event")] = "LAN party",
        });
        using var writer = new StringWriter();

        StatsCommand.Run(scratch, writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "Custom fields");
        StringAssert.Contains(output, "event");
    }

    [TestMethod]
    public void Run_SchemaFieldExcluded_NotListedAsUserField()
    {
        // The write engine always writes a "schema" version field.
        // It should not appear in "Fields set:" or "Custom fields:".
        string pristine = TestClipsLocator.AllPristine().First();
        string scratch = WriteFields(pristine, new()
        {
            [ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2",
        });
        using var writer = new StringWriter();

        StatsCommand.Run(scratch, writer);

        string output = writer.ToString();
        // "schema" should not appear on any output line
        // (it's an internal version marker, not a user-facing field)
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.IsFalse(
            lines.Any(l => l.Contains("schema")),
            $"Internal 'schema' field leaked into stats output:\n{output}");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (StatsCommand doesn't exist yet)**

```
cd C:\path\to\peckworks-clipmeta
dotnet test clipmetascribe.Tests --filter "StatsCommandTests" --no-build 2>&1 | head -20
```

Expected: compile error, `StatsCommand` does not exist.

- [ ] **Step 3: Implement StatsCommand**

Create `clipmetascribe/Commands/StatsCommand.cs`:

```csharp
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;

namespace ClipMetaScribe.Commands;

/// <summary>Displays file size and a summary of which clipmeta fields are set/unset.</summary>
internal static class StatsCommand
{
    private static readonly string[] KnownFields =
    [
        ClipMetaSchema.Game, ClipMetaSchema.Players, ClipMetaSchema.Tags,
        ClipMetaSchema.Timecode, ClipMetaSchema.Rating, ClipMetaSchema.Notes,
    ];

    /// <summary>
    /// Parses <paramref name="filePath"/>, computes field stats, and writes
    /// formatted output to <paramref name="output"/> (defaults to <see cref="Console.Out"/>).
    /// </summary>
    /// <returns>Exit code 0 on success.</returns>
    internal static int Run(string filePath, TextWriter? output = null)
    {
        output ??= Console.Out;

        var root   = Mp4Parser.ParseFile(filePath);
        var fields = ClipMetaReader.GetFields(root);

        long bytes = new FileInfo(filePath).Length;
        output.WriteLine($"{Path.GetFileName(filePath)}  ({FormatBytes(bytes)})");

        // Exclude the internal schema version field from user-visible stats
        var userFields    = fields.Where(f => !f.Field.Equals(ClipMetaSchema.Schema, StringComparison.Ordinal)).ToList();

        if (userFields.Count == 0)
        {
            output.WriteLine("  (no clipmeta metadata)");
            return 0;
        }

        var setFieldNames = userFields.Select(f => f.Field).ToList();
        var knownUnset    = KnownFields.Where(k => !setFieldNames.Contains(k, StringComparer.Ordinal)).ToList();
        var customFields  = setFieldNames.Where(n => !KnownFields.Contains(n, StringComparer.Ordinal)).ToList();

        output.WriteLine($"  Fields set:    {string.Join(", ", setFieldNames)}");
        if (knownUnset.Count > 0)
            output.WriteLine($"  Fields unset:  {string.Join(", ", knownUnset)}");
        if (customFields.Count > 0)
            output.WriteLine($"  Custom fields: {string.Join(", ", customFields)}");

        return 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }
}
```

- [ ] **Step 4: Wire --stats into Program.cs**

In `clipmetascribe/Program.cs`, add the `--stats` check immediately after the `--list` check (inside the `try` block):

**Find this block:**
```csharp
            if (ContainsFlag(args, "--list"))
            {
                return ListCommand.Run(filePath);
            }
```

**Add immediately after it:**
```csharp
            if (ContainsFlag(args, "--stats"))
            {
                return StatsCommand.Run(filePath);
            }
```

**Update the error message** (find the existing line):
```csharp
Console.Error.WriteLine("Error: No write operation specified. Use --set, --append, --clear, --clear-all, or --list.");
```
Change to:
```csharp
Console.Error.WriteLine("Error: No write operation specified. Use --set, --append, --clear, --clear-all, --list, or --stats.");
```

**Update PrintUsage**, in the Usage section, add `clipmetascribe "clip.mp4" --stats` after the `--list` line. In the Examples section, add `clipmetascribe "clip.mp4" --stats` after the list example.

The updated usage string should have these lines (find and update the raw string literal):
```
  clipmetascribe "clip.mp4" --list
  clipmetascribe "clip.mp4" --stats
```
And in examples:
```
  clipmetascribe "clip.mp4" --list
  clipmetascribe "clip.mp4" --stats
```

- [ ] **Step 5: Build**

```
dotnet build clipmetascribe
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Run all tests**

```
dotnet test clipmetascribe.Tests
```

Expected: all tests pass (was 183 before this task; new total should be 183 + 8 = 191).

- [ ] **Step 7: Self-review**

Read `StatsCommand.cs` and `StatsCommandTests.cs`. Check:
- Schema field is excluded from output (not listed in Fields set, Fields unset, or Custom fields)
- `KnownFields` array has exactly 6 entries: game, players, tags, timecode, rating, notes
- `FormatBytes` covers all four size ranges with correct thresholds
- All tests have a comment explaining what they verify
- No test leaks scratch files (all scratch files registered in `_scratchFiles`)

- [ ] **Step 8: Commit**

```
git add clipmetascribe/Commands/StatsCommand.cs clipmetascribe/Program.cs clipmetascribe.Tests/StatsCommandTests.cs
git commit -m "feat: add --stats command showing file size and field set/unset summary"
```
