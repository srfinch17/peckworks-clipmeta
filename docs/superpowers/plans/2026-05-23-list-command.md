# `--list` Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `--list` flag to clipmetascribe that reads and displays all `com.peckworkslab.clipmeta` metadata fields from an MP4 file.

**Architecture:** Field-extraction logic lives in `clipmeta.core` as `ClipMetaReader.GetFields(BoxNode)`, making it reusable and directly testable from `clipmetascribe.Tests` without a reference to the CLI project. `ListCommand` in clipmetascribe wraps it with I/O, parse, extract, format, print. `Program.cs` checks `--list` before the write-operation path and dispatches to `ListCommand.Run`.

**Tech Stack:** C# / .NET 10, MSTest 4, ClipMeta.Core (Mp4Parser, BoxNode, ClipMetaSchema, Mp4Writer)

---

## File Layout

| File | Action | Purpose |
|---|---|---|
| `clipmeta.core/Read/ClipMetaReader.cs` | Create | `GetFields(BoxNode root)`, walks the box tree and collects all clipmeta freeform atoms |
| `clipmetascribe/Commands/ListCommand.cs` | Create | `Run(string filePath, TextWriter? output)`, parse + format + print |
| `clipmetascribe/Program.cs` | Modify | Handle `--list` flag before write-operation check; update usage text |
| `clipmetascribe.Tests/ClipMetaReaderTests.cs` | Create | Unit tests (constructed BoxNode trees) + integration tests (real clips after real writes) |

---

## Task 1: ClipMetaReader, field extraction (TDD)

**Files:**
- Create: `clipmeta.core/Read/ClipMetaReader.cs`
- Create: `clipmetascribe.Tests/ClipMetaReaderTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/ClipMetaReaderTests.cs`:

```csharp
using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaReaderTests
{
    // ── Unit tests (manually constructed BoxNode trees) ──────────────────────

    [TestMethod]
    public void GetFields_NoIlst_ReturnsEmpty()
    {
        var root = new BoxNode { Type = "root" };
        var moov = new BoxNode { Type = "moov" };
        root.Children.Add(moov);

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(0, fields.Count);
    }

    [TestMethod]
    public void GetFields_EmptyIlst_ReturnsEmpty()
    {
        var root = BuildTreeWithIlst(new List<BoxNode>());

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(0, fields.Count);
    }

    [TestMethod]
    public void GetFields_SingleClipmetaField_ReturnsField()
    {
        var atom = MakeFreeformAtom("game", "Team Fortress 2");
        var root = BuildTreeWithIlst(new List<BoxNode> { atom });

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(1, fields.Count);
        Assert.AreEqual("game", fields[0].Field);
        Assert.AreEqual("Team Fortress 2", fields[0].Value);
    }

    [TestMethod]
    public void GetFields_SkipsForeignFreeformAtom()
    {
        var foreign = new BoxNode
        {
            Type = "----",
            IsEditable = true,
            EditableKey = "com.other.domain:field",
            DisplayValue = "something",
        };
        var root = BuildTreeWithIlst(new List<BoxNode> { foreign });

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(0, fields.Count);
    }

    [TestMethod]
    public void GetFields_SkipsNonFreeformAtom()
    {
        var nam = new BoxNode
        {
            Type = "©nam",
            IsEditable = true,
            EditableKey = "©nam",
            DisplayValue = "My Video",
        };
        var root = BuildTreeWithIlst(new List<BoxNode> { nam });

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(0, fields.Count);
    }

    [TestMethod]
    public void GetFields_SkipsAtomWithNullDisplayValue()
    {
        var atom = new BoxNode
        {
            Type = "----",
            IsEditable = true,
            EditableKey = ClipMetaSchema.AtomName("game"),
            DisplayValue = null,
        };
        var root = BuildTreeWithIlst(new List<BoxNode> { atom });

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(0, fields.Count);
    }

    [TestMethod]
    public void GetFields_MultipleFields_ReturnsAllInOrder()
    {
        var atoms = new List<BoxNode>
        {
            MakeFreeformAtom("game",   "Team Fortress 2"),
            MakeFreeformAtom("tags",   "rocket jump|headshot"),
            MakeFreeformAtom("rating", "4"),
        };
        var root = BuildTreeWithIlst(atoms);

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(3, fields.Count);
        Assert.AreEqual("game",   fields[0].Field);
        Assert.AreEqual("tags",   fields[1].Field);
        Assert.AreEqual("rating", fields[2].Field);
        Assert.AreEqual("Team Fortress 2",      fields[0].Value);
        Assert.AreEqual("rocket jump|headshot", fields[1].Value);
        Assert.AreEqual("4",                    fields[2].Value);
    }

    // ── Integration tests (real MP4 files written by Mp4Writer) ──────────────

    public static IEnumerable<object[]> PristineClips()
        => TestClipsLocator.AllPristine().Select(p => new object[] { p });

    private static readonly System.Collections.Concurrent.ConcurrentBag<string> _scratchFiles = new();

    [ClassCleanup]
    public static void CleanupScratch()
    {
        foreach (string path in _scratchFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
        }
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void GetFields_PristineClip_DoesNotThrow(string pristinePath)
    {
        var root   = Mp4Parser.ParseFile(pristinePath);
        var fields = ClipMetaReader.GetFields(root);
        Assert.IsNotNull(fields);
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void GetFields_AfterWriteAllFields_ReturnsAllFields(string pristinePath)
    {
        string scratch = ScratchClips.Prepare(pristinePath);
        _scratchFiles.Add(scratch);

        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)]   = "Team Fortress 2";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)]   = "rocket jump|headshot";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Rating)] = "4";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Notes)]  = "great clip";
        new Mp4Writer().WriteMetadata(scratch, mutation, NullLogger.Instance);

        var root   = Mp4Parser.ParseFile(scratch);
        var fields = ClipMetaReader.GetFields(root);

        var dict = fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);
        Assert.IsTrue(dict.ContainsKey(ClipMetaSchema.Game),   "game missing");
        Assert.IsTrue(dict.ContainsKey(ClipMetaSchema.Tags),   "tags missing");
        Assert.IsTrue(dict.ContainsKey(ClipMetaSchema.Rating), "rating missing");
        Assert.IsTrue(dict.ContainsKey(ClipMetaSchema.Notes),  "notes missing");
        Assert.AreEqual("Team Fortress 2",      dict[ClipMetaSchema.Game]);
        Assert.AreEqual("rocket jump|headshot", dict[ClipMetaSchema.Tags]);
        Assert.AreEqual("4",                    dict[ClipMetaSchema.Rating]);
        Assert.AreEqual("great clip",           dict[ClipMetaSchema.Notes]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static BoxNode MakeFreeformAtom(string field, string value) => new BoxNode
    {
        Type = "----",
        IsEditable = true,
        EditableKey = ClipMetaSchema.AtomName(field),
        DisplayValue = value,
    };

    private static BoxNode BuildTreeWithIlst(List<BoxNode> ilstChildren)
    {
        var ilst = new BoxNode { Type = "ilst" };
        ilst.Children.AddRange(ilstChildren);
        var meta = new BoxNode { Type = "meta" };
        meta.Children.Add(ilst);
        var udta = new BoxNode { Type = "udta" };
        udta.Children.Add(meta);
        var moov = new BoxNode { Type = "moov" };
        moov.Children.Add(udta);
        var root = new BoxNode { Type = "root" };
        root.Children.Add(moov);
        return root;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test clipmetascribe.Tests --filter "ClassName=ClipMetaScribe.Tests.ClipMetaReaderTests" --no-build 2>&1 | head -20
```

Expected: compile error, `error CS0234: The type or namespace name 'Read' does not exist in the namespace 'ClipMetaCore'`

- [ ] **Step 3: Create `clipmeta.core/Read/ClipMetaReader.cs`**

```csharp
using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Read;

/// <summary>Reads ClipMeta metadata fields from a parsed MP4 box tree.</summary>
public static class ClipMetaReader
{
    private static readonly string DomainPrefix = ClipMetaSchema.Domain + ":";

    /// <summary>
    /// Walks <paramref name="root"/> and returns all <c>com.peckworkslab.clipmeta</c> freeform atoms
    /// found in any <c>ilst</c> box, in document order.
    /// </summary>
    /// <param name="root">The root node returned by <see cref="Mp4Parser.ParseFile"/>.</param>
    /// <returns>
    /// A list of (Field, Value) pairs where Field is the bare field name (e.g. "game")
    /// and Value is the atom's display string.
    /// </returns>
    public static IReadOnlyList<(string Field, string Value)> GetFields(BoxNode root)
    {
        var result = new List<(string, string)>();
        CollectFromNode(root, result);
        return result;
    }

    private static void CollectFromNode(BoxNode node, List<(string, string)> result)
    {
        if (node.Type == "ilst")
        {
            foreach (var child in node.Children)
            {
                if (child.Type == "----" &&
                    child.EditableKey?.StartsWith(DomainPrefix, StringComparison.Ordinal) == true &&
                    child.DisplayValue != null)
                {
                    string field = child.EditableKey[DomainPrefix.Length..];
                    result.Add((field, child.DisplayValue));
                }
            }
            return; // ilst never contains nested ilst boxes
        }
        foreach (var child in node.Children)
            CollectFromNode(child, result);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test clipmetascribe.Tests --filter "ClassName=ClipMetaScribe.Tests.ClipMetaReaderTests"
```

Expected: all 8+ tests pass, 0 failures.

- [ ] **Step 5: Run the full test suite to confirm no regressions**

```
dotnet test
```

Expected: all tests pass (155 prior + new ClipMetaReader tests).

- [ ] **Step 6: Commit**

```
git add clipmeta.core/Read/ClipMetaReader.cs clipmetascribe.Tests/ClipMetaReaderTests.cs
git commit -m "feat: add ClipMetaReader.GetFields with unit and integration tests"
```

---

## Task 2: ListCommand + Program.cs wire-up

**Files:**
- Create: `clipmetascribe/Commands/ListCommand.cs`
- Modify: `clipmetascribe/Program.cs`

- [ ] **Step 1: Create `clipmetascribe/Commands/ListCommand.cs`**

```csharp
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Displays all com.peckworkslab.clipmeta metadata fields from an MP4 file.</summary>
internal static class ListCommand
{
    /// <summary>
    /// Parses <paramref name="filePath"/>, extracts ClipMeta fields, and writes
    /// formatted output to <paramref name="output"/> (defaults to <see cref="Console.Out"/>).
    /// </summary>
    /// <returns>Exit code 0 on success.</returns>
    internal static int Run(string filePath, TextWriter? output = null)
    {
        output ??= Console.Out;

        var root   = Mp4Parser.ParseFile(filePath);
        var fields = ClipMetaReader.GetFields(root);

        output.WriteLine(Path.GetFileName(filePath));

        if (fields.Count == 0)
        {
            output.WriteLine("  (no clipmeta metadata)");
            return 0;
        }

        int pad = fields.Max(f => f.Field.Length);
        foreach (var (field, value) in fields)
            output.WriteLine($"  {field.PadRight(pad)}  {value}");

        return 0;
    }
}
```

- [ ] **Step 2: Modify `clipmetascribe/Program.cs`**

The current try block (lines 59–96) starts with:
```csharp
try
{
    if (ContainsFlag(args, "--clear-all"))
```

Replace the entire try block and update PrintUsage. The new try block adds `--list` as the first check before `--clear-all`:

```csharp
try
{
    if (ContainsFlag(args, "--list"))
    {
        return ListCommand.Run(filePath);
    }

    if (ContainsFlag(args, "--clear-all"))
    {
        return WriteCommand.RunClearAll(filePath, dryRun, yes, backup ? filePath + ".bak" : null, logger);
    }

    var mutation = BuildMutation(args, filePath, dryRun, backup);

    if (mutation.SetFields.Count > 0 || mutation.AppendFields.Count > 0 || mutation.DeleteFields.Count > 0)
    {
        return WriteCommand.Run(filePath, mutation, logger);
    }

    Console.Error.WriteLine("Error: No write operation specified. Use --set, --append, --clear, --clear-all, or --list.");
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
```

Replace the PrintUsage method body with:

```csharp
private static void PrintUsage()
{
    Console.WriteLine("""
        clipmetascribe, MP4 metadata writer (Peckworks Lab)

        Usage:
          clipmetascribe "clip.mp4" --list
          clipmetascribe "clip.mp4" --set <field> <value>
          clipmetascribe "clip.mp4" --append <field> <value>
          clipmetascribe "clip.mp4" --clear <field>
          clipmetascribe "clip.mp4" --clear-all [--yes]

        Fields:  game  players  tags  timecode  rating  notes  (or any custom name)

        Examples:
          clipmetascribe "clip.mp4" --list
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
```

- [ ] **Step 3: Build**

```
dotnet build
```

Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 4: Run full test suite**

```
dotnet test
```

Expected: all tests pass, 0 failures.

- [ ] **Step 5: End-to-end smoke test**

Pick any pristine clip, write metadata to a scratch copy, then list it:

```powershell
$clip = (Get-ChildItem "testclips\pristine\*.mp4" | Select-Object -First 1).FullName
Copy-Item $clip "testclips\scratch\smoketest.mp4"
dotnet run --project clipmetascribe -- "testclips\scratch\smoketest.mp4" --set game "Team Fortress 2" --set tags "rocket jump|headshot" --set rating "4"
dotnet run --project clipmetascribe -- "testclips\scratch\smoketest.mp4" --list
Remove-Item "testclips\scratch\smoketest.mp4"
```

Expected `--list` output (fields appear in ilst document order; schema is always written first by the writer):
```
smoketest.mp4
  schema  1
  game    Team Fortress 2
  tags    rocket jump|headshot
  rating  4
```

Also verify a pristine clip with no clipmeta shows the no-metadata message:
```powershell
dotnet run --project clipmetascribe -- $clip --list
```

Expected:
```
<clipname>.mp4
  (no clipmeta metadata)
```

- [ ] **Step 6: Commit**

```
git add clipmetascribe/Commands/ListCommand.cs clipmetascribe/Program.cs
git commit -m "feat: wire --list command into clipmetascribe CLI"
```
