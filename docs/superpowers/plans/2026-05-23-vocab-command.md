# vocab Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `--vocab <field>` to clipmetascribe so a user can enumerate all distinct values for a given field across every MP4 in a directory, with per-value clip counts.

**Architecture:** `ClipMetaVocab.Enumerate` (in `clipmeta.core/Read/`) scans the directory, reads fields via `ClipMetaReader.GetFields`, and returns a `VocabResult` record containing a value→count dictionary and a total clip count. Pipe-separated fields (tags, players, timecode, defined in `ClipMetaSchema.PipeFields`) are split on `|` so each pipe-item is counted individually. `VocabCommand` in `clipmetascribe/Commands/` formats and prints the result. Program.cs wires `--vocab` before the `File.Exists` check (same pattern as `--find`), using the existing `GetFlag` helper to read the single field argument.

**Tech Stack:** C# / .NET, MSTest 4, no external packages. Solution root: `C:\path\to\peckworks-clipmeta`.

---

## File Structure

| File | Action | Responsibility |
|------|--------|---------------|
| `clipmeta.core/Read/ClipMetaVocab.cs` | Create | Core enumeration: scans directory, returns `VocabResult` |
| `clipmetascribe/Commands/VocabCommand.cs` | Create | Formats and prints vocab results |
| `clipmetascribe/Program.cs` | Modify | Wire `--vocab` flag + update usage/error text |
| `clipmetascribe.Tests/ClipMetaVocabTests.cs` | Create | Unit tests for `ClipMetaVocab.Enumerate` |
| `clipmetascribe.Tests/VocabCommandTests.cs` | Create | Unit tests for `VocabCommand.Run` |

---

## Reference: Key Types You'll Use

**`ClipMetaReader.GetFields(BoxNode root)`**, returns `IReadOnlyList<(string Field, string Value)>` where Field is the bare field name (e.g. `"game"`) and Value is the unquoted string (e.g. `"Team Fortress 2"`). Defined in `clipmeta.core/Read/ClipMetaReader.cs`.

**`ClipMetaSchema.PipeFields`**, `IReadOnlySet<string>` containing `"players"`, `"tags"`, `"timecode"`. Fields in this set store pipe-separated lists. Defined in `clipmeta.core/Schema/ClipMetaSchema.cs`.

**`ClipMetaSchema.AtomName(field)`**, returns `"com.peckworkslab.clipmeta:field"`. Used in tests when writing metadata via `Mp4Writer`.

**`Mp4Parser.ParseFile(path)`**, parses an MP4, returns `BoxNode`. Can throw `IOException`, `UnauthorizedAccessException`, `InvalidDataException`.

**`TestClipsLocator.AllPristine()`**, returns pristine `.mp4` paths from `testclips/pristine/`. Defined in `clipmetascribe.Tests/Helpers/TestClipsLocator.cs`.

**`Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance)`**, writes metadata into an MP4 copy. `MetadataMutation.SetFields[atomName] = value`.

**`GetFlag(args, "--vocab")`**, already in `Program.cs`. Returns `args[idx+1]` if `--vocab` is present and has a following argument, else null.

---

## Task 1: ClipMetaVocab Core + Tests

**Files:**
- Create: `clipmeta.core/Read/ClipMetaVocab.cs`
- Create: `clipmetascribe.Tests/ClipMetaVocabTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/ClipMetaVocabTests.cs`:

```csharp
using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaVocabTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void PrepareClip(string fileName, string field, string value, string? subDir = null)
    {
        string dir = subDir != null ? Path.Combine(_tempDir, subDir) : _tempDir;
        Directory.CreateDirectory(dir);
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(dir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
    }

    [TestMethod]
    public void Enumerate_SingleValue_ReturnsCountAndClipsWithField()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game");

        Assert.AreEqual(1, result.Counts.Count);
        Assert.AreEqual(1, result.Counts["Team Fortress 2"]);
        Assert.AreEqual(1, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_MultipleClipsSameValue_AccumulatesCount()
    {
        PrepareClip("clip1.mp4", "game", "Team Fortress 2");
        PrepareClip("clip2.mp4", "game", "Team Fortress 2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game");

        Assert.AreEqual(1, result.Counts.Count);
        Assert.AreEqual(2, result.Counts["Team Fortress 2"]);
        Assert.AreEqual(2, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_MultipleDistinctValues_ReturnsAll()
    {
        PrepareClip("clip1.mp4", "game", "Team Fortress 2");
        PrepareClip("clip2.mp4", "game", "Counter-Strike 2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game");

        Assert.AreEqual(2, result.Counts.Count);
        Assert.IsTrue(result.Counts.ContainsKey("Team Fortress 2"));
        Assert.IsTrue(result.Counts.ContainsKey("Counter-Strike 2"));
        Assert.AreEqual(2, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_PipeField_SplitsItems()
    {
        PrepareClip("clip.mp4", "tags", "headshot|rocket jump");

        var result = ClipMetaVocab.Enumerate(_tempDir, "tags");

        Assert.AreEqual(2, result.Counts.Count);
        Assert.AreEqual(1, result.Counts["headshot"]);
        Assert.AreEqual(1, result.Counts["rocket jump"]);
        Assert.AreEqual(1, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_PipeField_CombinesCountsAcrossClips()
    {
        PrepareClip("clip1.mp4", "tags", "headshot|rocket jump");
        PrepareClip("clip2.mp4", "tags", "headshot");

        var result = ClipMetaVocab.Enumerate(_tempDir, "tags");

        Assert.AreEqual(2, result.Counts.Count);
        Assert.AreEqual(2, result.Counts["headshot"]);
        Assert.AreEqual(1, result.Counts["rocket jump"]);
        Assert.AreEqual(2, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_FieldNotPresent_ReturnsEmpty()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "notes");

        Assert.AreEqual(0, result.Counts.Count);
        Assert.AreEqual(0, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_CaseInsensitiveKey_DeduplicatesValues()
    {
        PrepareClip("clip1.mp4", "game", "TF2");
        PrepareClip("clip2.mp4", "game", "tf2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game");

        Assert.AreEqual(1, result.Counts.Count);
        Assert.AreEqual(2, result.Counts.Values.First());
    }

    [TestMethod]
    public void Enumerate_CaseInsensitiveFieldName_Matches()
    {
        PrepareClip("clip.mp4", "game", "TF2");

        var result = ClipMetaVocab.Enumerate(_tempDir, "GAME");

        Assert.AreEqual(1, result.Counts.Count);
        Assert.AreEqual(1, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_EmptyDirectory_ReturnsEmpty()
    {
        var result = ClipMetaVocab.Enumerate(_tempDir, "game");

        Assert.AreEqual(0, result.Counts.Count);
        Assert.AreEqual(0, result.ClipsWithField);
    }

    [TestMethod]
    public void Enumerate_Recursive_FindsClipsInSubdirectory()
    {
        PrepareClip("clip.mp4", "game", "TF2", subDir: "sub");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game", recursive: true);

        Assert.AreEqual(1, result.Counts.Count);
    }

    [TestMethod]
    public void Enumerate_NonRecursive_IgnoresSubdirectory()
    {
        PrepareClip("clip.mp4", "game", "TF2", subDir: "sub");

        var result = ClipMetaVocab.Enumerate(_tempDir, "game", recursive: false);

        Assert.AreEqual(0, result.Counts.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
cd C:\path\to\peckworks-clipmeta
dotnet test clipmetascribe.Tests --filter "ClipMetaVocabTests" --verbosity minimal
```

Expected: build error, `ClipMetaVocab` does not exist.

- [ ] **Step 3: Implement ClipMetaVocab**

Create `clipmeta.core/Read/ClipMetaVocab.cs`:

```csharp
using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Read;

/// <summary>Result of a vocabulary enumeration over a directory of MP4 files.</summary>
public record VocabResult(
    /// <summary>Distinct values mapped to the number of clips that contain each value.</summary>
    IReadOnlyDictionary<string, int> Counts,
    /// <summary>Number of clips that had at least one value for the queried field.</summary>
    int ClipsWithField);

/// <summary>Enumerates distinct values for a metadata field across a directory of MP4 files.</summary>
public static class ClipMetaVocab
{
    /// <summary>
    /// Scans .mp4 files in <paramref name="directory"/> and returns distinct values for
    /// <paramref name="field"/> with per-value clip counts.
    /// Pipe-separated fields (tags, players, timecode) are split on '|'; each item is counted
    /// individually. Malformed or unreadable files are silently skipped.
    /// </summary>
    /// <param name="directory">Directory to search.</param>
    /// <param name="field">Bare field name (e.g. "tags"), matched case-insensitively.</param>
    /// <param name="recursive">When true, searches subdirectories. Default: true.</param>
    /// <returns>A <see cref="VocabResult"/> with value counts and total clips with the field.</returns>
    public static VocabResult Enumerate(string directory, string field, bool recursive = true)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int clipsWithField = 0;
        bool isPipeField = ClipMetaSchema.PipeFields.Any(f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (string path in Directory.EnumerateFiles(directory, "*.mp4", option))
        {
            BoxNode root;
            try { root = Mp4Parser.ParseFile(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            catch (InvalidDataException) { continue; }

            bool clipHasField = false;
            foreach (var (f, v) in ClipMetaReader.GetFields(root))
            {
                if (!f.Equals(field, StringComparison.OrdinalIgnoreCase)) continue;
                clipHasField = true;

                if (isPipeField)
                {
                    foreach (string item in v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        counts[item] = counts.TryGetValue(item, out int c) ? c + 1 : 1;
                }
                else
                {
                    counts[v] = counts.TryGetValue(v, out int c) ? c + 1 : 1;
                }
            }

            if (clipHasField) clipsWithField++;
        }

        return new VocabResult(counts, clipsWithField);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test clipmetascribe.Tests --filter "ClipMetaVocabTests" --verbosity minimal
```

Expected: 11 tests pass, 0 failures.

- [ ] **Step 5: Commit**

```
git add clipmeta.core/Read/ClipMetaVocab.cs clipmetascribe.Tests/ClipMetaVocabTests.cs
git commit -m "feat: add ClipMetaVocab.Enumerate with pipe-field splitting and tests"
```

---

## Task 2: VocabCommand + Program.cs + Tests

**Files:**
- Create: `clipmetascribe/Commands/VocabCommand.cs`
- Modify: `clipmetascribe/Program.cs`
- Create: `clipmetascribe.Tests/VocabCommandTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/VocabCommandTests.cs`:

```csharp
using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class VocabCommandTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void PrepareClip(string fileName, string field, string value)
    {
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(_tempDir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
    }

    [TestMethod]
    public void Run_MatchFound_PrintsScanHeader()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "game", writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "Scanning");
        StringAssert.Contains(output, "game");
    }

    [TestMethod]
    public void Run_MatchFound_PrintsValues()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "game", writer);

        StringAssert.Contains(writer.ToString(), "Team Fortress 2");
    }

    [TestMethod]
    public void Run_MatchFound_PrintsClipCount()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "game", writer);

        StringAssert.Contains(writer.ToString(), "clip(s)");
    }

    [TestMethod]
    public void Run_MatchFound_PrintsFooter()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "game", writer);

        StringAssert.Contains(writer.ToString(), "distinct value(s)");
    }

    [TestMethod]
    public void Run_NoMatch_PrintsNoFieldMessage()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "notes", writer);

        StringAssert.Contains(writer.ToString(), "no clips have field 'notes'");
    }

    [TestMethod]
    public void Run_PipeField_ShowsSplitItems()
    {
        PrepareClip("clip.mp4", "tags", "headshot|rocket jump");
        using var writer = new StringWriter();

        VocabCommand.Run(_tempDir, "tags", writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "headshot");
        StringAssert.Contains(output, "rocket jump");
    }

    [TestMethod]
    public void Run_AlwaysReturnsZero()
    {
        using var writer = new StringWriter();

        int exitCode = VocabCommand.Run(_tempDir, "game", writer);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void Run_DefaultOutput_UsesConsoleOut()
    {
        PrepareClip("clip.mp4", "game", "TF2");
        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            int exitCode = VocabCommand.Run(_tempDir, "game");

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(writer.ToString(), "Scanning");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test clipmetascribe.Tests --filter "VocabCommandTests" --verbosity minimal
```

Expected: build error, `VocabCommand` does not exist.

- [ ] **Step 3: Implement VocabCommand**

Create `clipmetascribe/Commands/VocabCommand.cs`:

```csharp
using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Displays distinct values for a metadata field across a directory of MP4 files.</summary>
internal static class VocabCommand
{
    /// <summary>
    /// Scans <paramref name="directory"/> for distinct values of <paramref name="field"/>
    /// and writes results to <paramref name="output"/> (defaults to <see cref="Console.Out"/>).
    /// </summary>
    /// <returns>Exit code 0 on success.</returns>
    internal static int Run(string directory, string field, TextWriter? output = null)
    {
        output ??= Console.Out;
        output.WriteLine($"Scanning {directory} for field: {field}");

        var result = ClipMetaVocab.Enumerate(directory, field);

        if (result.Counts.Count == 0)
        {
            output.WriteLine($"  (no clips have field '{field}')");
            return 0;
        }

        int labelWidth = result.Counts.Keys.Max(k => k.Length) + 2;
        foreach (var kvp in result.Counts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            output.WriteLine($"  {kvp.Key.PadRight(labelWidth)}{kvp.Value} clip(s)");

        output.WriteLine($"{result.Counts.Count} distinct value(s) across {result.ClipsWithField} clip(s).");
        return 0;
    }
}
```

- [ ] **Step 4: Wire --vocab in Program.cs**

In `clipmetascribe/Program.cs`, add the `--vocab` block immediately after the closing brace of the `--find` block (which ends at line 67) and before the `if (filePath == null || !File.Exists(filePath))` check.

Replace this exact block in Program.cs:

```csharp
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
```

With:

```csharp
        if (ContainsFlag(args, "--vocab"))
        {
            if (filePath == null || !Directory.Exists(filePath))
            {
                Console.Error.WriteLine("Error: --vocab requires a valid directory as the first argument.");
                return 1;
            }
            string? vocabField = GetFlag(args, "--vocab");
            if (vocabField == null || vocabField.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Error: --vocab requires a field name: --vocab <field>");
                return 1;
            }
            try
            {
                return VocabCommand.Run(filePath, vocabField);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }

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
```

- [ ] **Step 5: Update error message and PrintUsage in Program.cs**

Replace the no-write-op error message:

```csharp
            Console.Error.WriteLine("Error: No write operation specified. Use --set, --append, --clear, --clear-all, --list, or --stats.");
```

With:

```csharp
            Console.Error.WriteLine("Error: No write operation specified. Use --set, --append, --clear, --clear-all, --list, --stats, or see --vocab / --find for directory commands.");
```

Replace the PrintUsage content block (the entire `"""..."""` string):

```csharp
        Console.WriteLine("""
            clipmetascribe, MP4 metadata writer (Peckworks Lab)

            Usage:
              clipmetascribe "clip.mp4" --list
              clipmetascribe "clip.mp4" --stats
              clipmetascribe "clip.mp4" --set <field> <value>
              clipmetascribe "clip.mp4" --append <field> <value>
              clipmetascribe "clip.mp4" --clear <field>
              clipmetascribe "clip.mp4" --clear-all [--yes]
              clipmetascribe "C:\clips\" --find <field> <value>
              clipmetascribe "C:\clips\" --vocab <field>

            Fields:  game  players  tags  timecode  rating  notes  (or any custom name)

            Examples:
              clipmetascribe "clip.mp4" --list
              clipmetascribe "clip.mp4" --stats
              clipmetascribe "clip.mp4" --set game "Team Fortress 2"
              clipmetascribe "clip.mp4" --set tags "rocket jump|headshot"
              clipmetascribe "clip.mp4" --append tags "market garden"
              clipmetascribe "clip.mp4" --clear tags
              clipmetascribe "clip.mp4" --clear-all --yes
              clipmetascribe "clip.mp4" --set game "TF2" --append tags "headshot" --set rating "4"
              clipmetascribe "C:\clips\" --find game "Team Fortress 2"
              clipmetascribe "C:\clips\" --find tags "headshot"
              clipmetascribe "C:\clips\" --vocab game
              clipmetascribe "C:\clips\" --vocab tags

            Options:
              --dry-run      Preview changes without writing
              --backup       Keep .bak copy of original before write
              --verbose      Verbose logging (requires --log)
              --log <path>   Write structured log to file
              --yes          Skip confirmation prompts
              --version      Print version and exit

            Exit codes:  0=success  1=bad args / not found  2=write failure  3=verification failure
            """);
```

- [ ] **Step 6: Run tests to verify they pass**

```
dotnet test clipmetascribe.Tests --filter "VocabCommandTests" --verbosity minimal
```

Expected: 8 tests pass, 0 failures.

- [ ] **Step 7: Run full test suite**

```
dotnet test --verbosity minimal
```

Expected: all tests pass (previous 210 + 19 new = 229 total), 0 failures.

- [ ] **Step 8: Commit**

```
git add clipmetascribe/Commands/VocabCommand.cs clipmetascribe/Program.cs clipmetascribe.Tests/VocabCommandTests.cs
git commit -m "feat: add --vocab command to enumerate distinct field values across a directory"
```

---

## Self-Review

**Spec coverage:**
- ✅ `--vocab <field>` enumerates distinct values across a directory, Task 1 (ClipMetaVocab) + Task 2 (VocabCommand)
- ✅ Per-value clip counts, `VocabResult.Counts` dictionary + footer line
- ✅ Pipe fields split on `|`, `isPipeField` + `Split('|', ...)` in ClipMetaVocab
- ✅ Case-insensitive field name matching, `StringComparison.OrdinalIgnoreCase` in field match
- ✅ Case-insensitive value deduplication, `StringComparer.OrdinalIgnoreCase` on dictionary
- ✅ Malformed files silently skipped, three-exception catch block (same as ClipMetaFinder)
- ✅ Directory not file, `--vocab` block placed before `File.Exists` check, uses `Directory.Exists`
- ✅ `--flag` guard on field arg, `vocabField.StartsWith("--")` check in Program.cs
- ✅ IOException on directory access, wrapped in try/catch in Program.cs
- ✅ Recursive/non-recursive, `SearchOption` parameter exposed and tested
- ✅ Empty directory, returns empty result, tested
- ✅ No clips have field, prints `(no clips have field '...')`, tested
- ✅ PrintUsage updated, both Usage and Examples sections updated

**Placeholder scan:** No TBD, TODO, or "similar to Task N" patterns. All steps contain actual code.

**Type consistency:**
- `VocabResult` defined in Task 1 (`ClipMetaVocab.cs`), used in Task 2 (`VocabCommand.cs`), consistent
- `ClipMetaVocab.Enumerate(directory, field)` called correctly in `VocabCommand.Run`
- `result.Counts` and `result.ClipsWithField`, correct property names from the record definition
- `VocabCommand.Run(_tempDir, "game", writer)`, matches the `internal static int Run(string directory, string field, TextWriter? output = null)` signature
