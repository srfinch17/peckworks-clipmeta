# Find Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `--find <field> <value>` flag to clipmetascribe that searches a directory for MP4 files containing the given field/value pair, printing matching file paths.

**Architecture:** `ClipMetaFinder.Find` in `clipmeta.core/Read/` does the directory walk + parse + filter (testable without CLI). `FindCommand` in `clipmetascribe/Commands/` is a thin I/O wrapper that formats results to a `TextWriter`. Program.cs detects `--find` BEFORE the `File.Exists` check (since the first arg is a directory, not a file).

**Matching rule:** Case-insensitive substring match against the full raw field value. So `--find tags "headshot"` matches a clip whose tags value is `"rocket jump|headshot"` because the string contains "headshot".

**Tech Stack:** C# / .NET 10, MSTest 4, no new NuGet packages

---

## File Structure

| Action | Path | Purpose |
|--------|------|---------|
| Create | `clipmeta.core/Read/ClipMetaFinder.cs` | Directory walk + parse + filter logic |
| Create | `clipmetascribe/Commands/FindCommand.cs` | CLI output wrapper |
| Modify | `clipmetascribe/Program.cs` | Wire `--find`, add `GetFindArgs` helper, update usage |
| Create | `clipmetascribe.Tests/ClipMetaFinderTests.cs` | Unit tests for ClipMetaFinder |
| Create | `clipmetascribe.Tests/FindCommandTests.cs` | Unit tests for FindCommand output |

---

### Task 1: ClipMetaFinder core logic + tests

**Files:**
- Create: `clipmeta.core/Read/ClipMetaFinder.cs`
- Create: `clipmetascribe.Tests/ClipMetaFinderTests.cs`

#### Context you need

**ClipMetaReader.GetFields** (already exists at `clipmeta.core/Read/ClipMetaReader.cs`):
```csharp
public static IReadOnlyList<(string Field, string Value)> GetFields(BoxNode root)
```
Returns bare field names (e.g. `"game"`) and unquoted values (e.g. `"Team Fortress 2"`). In the same namespace as ClipMetaFinder, so no `using` needed.

**Mp4Parser.ParseFile** (already exists at `clipmeta.core/Mp4/Mp4Parser.cs`):
```csharp
public static BoxNode ParseFile(string path)
```
Throws on malformed files. ClipMetaFinder must catch and skip (continue to next file).

**Test pattern for temp-dir tests** (ClipMetaFinder tests need a temp directory, not the shared scratch dir):
- `TestInitialize`: `_tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_tempDir);`
- `TestCleanup`: `if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);`
- Write clips to `_tempDir` directly via `File.Copy` + `Mp4Writer`

**Test namespaces**: `ClipMetaCore.Logging`, `ClipMetaCore.Mp4` (for `Mp4Parser`), `ClipMetaCore.Read`, `ClipMetaCore.Schema`, `ClipMetaCore.Write`, `ClipMetaScribe.Tests.Helpers`

**AtomName reminder**: `ClipMetaSchema.AtomName("game")` returns `"com.peckworkslab.clipmeta:game"`. Pass this as the key to `MetadataMutation.SetFields`.

**No scratch-file tracking needed** in ClipMetaFinderTests, the entire `_tempDir` is deleted in `TestCleanup`.

#### Step-by-step

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/ClipMetaFinderTests.cs`:

```csharp
using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaFinderTests
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string PrepareClipWithFields(string fileName, Dictionary<string, string> fieldValues)
    {
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(_tempDir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        foreach (var (atomName, value) in fieldValues)
            mutation.SetFields[atomName] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
        return dest;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Find_MatchingField_ReturnsFilePath()
    {
        string clip = PrepareClipWithFields("clip.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });

        var results = ClipMetaFinder.Find(_tempDir, "game", "Team Fortress 2").ToList();

        CollectionAssert.Contains(results, clip);
    }

    [TestMethod]
    public void Find_NonMatchingValue_ReturnsEmpty()
    {
        PrepareClipWithFields("clip.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });

        var results = ClipMetaFinder.Find(_tempDir, "game", "Counter-Strike").ToList();

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Find_FieldNameCaseInsensitive_ReturnsFile()
    {
        string clip = PrepareClipWithFields("clip.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });

        // Field name searched as uppercase, should still match
        var results = ClipMetaFinder.Find(_tempDir, "GAME", "Team Fortress 2").ToList();

        CollectionAssert.Contains(results, clip);
    }

    [TestMethod]
    public void Find_ValueCaseInsensitive_ReturnsFile()
    {
        string clip = PrepareClipWithFields("clip.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });

        // Value searched lowercase, substring match, case-insensitive
        var results = ClipMetaFinder.Find(_tempDir, "game", "team fortress").ToList();

        CollectionAssert.Contains(results, clip);
    }

    [TestMethod]
    public void Find_PipeField_MatchesSubstringWithinValue()
    {
        // tags is pipe-separated; "headshot" is a substring of "rocket jump|headshot"
        string clip = PrepareClipWithFields("clip.mp4",
            new() { [ClipMetaSchema.AtomName("tags")] = "rocket jump|headshot" });

        var results = ClipMetaFinder.Find(_tempDir, "tags", "headshot").ToList();

        CollectionAssert.Contains(results, clip);
    }

    [TestMethod]
    public void Find_NoMetadataClip_ReturnsEmpty()
    {
        // Copy a pristine clip (no metadata) directly to tempDir
        string source = TestClipsLocator.AllPristine().First();
        File.Copy(source, Path.Combine(_tempDir, "pristine.mp4"));

        var results = ClipMetaFinder.Find(_tempDir, "game", "anything").ToList();

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Find_MultipleClips_ReturnsOnlyMatching()
    {
        string clip1 = PrepareClipWithFields("clip1.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Team Fortress 2" });
        string clip2 = PrepareClipWithFields("clip2.mp4",
            new() { [ClipMetaSchema.AtomName("game")] = "Counter-Strike" });

        var results = ClipMetaFinder.Find(_tempDir, "game", "Team Fortress 2").ToList();

        CollectionAssert.Contains(results, clip1);
        CollectionAssert.DoesNotContain(results, clip2);
    }

    [TestMethod]
    public void Find_SameFileNotReturnedTwice()
    {
        // Even if a clip somehow has the search term in two separate fields,
        // it should only appear once in results.
        string clip = PrepareClipWithFields("clip.mp4", new()
        {
            [ClipMetaSchema.AtomName("game")] = "team",
            [ClipMetaSchema.AtomName("notes")] = "team effort",
        });

        // "team" matches both fields, clip should appear exactly once
        var results = ClipMetaFinder.Find(_tempDir, "game", "team").ToList();

        Assert.AreEqual(1, results.Count(r => r == clip));
    }

    [TestMethod]
    public void Find_RecursiveTrue_FindsInSubdirectory()
    {
        string subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);

        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(subDir, "nested.mp4");
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName("game")] = "Team Fortress 2";
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);

        // recursive = true (default) should find it
        var results = ClipMetaFinder.Find(_tempDir, "game", "Team Fortress 2", recursive: true).ToList();

        CollectionAssert.Contains(results, dest);
    }

    [TestMethod]
    public void Find_RecursiveFalse_DoesNotFindInSubdirectory()
    {
        string subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);

        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(subDir, "nested.mp4");
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName("game")] = "Team Fortress 2";
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);

        // recursive = false should NOT find the subdirectory clip
        var results = ClipMetaFinder.Find(_tempDir, "game", "Team Fortress 2", recursive: false).ToList();

        CollectionAssert.DoesNotContain(results, dest);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (ClipMetaFinder doesn't exist yet)**

```
cd C:\Users\srfin\Dropbox\Dev\repos\peckworks-clipmeta
dotnet build clipmetascribe.Tests 2>&1 | tail -5
```

Expected: compile error, `ClipMetaFinder` does not exist.

- [ ] **Step 3: Implement ClipMetaFinder**

Create `clipmeta.core/Read/ClipMetaFinder.cs`:

```csharp
using ClipMetaCore.Mp4;

namespace ClipMetaCore.Read;

/// <summary>Searches directories for MP4 files matching a clipmeta field/value filter.</summary>
public static class ClipMetaFinder
{
    /// <summary>
    /// Enumerates .mp4 files in <paramref name="directory"/> and yields paths where
    /// <paramref name="field"/> has a value containing <paramref name="value"/>
    /// (case-insensitive substring match).
    /// Malformed or unreadable files are silently skipped.
    /// </summary>
    /// <param name="directory">The directory to search.</param>
    /// <param name="field">Bare field name (e.g. "game"), matched case-insensitively.</param>
    /// <param name="value">Substring to search for within the field value, case-insensitively.</param>
    /// <param name="recursive">When true, searches subdirectories. Default: true.</param>
    public static IEnumerable<string> Find(
        string directory, string field, string value, bool recursive = true)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (string path in Directory.EnumerateFiles(directory, "*.mp4", option))
        {
            BoxNode root;
            try { root = Mp4Parser.ParseFile(path); }
            catch { continue; }

            var fields = ClipMetaReader.GetFields(root);
            foreach (var (f, v) in fields)
            {
                if (f.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                    v.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                    break; // don't yield the same file twice if multiple fields match
                }
            }
        }
    }
}
```

- [ ] **Step 4: Build and run tests**

```
dotnet build clipmetascribe.Tests
dotnet test clipmetascribe.Tests --filter "ClipMetaFinderTests"
```

Expected: all ClipMetaFinderTests pass.

- [ ] **Step 5: Run full test suite to verify no regressions**

```
dotnet test clipmetascribe.Tests
```

Expected: all previously passing tests still pass (was 183; now 183 + 9 = 192).

- [ ] **Step 6: Self-review ClipMetaFinder**

Read `clipmeta.core/Read/ClipMetaFinder.cs`. Check:
- `catch { continue; }` is present, malformed files are skipped
- `break` after `yield return`, no file yielded twice
- Both `SearchOption.AllDirectories` and `SearchOption.TopDirectoryOnly` are used correctly
- `StringComparison.OrdinalIgnoreCase` used in both field name and value comparisons
- XML doc comments on the public type and method

- [ ] **Step 7: Commit**

```
git add clipmeta.core/Read/ClipMetaFinder.cs clipmetascribe.Tests/ClipMetaFinderTests.cs
git commit -m "feat: add ClipMetaFinder.Find with directory search and integration tests"
```

---

### Task 2: FindCommand + Program.cs wiring + tests

**Files:**
- Create: `clipmetascribe/Commands/FindCommand.cs`
- Modify: `clipmetascribe/Program.cs`
- Create: `clipmetascribe.Tests/FindCommandTests.cs`

#### Context you need

**ClipMetaFinder.Find** (just implemented in Task 1):
```csharp
public static IEnumerable<string> Find(string directory, string field, string value, bool recursive = true)
```
Returns absolute paths of matching files.

**Program.cs arg parsing pattern**: `--find` takes TWO arguments after it (`--find <field> <value>`). The existing `GetFlag` helper only reads ONE arg after a flag name. Add a private `GetFindArgs` helper:
```csharp
private static (string? field, string? value) GetFindArgs(string[] args)
{
    int idx = Array.FindIndex(args, a => a.Equals("--find", StringComparison.OrdinalIgnoreCase));
    if (idx < 0) return (null, null);
    string? field = idx + 1 < args.Length ? args[idx + 1] : null;
    string? value = idx + 2 < args.Length ? args[idx + 2] : null;
    return (field, value);
}
```

**Critical Program.cs placement**: The `--find` check MUST go BEFORE the `File.Exists(filePath)` check (line ~42 in Program.cs), because the first positional arg is a directory for `--find`, not a file. The current structure is:

```csharp
string? filePath = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null;

if (filePath == null || !File.Exists(filePath))   // <-- BEFORE this line
{
    ...
}
```

Add the `--find` check between these two blocks.

**Output format** of FindCommand:
```
Searching C:\clips\ for game = "Team Fortress 2"
  clip1.mp4
  subfolder\clip2.mp4
2 match(es) found.
```

With no matches:
```
Searching C:\clips\ for tags = "headshot"
  No matches found.
```

Use `Path.GetRelativePath(directory, match)` to produce display paths.

**InternalsVisibleTo**: `clipmetascribe/AssemblyInfo.cs` already has `[assembly: InternalsVisibleTo("clipmetascribe.Tests")]`. `clipmetascribe.Tests.csproj` already references `clipmetascribe.csproj`. No changes needed.

**Namespaces for FindCommandTests**: `ClipMetaCore.Logging`, `ClipMetaCore.Schema`, `ClipMetaCore.Write`, `ClipMetaScribe.Commands`, `ClipMetaScribe.Tests.Helpers`

**Test pattern**: Use `TestInitialize`/`TestCleanup` with `_tempDir` (same as ClipMetaFinderTests). No shared scratch dir, temp dir is wiped entirely in cleanup.

#### Step-by-step

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/FindCommandTests.cs`:

```csharp
using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class FindCommandTests
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void PrepareClip(string fileName, string field, string value)
    {
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(_tempDir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Run_MatchFound_PrintsSearchHeader()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        FindCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "Searching");
        StringAssert.Contains(output, "game");
        StringAssert.Contains(output, "Team Fortress 2");
    }

    [TestMethod]
    public void Run_MatchFound_PrintsRelativePath()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        FindCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        // Should show "clip.mp4" (relative path from tempDir), not the full absolute path
        StringAssert.Contains(writer.ToString(), "clip.mp4");
    }

    [TestMethod]
    public void Run_NoMatch_PrintsNoMatchesMessage()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        FindCommand.Run(_tempDir, "game", "Counter-Strike", writer);

        StringAssert.Contains(writer.ToString(), "No matches found");
    }

    [TestMethod]
    public void Run_OneMatch_PrintsOneMatchCount()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        FindCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        StringAssert.Contains(writer.ToString(), "1 match(es) found.");
    }

    [TestMethod]
    public void Run_MultipleMatches_PrintsMatchCount()
    {
        PrepareClip("clip1.mp4", "game", "Team Fortress 2");
        PrepareClip("clip2.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        FindCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        StringAssert.Contains(writer.ToString(), "2 match(es) found.");
    }

    [TestMethod]
    public void Run_AlwaysReturnsZero()
    {
        using var writer = new StringWriter();

        int exitCode = FindCommand.Run(_tempDir, "game", "anything", writer);

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

            int exitCode = FindCommand.Run(_tempDir, "game", "TF2");

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(writer.ToString(), "Searching");
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
dotnet build clipmetascribe.Tests 2>&1 | tail -5
```

Expected: compile error, `FindCommand` does not exist.

- [ ] **Step 3: Implement FindCommand**

Create `clipmetascribe/Commands/FindCommand.cs`:

```csharp
using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Searches a directory for MP4 files matching a clipmeta field/value filter.</summary>
internal static class FindCommand
{
    /// <summary>
    /// Searches <paramref name="directory"/> for clips where <paramref name="field"/> contains
    /// <paramref name="value"/> and writes results to <paramref name="output"/>
    /// (defaults to <see cref="Console.Out"/>).
    /// </summary>
    /// <returns>Exit code 0 on success.</returns>
    internal static int Run(string directory, string field, string value, TextWriter? output = null)
    {
        output ??= Console.Out;

        output.WriteLine($"Searching {directory} for {field} = \"{value}\"");

        int count = 0;
        foreach (string match in ClipMetaFinder.Find(directory, field, value))
        {
            string relative = Path.GetRelativePath(directory, match);
            output.WriteLine($"  {relative}");
            count++;
        }

        if (count == 0)
            output.WriteLine("  No matches found.");
        else
            output.WriteLine($"{count} match(es) found.");

        return 0;
    }
}
```

- [ ] **Step 4: Wire --find into Program.cs**

**4a. Add `GetFindArgs` helper** at the bottom of `Program.cs` (after the existing `GetFlag` method):

```csharp
    private static (string? field, string? value) GetFindArgs(string[] args)
    {
        int idx = Array.FindIndex(args, a => a.Equals("--find", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return (null, null);
        string? field = idx + 1 < args.Length ? args[idx + 1] : null;
        string? value = idx + 2 < args.Length ? args[idx + 2] : null;
        return (field, value);
    }
```

**4b. Add `--find` check in Main**, insert this block BETWEEN the `filePath` extraction and the `File.Exists` check:

Find this exact sequence in Program.cs:
```csharp
        string? filePath = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null;

        if (filePath == null || !File.Exists(filePath))
```

Insert between them (after the `filePath =` line, before the `if (filePath == null`):
```csharp
        // --find takes a directory path, so handle it before the File.Exists check
        if (ContainsFlag(args, "--find"))
        {
            if (filePath == null || !Directory.Exists(filePath))
            {
                Console.Error.WriteLine("Error: --find requires a valid directory as the first argument.");
                return 1;
            }
            var (findField, findValue) = GetFindArgs(args);
            if (findField == null || findValue == null)
            {
                Console.Error.WriteLine("Error: --find requires a field name and value: --find <field> <value>");
                return 1;
            }
            return FindCommand.Run(filePath, findField, findValue);
        }

```

**4c. Update PrintUsage**, in the raw string literal, add to the Usage section:
```
  clipmetascribe "C:\clips\" --find game "Team Fortress 2"
```
And add to the Examples section:
```
  clipmetascribe "C:\clips\" --find game "Team Fortress 2"
  clipmetascribe "C:\clips\" --find tags "headshot"
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

Expected: all tests pass (was 192 after Task 1; new total should be 192 + 7 = 199).

- [ ] **Step 7: Self-review**

Read `FindCommand.cs` and the Program.cs changes. Check:
- `--find` check is BEFORE the `File.Exists` check (not inside the `try` block)
- `GetFindArgs` correctly reads `args[idx+1]` (field) and `args[idx+2]` (value)
- `Directory.Exists` (not `File.Exists`) used for the `--find` path validation
- `Path.GetRelativePath(directory, match)` used for display (relative, not absolute)
- Match count printed as `"{count} match(es) found."` (exact format, tests assert this)
- "No matches found." message uses `  No matches found.` (two leading spaces, period at end)

- [ ] **Step 8: Commit**

```
git add clipmetascribe/Commands/FindCommand.cs clipmetascribe/Program.cs clipmetascribe.Tests/FindCommandTests.cs
git commit -m "feat: add --find command to search a directory for clips by field/value"
```
