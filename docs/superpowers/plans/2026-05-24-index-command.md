# ClipMeta Index Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `--index` and `--index-search` commands that cache MP4 metadata to a file so repeat searches don't require scanning every clip.

**Architecture:** `ClipMetaIndex` (core library) scans a directory, builds an `IndexData` snapshot, and serializes it to a line-based text file (`.clipmeta-index`) inside the directory. `ClipMetaSearch` does in-memory substring searches over a loaded `IndexData`. Two thin CLI commands, `IndexCommand` and `IndexSearchCommand`, wire these classes to the `clipmetascribe` argument parser.

**Tech Stack:** C# / .NET 10, MSTest 4, zero external NuGet packages. All serialization hand-written using `TextWriter`/`TextReader` with a simple line-keyword format.

---

## Codebase Context

**Solution root:** `C:\Users\srfin\Dropbox\Dev\repos\peckworks-clipmeta`

No `.sln` file. Run tests with:
```
dotnet test clipmetascribe.Tests
```
from the solution root.

**Existing patterns to follow:**

- Core library lives in `clipmeta.core/Read/` (`ClipMetaFinder.cs`, `ClipMetaVocab.cs`, `ClipMetaExporter.cs`)
- CLI commands live in `clipmetascribe/Commands/` (`FindCommand.cs`, `VocabCommand.cs`, `ExportCommand.cs`)
- All CLI commands accept `TextWriter? output = null` and use `output ??= Console.Out` for testability
- All tests use a `_tempDir` created in `[TestInitialize]` and deleted in `[TestCleanup]`
- `TestClipsLocator.AllPristine().First()` provides a real MP4 for integration tests
- `PrepareClip` helper copies a pristine clip and calls `Mp4Writer.WriteMetadata` to set metadata
- Zero external NuGet packages, all serialization hand-written

**Key types already defined:**
- `ClipMetaSchema.Schema`, the internal schema field name to exclude from output
- `ClipMetaReader.GetFields(BoxNode root)`, returns `IReadOnlyList<(string Field, string Value)>`
- `Mp4Parser.ParseFile(string path)`, throws `IOException`, `UnauthorizedAccessException`, `InvalidDataException` on bad files
- `ExportRecord(string FilePath, IReadOnlyList<(string Field, string Value)> Fields)`, pattern for field data
- `ClipMetaSchema.AtomName(field)`, converts bare field name to full atom name for `MetadataMutation.SetFields`

**Index file format** (`.clipmeta-index`, UTF-8 text, line-keyword format):
```
version 1
built 2026-05-24T12:34:56.1234567+00:00
directory C:\clips
---
path C:\clips\clip1.mp4
size 12345678
modified 2026-05-20T10:00:00.0000000+00:00
field game Team Fortress 2
field tags rocket jump|headshot
---
path C:\clips\clip2.mp4
size 9876543
modified 2026-05-19T08:00:00.0000000+00:00
field game Counter-Strike 2
```

Parsing rules: each line is split on the **first space** to get `keyword` and `value`. `---` marks the start of a new entry (header ends at first `---`). Field values may contain spaces and `|`. The `schema` internal field is excluded from index entries (same as export).

---

## File Map

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `clipmeta.core/Read/ClipMetaIndex.cs` | `IndexEntry` record, `IndexData` record, `ClipMetaIndex` static class (Build, Write, Read, WriteToFile, ReadFromFile) |
| Create | `clipmeta.core/Read/ClipMetaSearch.cs` | `ClipMetaSearch` static class (Find) |
| Create | `clipmetascribe/Commands/IndexCommand.cs` | `--index` CLI command |
| Create | `clipmetascribe/Commands/IndexSearchCommand.cs` | `--index-search` CLI command |
| Modify | `clipmetascribe/Program.cs` | Add `--index`/`--index-search` blocks + `GetIndexSearchArgs` helper + PrintUsage updates |
| Create | `clipmetascribe.Tests/ClipMetaIndexTests.cs` | Unit+integration tests for `ClipMetaIndex` |
| Create | `clipmetascribe.Tests/ClipMetaSearchTests.cs` | Pure unit tests for `ClipMetaSearch` |
| Create | `clipmetascribe.Tests/IndexCommandTests.cs` | Integration tests for `IndexCommand` |
| Create | `clipmetascribe.Tests/IndexSearchCommandTests.cs` | Integration tests for `IndexSearchCommand` |

---

## Task 1: ClipMetaIndex, build, serialize, deserialize

**Files:**
- Create: `clipmeta.core/Read/ClipMetaIndex.cs`
- Test: `clipmetascribe.Tests/ClipMetaIndexTests.cs`

---

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/ClipMetaIndexTests.cs`:

```csharp
using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaIndexTests
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

    private string PrepareClip(string fileName, string field, string value)
    {
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(_tempDir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
        return dest;
    }

    [TestMethod]
    public void Build_EmptyDirectory_ReturnsZeroEntries()
    {
        var data = ClipMetaIndex.Build(_tempDir);

        Assert.AreEqual(0, data.Entries.Count);
    }

    [TestMethod]
    public void Build_WithMetadataClip_ReturnsOneEntry()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var data = ClipMetaIndex.Build(_tempDir);

        Assert.AreEqual(1, data.Entries.Count);
    }

    [TestMethod]
    public void Build_EntryContainsWrittenField()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var data = ClipMetaIndex.Build(_tempDir);

        Assert.IsTrue(data.Entries[0].Fields.Any(f => f.Field == "game" && f.Value == "Team Fortress 2"));
    }

    [TestMethod]
    public void Build_ExcludesSchemaField()
    {
        PrepareClip("clip.mp4", "game", "TF2");

        var data = ClipMetaIndex.Build(_tempDir);

        Assert.IsFalse(data.Entries[0].Fields.Any(f => f.Field.Equals("schema", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Build_SetsDirectory()
    {
        var data = ClipMetaIndex.Build(_tempDir);

        Assert.AreEqual(_tempDir, data.Directory);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_Directory()
    {
        var data = ClipMetaIndex.Build(_tempDir);
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual(data.Directory, result.Directory);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_EntryFilePath()
    {
        PrepareClip("clip.mp4", "game", "TF2");
        var data = ClipMetaIndex.Build(_tempDir);
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual(data.Entries[0].FilePath, result.Entries[0].FilePath);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_Fields()
    {
        PrepareClip("clip.mp4", "game", "Team Fortress 2");
        var data = ClipMetaIndex.Build(_tempDir);
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.IsTrue(result.Entries[0].Fields.Any(f => f.Field == "game" && f.Value == "Team Fortress 2"));
    }

    [TestMethod]
    public void WriteRead_EmptyEntries_ReturnsZeroEntries()
    {
        var data = ClipMetaIndex.Build(_tempDir);
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual(0, result.Entries.Count);
    }

    [TestMethod]
    public void WriteRead_RoundTrips_FileSizeAndModified()
    {
        PrepareClip("clip.mp4", "game", "TF2");
        var data = ClipMetaIndex.Build(_tempDir);
        using var sw = new StringWriter();
        ClipMetaIndex.Write(data, sw);
        using var sr = new StringReader(sw.ToString());

        var result = ClipMetaIndex.Read(sr);

        Assert.AreEqual(data.Entries[0].FileSizeBytes, result.Entries[0].FileSizeBytes);
        Assert.AreEqual(
            data.Entries[0].LastModified.ToUnixTimeSeconds(),
            result.Entries[0].LastModified.ToUnixTimeSeconds());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test clipmetascribe.Tests --filter ClipMetaIndexTests
```

Expected: compilation error, `ClipMetaIndex`, `IndexEntry`, `IndexData` not defined.

- [ ] **Step 3: Implement ClipMetaIndex**

Create `clipmeta.core/Read/ClipMetaIndex.cs`:

```csharp
using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Read;

/// <summary>A single entry in a clipmeta index representing one MP4 file's cached metadata.</summary>
public record IndexEntry(
    /// <summary>Full path to the MP4 file.</summary>
    string FilePath,
    /// <summary>File size in bytes at index build time.</summary>
    long FileSizeBytes,
    /// <summary>UTC last-write time at index build time.</summary>
    DateTimeOffset LastModified,
    /// <summary>All user-facing clipmeta fields at index build time. The schema field is excluded.</summary>
    IReadOnlyList<(string Field, string Value)> Fields);

/// <summary>Snapshot of all indexed MP4 metadata for a directory.</summary>
public record IndexData(
    /// <summary>Directory that was indexed.</summary>
    string Directory,
    /// <summary>UTC timestamp when the index was built.</summary>
    DateTimeOffset Built,
    /// <summary>One entry per MP4 file in the indexed directory.</summary>
    IReadOnlyList<IndexEntry> Entries);

/// <summary>Builds and serializes a metadata index for a directory of MP4 files.</summary>
public static class ClipMetaIndex
{
    /// <summary>The file name written inside the indexed directory.</summary>
    public const string IndexFileName = ".clipmeta-index";

    /// <summary>
    /// Scans all .mp4 files in <paramref name="directory"/> and returns an
    /// <see cref="IndexData"/> snapshot. Malformed or unreadable files are silently skipped.
    /// The internal schema field is excluded.
    /// </summary>
    public static IndexData Build(string directory, bool recursive = true)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = new List<IndexEntry>();

        foreach (string path in Directory.EnumerateFiles(directory, "*.mp4", option))
        {
            BoxNode root;
            try { root = Mp4Parser.ParseFile(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            catch (InvalidDataException) { continue; }

            var fields = ClipMetaReader.GetFields(root)
                .Where(f => !f.Field.Equals(ClipMetaSchema.Schema, StringComparison.Ordinal))
                .ToList();

            var info = new FileInfo(path);
            entries.Add(new IndexEntry(
                path,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                fields));
        }

        return new IndexData(directory, DateTimeOffset.UtcNow, entries);
    }

    /// <summary>
    /// Serializes <paramref name="data"/> to <paramref name="writer"/> in the clipmeta index format.
    /// Each entry is preceded by a <c>---</c> separator line. Field values are written as-is;
    /// values must not contain embedded newlines.
    /// </summary>
    public static void Write(IndexData data, TextWriter writer)
    {
        writer.WriteLine("version 1");
        writer.WriteLine($"built {data.Built:O}");
        writer.WriteLine($"directory {data.Directory}");
        foreach (var entry in data.Entries)
        {
            writer.WriteLine("---");
            writer.WriteLine($"path {entry.FilePath}");
            writer.WriteLine($"size {entry.FileSizeBytes}");
            writer.WriteLine($"modified {entry.LastModified:O}");
            foreach (var (field, value) in entry.Fields)
                writer.WriteLine($"field {field} {value}");
        }
    }

    /// <summary>Deserializes an <see cref="IndexData"/> from <paramref name="reader"/>.</summary>
    public static IndexData Read(TextReader reader)
    {
        string directory = "";
        DateTimeOffset built = DateTimeOffset.UtcNow;
        var entries = new List<IndexEntry>();

        bool inHeader = true;
        string? filePath = null;
        long size = 0;
        DateTimeOffset modified = default;
        var fields = new List<(string, string)>();

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line == "---")
            {
                if (!inHeader && filePath != null)
                    entries.Add(new IndexEntry(filePath, size, modified, fields.ToList()));
                inHeader = false;
                filePath = null;
                size = 0;
                modified = default;
                fields = new List<(string, string)>();
                continue;
            }

            int spaceIdx = line.IndexOf(' ');
            if (spaceIdx < 0) continue;
            string keyword = line[..spaceIdx];
            string value = line[(spaceIdx + 1)..];

            if (inHeader)
            {
                if (keyword == "built") built = DateTimeOffset.Parse(value);
                else if (keyword == "directory") directory = value;
            }
            else
            {
                if (keyword == "path") filePath = value;
                else if (keyword == "size" && long.TryParse(value, out long parsedSize)) size = parsedSize;
                else if (keyword == "modified") modified = DateTimeOffset.Parse(value);
                else if (keyword == "field")
                {
                    int fieldSpace = value.IndexOf(' ');
                    if (fieldSpace >= 0)
                        fields.Add((value[..fieldSpace], value[(fieldSpace + 1)..]));
                }
            }
        }

        if (!inHeader && filePath != null)
            entries.Add(new IndexEntry(filePath, size, modified, fields.ToList()));

        return new IndexData(directory, built, entries);
    }

    /// <summary>Writes <paramref name="data"/> to <paramref name="filePath"/> using UTF-8.</summary>
    public static void WriteToFile(IndexData data, string filePath)
    {
        using var writer = new StreamWriter(filePath, append: false, System.Text.Encoding.UTF8);
        Write(data, writer);
    }

    /// <summary>Reads an <see cref="IndexData"/> from <paramref name="filePath"/> using UTF-8.</summary>
    public static IndexData ReadFromFile(string filePath)
    {
        using var reader = new StreamReader(filePath, System.Text.Encoding.UTF8);
        return Read(reader);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test clipmetascribe.Tests --filter ClipMetaIndexTests
```

Expected: all 10 tests PASS.

- [ ] **Step 5: Build the solution**

```
dotnet build clipmetascribe.Tests
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Commit**

```
git add clipmeta.core/Read/ClipMetaIndex.cs clipmetascribe.Tests/ClipMetaIndexTests.cs
git commit -m "feat: add ClipMetaIndex with build/serialize/deserialize"
```

---

## Task 2: ClipMetaSearch, in-memory index search

**Files:**
- Create: `clipmeta.core/Read/ClipMetaSearch.cs`
- Test: `clipmetascribe.Tests/ClipMetaSearchTests.cs`

---

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/ClipMetaSearchTests.cs`:

```csharp
using ClipMetaCore.Read;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaSearchTests
{
    private static IndexEntry MakeEntry(string path, params (string Field, string Value)[] fields)
        => new IndexEntry(path, 0, DateTimeOffset.UtcNow, fields.ToList());

    private static IndexData MakeIndex(params IndexEntry[] entries)
        => new IndexData(@"C:\clips", DateTimeOffset.UtcNow, entries.ToList());

    [TestMethod]
    public void Find_MatchingField_ReturnsEntry()
    {
        var index = MakeIndex(MakeEntry("clip.mp4", ("game", "Team Fortress 2")));

        var results = ClipMetaSearch.Find(index, "game", "Team Fortress 2");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("clip.mp4", results[0].FilePath);
    }

    [TestMethod]
    public void Find_NoMatch_ReturnsEmpty()
    {
        var index = MakeIndex(MakeEntry("clip.mp4", ("game", "Team Fortress 2")));

        var results = ClipMetaSearch.Find(index, "game", "Counter-Strike");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Find_CaseInsensitive_Matches()
    {
        var index = MakeIndex(MakeEntry("clip.mp4", ("game", "Team Fortress 2")));

        var results = ClipMetaSearch.Find(index, "GAME", "team fortress");

        Assert.AreEqual(1, results.Count);
    }

    [TestMethod]
    public void Find_SubstringMatch_Matches()
    {
        var index = MakeIndex(MakeEntry("clip.mp4", ("game", "Team Fortress 2")));

        var results = ClipMetaSearch.Find(index, "game", "Fortress");

        Assert.AreEqual(1, results.Count);
    }

    [TestMethod]
    public void Find_EmptyIndex_ReturnsEmpty()
    {
        var index = MakeIndex();

        var results = ClipMetaSearch.Find(index, "game", "TF2");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Find_MultipleEntries_ReturnsOnlyMatches()
    {
        var index = MakeIndex(
            MakeEntry("clip1.mp4", ("game", "Team Fortress 2")),
            MakeEntry("clip2.mp4", ("game", "Counter-Strike 2")));

        var results = ClipMetaSearch.Find(index, "game", "Team Fortress");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("clip1.mp4", results[0].FilePath);
    }

    [TestMethod]
    public void Find_EntryWithNoMatchingField_NotIncluded()
    {
        var index = MakeIndex(MakeEntry("clip.mp4", ("notes", "some notes")));

        var results = ClipMetaSearch.Find(index, "game", "TF2");

        Assert.AreEqual(0, results.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
dotnet test clipmetascribe.Tests --filter ClipMetaSearchTests
```

Expected: compilation error, `ClipMetaSearch` not defined.

- [ ] **Step 3: Implement ClipMetaSearch**

Create `clipmeta.core/Read/ClipMetaSearch.cs`:

```csharp
namespace ClipMetaCore.Read;

/// <summary>Searches a loaded <see cref="IndexData"/> for entries matching field/value criteria.</summary>
public static class ClipMetaSearch
{
    /// <summary>
    /// Returns all entries in <paramref name="index"/> where <paramref name="field"/> has a value
    /// containing <paramref name="value"/> (case-insensitive substring match).
    /// </summary>
    public static IReadOnlyList<IndexEntry> Find(IndexData index, string field, string value)
    {
        var results = new List<IndexEntry>();
        foreach (var entry in index.Entries)
        {
            foreach (var (f, v) in entry.Fields)
            {
                if (f.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                    v.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(entry);
                    break;
                }
            }
        }
        return results;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test clipmetascribe.Tests --filter ClipMetaSearchTests
```

Expected: all 7 tests PASS.

- [ ] **Step 5: Run all tests to confirm no regressions**

```
dotnet test clipmetascribe.Tests
```

Expected: all existing tests still pass (one pre-existing flaky test `Write_NoTempFileLeft_AfterSuccess` may occasionally fail, this is an OS-level race condition unrelated to this work).

- [ ] **Step 6: Commit**

```
git add clipmeta.core/Read/ClipMetaSearch.cs clipmetascribe.Tests/ClipMetaSearchTests.cs
git commit -m "feat: add ClipMetaSearch for in-memory index lookups"
```

---

## Task 3: IndexCommand + IndexSearchCommand + Program.cs wiring

**Files:**
- Create: `clipmetascribe/Commands/IndexCommand.cs`
- Create: `clipmetascribe/Commands/IndexSearchCommand.cs`
- Modify: `clipmetascribe/Program.cs`
- Test: `clipmetascribe.Tests/IndexCommandTests.cs`
- Test: `clipmetascribe.Tests/IndexSearchCommandTests.cs`

---

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/IndexCommandTests.cs`:

```csharp
using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class IndexCommandTests
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
    public void Run_ValidDirectory_CreatesIndexFile()
    {
        PrepareClip("clip.mp4", "game", "TF2");
        using var writer = new StringWriter();

        IndexCommand.Run(_tempDir, writer);

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, ClipMetaIndex.IndexFileName)));
    }

    [TestMethod]
    public void Run_ValidDirectory_ReturnsZero()
    {
        PrepareClip("clip.mp4", "game", "TF2");
        using var writer = new StringWriter();

        int exitCode = IndexCommand.Run(_tempDir, writer);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void Run_ValidDirectory_PrintsFileCount()
    {
        PrepareClip("clip1.mp4", "game", "TF2");
        PrepareClip("clip2.mp4", "game", "CS2");
        using var writer = new StringWriter();

        IndexCommand.Run(_tempDir, writer);

        StringAssert.Contains(writer.ToString(), "2");
    }

    [TestMethod]
    public void Run_EmptyDirectory_CreatesIndexWithZeroEntries()
    {
        using var writer = new StringWriter();

        IndexCommand.Run(_tempDir, writer);

        var data = ClipMetaIndex.ReadFromFile(Path.Combine(_tempDir, ClipMetaIndex.IndexFileName));
        Assert.AreEqual(0, data.Entries.Count);
    }

    [TestMethod]
    public void Run_DefaultOutput_UsesConsoleOut()
    {
        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            int exitCode = IndexCommand.Run(_tempDir);

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(writer.ToString(), "Indexed");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
```

Create `clipmetascribe.Tests/IndexSearchCommandTests.cs`:

```csharp
using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class IndexSearchCommandTests
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

    private void PrepareClipAndBuildIndex(string fileName, string field, string value)
    {
        string source = TestClipsLocator.AllPristine().First();
        string dest   = Path.Combine(_tempDir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
        var data = ClipMetaIndex.Build(_tempDir);
        ClipMetaIndex.WriteToFile(data, Path.Combine(_tempDir, ClipMetaIndex.IndexFileName));
    }

    [TestMethod]
    public void Run_MatchFound_PrintsRelativePath()
    {
        PrepareClipAndBuildIndex("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        IndexSearchCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        StringAssert.Contains(writer.ToString(), "clip.mp4");
    }

    [TestMethod]
    public void Run_MatchFound_PrintsMatchCount()
    {
        PrepareClipAndBuildIndex("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        IndexSearchCommand.Run(_tempDir, "game", "Team Fortress 2", writer);

        StringAssert.Contains(writer.ToString(), "1 match(es) found.");
    }

    [TestMethod]
    public void Run_NoMatch_PrintsNoMatchesMessage()
    {
        PrepareClipAndBuildIndex("clip.mp4", "game", "Team Fortress 2");
        using var writer = new StringWriter();

        IndexSearchCommand.Run(_tempDir, "game", "Counter-Strike", writer);

        StringAssert.Contains(writer.ToString(), "No matches found.");
    }

    [TestMethod]
    public void Run_NoIndexFile_ReturnsOne()
    {
        // No index built, directory exists but .clipmeta-index doesn't
        using var writer = new StringWriter();

        int exitCode = IndexSearchCommand.Run(_tempDir, "game", "TF2", writer);

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public void Run_MatchFound_ReturnsZero()
    {
        PrepareClipAndBuildIndex("clip.mp4", "game", "TF2");
        using var writer = new StringWriter();

        int exitCode = IndexSearchCommand.Run(_tempDir, "game", "TF2", writer);

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public void Run_DefaultOutput_UsesConsoleOut()
    {
        PrepareClipAndBuildIndex("clip.mp4", "game", "TF2");
        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            int exitCode = IndexSearchCommand.Run(_tempDir, "game", "TF2");

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
dotnet test clipmetascribe.Tests --filter "IndexCommandTests|IndexSearchCommandTests"
```

Expected: compilation error, `IndexCommand`, `IndexSearchCommand` not defined.

- [ ] **Step 3: Implement IndexCommand**

Create `clipmetascribe/Commands/IndexCommand.cs`:

```csharp
using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Builds a metadata index for a directory of MP4 files.</summary>
internal static class IndexCommand
{
    /// <summary>
    /// Scans <paramref name="directory"/>, builds a metadata index, and writes it to
    /// <c>.clipmeta-index</c> inside the directory.
    /// </summary>
    /// <returns>Exit code 0 on success.</returns>
    internal static int Run(string directory, TextWriter? output = null)
    {
        output ??= Console.Out;

        var data = ClipMetaIndex.Build(directory);
        string indexPath = Path.Combine(directory, ClipMetaIndex.IndexFileName);
        ClipMetaIndex.WriteToFile(data, indexPath);

        output.WriteLine($"Indexed {data.Entries.Count} file(s). Index saved to {indexPath}.");
        return 0;
    }
}
```

- [ ] **Step 4: Implement IndexSearchCommand**

Create `clipmetascribe/Commands/IndexSearchCommand.cs`:

```csharp
using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Searches a clipmeta index for files matching a field/value filter.</summary>
internal static class IndexSearchCommand
{
    /// <summary>
    /// Loads <c>.clipmeta-index</c> from <paramref name="directory"/> and writes matching
    /// file paths to <paramref name="output"/>. Returns exit code 1 if no index exists.
    /// </summary>
    /// <returns>Exit code 0 on success, 1 if no index found, 2 on read error.</returns>
    internal static int Run(string directory, string field, string value, TextWriter? output = null)
    {
        output ??= Console.Out;

        string indexPath = Path.Combine(directory, ClipMetaIndex.IndexFileName);
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"Error: No index found at '{indexPath}'. Run --index first.");
            return 1;
        }

        IndexData data;
        try { data = ClipMetaIndex.ReadFromFile(indexPath); }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Error reading index: {ex.Message}");
            return 2;
        }

        var matches = ClipMetaSearch.Find(data, field, value);

        output.WriteLine($"Searching index for {field} = \"{value}\"");
        if (matches.Count == 0)
        {
            output.WriteLine("  No matches found.");
        }
        else
        {
            foreach (var entry in matches)
            {
                string relative = Path.GetRelativePath(directory, entry.FilePath);
                output.WriteLine($"  {relative}");
            }
            output.WriteLine($"{matches.Count} match(es) found.");
        }
        return 0;
    }
}
```

- [ ] **Step 5: Wire up Program.cs**

Open `clipmetascribe/Program.cs`. Make these three changes:

**Change 1:** Add `--index` block after the `--vocab` block (around line 93) and before the `--export` block. Insert:

```csharp
        if (ContainsFlag(args, "--index"))
        {
            if (filePath == null || !Directory.Exists(filePath))
            {
                Console.Error.WriteLine("Error: --index requires a valid directory as the first argument.");
                return 1;
            }
            try
            {
                return IndexCommand.Run(filePath);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }

        if (ContainsFlag(args, "--index-search"))
        {
            if (filePath == null || !Directory.Exists(filePath))
            {
                Console.Error.WriteLine("Error: --index-search requires a valid directory as the first argument.");
                return 1;
            }
            var (isField, isValue) = GetIndexSearchArgs(args);
            if (isField == null || isValue == null ||
                isField.StartsWith("--", StringComparison.Ordinal) ||
                isValue.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Error: --index-search requires a field name and value: --index-search <field> <value>");
                return 1;
            }
            try
            {
                return IndexSearchCommand.Run(filePath, isField, isValue);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }
```

**Change 2:** Add `GetIndexSearchArgs` helper after the existing `GetFindArgs` method (around line 268):

```csharp
    private static (string? field, string? value) GetIndexSearchArgs(string[] args)
    {
        int idx = Array.FindIndex(args, a => a.Equals("--index-search", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return (null, null);
        string? field = idx + 1 < args.Length ? args[idx + 1] : null;
        string? value = idx + 2 < args.Length ? args[idx + 2] : null;
        return (field, value);
    }
```

**Change 3:** Update `PrintUsage`, add these two lines to the Usage section:

```
              clipmetascribe "C:\clips\" --index
              clipmetascribe "C:\clips\" --index-search <field> <value>
```

And add to the Examples section:

```
              clipmetascribe "C:\clips\" --index
              clipmetascribe "C:\clips\" --index-search game "Team Fortress 2"
```

- [ ] **Step 6: Run the new tests**

```
dotnet test clipmetascribe.Tests --filter "IndexCommandTests|IndexSearchCommandTests"
```

Expected: all 11 tests PASS.

- [ ] **Step 7: Run all clipmetascribe tests**

```
dotnet test clipmetascribe.Tests
```

Expected: all tests pass (one pre-existing flaky test `Write_NoTempFileLeft_AfterSuccess` may occasionally fail, ignore it).

- [ ] **Step 8: Build the full solution**

```
dotnet build clipmetascribe.Tests
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 9: Commit**

```
git add clipmetascribe/Commands/IndexCommand.cs clipmetascribe/Commands/IndexSearchCommand.cs clipmetascribe/Program.cs clipmetascribe.Tests/IndexCommandTests.cs clipmetascribe.Tests/IndexSearchCommandTests.cs
git commit -m "feat: add --index and --index-search commands"
```

---

## Self-Review

**Spec coverage:**
- ✅ `ClipMetaIndex.Build`, scans directory, returns `IndexData`
- ✅ `ClipMetaIndex.Write`/`Read`, serialize/deserialize index to line-keyword format
- ✅ `ClipMetaIndex.WriteToFile`/`ReadFromFile`, convenience file-based wrappers
- ✅ `ClipMetaSearch.Find`, case-insensitive substring search over `IndexData`
- ✅ `IndexCommand`, `--index` CLI command, writes `.clipmeta-index` to directory
- ✅ `IndexSearchCommand`, `--index-search <field> <value>` CLI command
- ✅ Program.cs wiring, both flags handled before `File.Exists` check

**Placeholder scan:** No TBDs, no "add appropriate error handling" phrases, all code blocks complete.

**Type consistency:**
- `IndexEntry` defined in Task 1, used in Task 2 (`ClipMetaSearch.Find` return type) and Task 3 (`IndexSearchCommand`) ✓
- `IndexData` defined in Task 1, used in Task 2 and Task 3 ✓
- `ClipMetaIndex.IndexFileName` defined in Task 1, used in Task 3 tests ✓
- `ClipMetaIndex.Build`/`WriteToFile`/`ReadFromFile` defined in Task 1, used in Task 3 tests ✓
- `ClipMetaSearch.Find(IndexData, string, string)` defined in Task 2, called in `IndexSearchCommand.Run` in Task 3 ✓
