# export Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `--export` to clipmetascribe so a user can dump clipmeta metadata from one MP4 file or an entire directory to JSON or CSV.

**Architecture:** `ClipMetaExporter.GetRecords(IEnumerable<string> filePaths)` (in `clipmeta.core/Read/`) reads fields from each file and returns `IReadOnlyList<ExportRecord>`, excluding the internal `schema` field. `ExportCommand.Run(IReadOnlyList<ExportRecord>, string format, TextWriter?)` formats the records as JSON or CSV with hand-written serialization (no NuGet). Program.cs constructs the file list (single file or directory scan), opens an optional `StreamWriter` for `--output`, and calls `ClipMetaExporter.GetRecords` → `ExportCommand.Run`. Tests for `ExportCommand` use constructed `ExportRecord` values, no MP4 file I/O needed, making them fast unit tests.

**Tech Stack:** C# / .NET 10, MSTest 4, no external packages. Solution root: `C:\Users\srfin\Dropbox\Dev\repos\peckworks-clipmeta`.

---

## File Structure

| File | Action | Responsibility |
|------|--------|---------------|
| `clipmeta.core/Read/ClipMetaExporter.cs` | Create | `ExportRecord` record + `ClipMetaExporter.GetRecords`, gathers fields from MP4 files |
| `clipmetascribe/Commands/ExportCommand.cs` | Create | Formats `ExportRecord` list as JSON or CSV; writes to `TextWriter` |
| `clipmetascribe/Program.cs` | Modify | Wire `--export`, `--format`, `--output`; open StreamWriter if needed |
| `clipmetascribe.Tests/ClipMetaExporterTests.cs` | Create | Integration tests using real MP4 files |
| `clipmetascribe.Tests/ExportCommandTests.cs` | Create | Unit tests using constructed `ExportRecord`, no file I/O |

---

## Reference: Key Types You'll Use

**`ClipMetaReader.GetFields(BoxNode root)`** → `IReadOnlyList<(string Field, string Value)>`. Bare field names, unquoted values. In `clipmeta.core/Read/ClipMetaReader.cs`.

**`ClipMetaSchema.Schema`** = `"schema"`, the internal field written on every write. Must be excluded from export output.

**`ClipMetaSchema.AtomName(field)`** → `"com.peckworkslab.clipmeta:field"`. Used in tests when calling `Mp4Writer`.

**`ClipMetaSchema.Game/Players/Tags/Timecode/Rating/Notes`**, the six known field names. Used as fixed CSV column headers.

**`Mp4Parser.ParseFile(path)`**, can throw `IOException`, `UnauthorizedAccessException`, `InvalidDataException`. All three must be caught per-file with `continue`.

**`TestClipsLocator.AllPristine()`**, in `clipmetascribe.Tests/Helpers/TestClipsLocator.cs`. Returns paths to pristine `.mp4` files.

**`GetFlag(args, "--format")`**, already in Program.cs. Returns `args[idx+1]` if flag present with a following arg, else null.

**`GetFlag(args, "--output")`**, same helper for the output path.

---

## Task 1: ClipMetaExporter Core + Tests

**Files:**
- Create: `clipmeta.core/Read/ClipMetaExporter.cs`
- Create: `clipmetascribe.Tests/ClipMetaExporterTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/ClipMetaExporterTests.cs`:

```csharp
using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaExporterTests
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
    public void GetRecords_SingleFile_ReturnsOneRecord()
    {
        string path = PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var records = ClipMetaExporter.GetRecords(new[] { path });

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(path, records[0].FilePath);
    }

    [TestMethod]
    public void GetRecords_SingleFile_ContainsWrittenField()
    {
        string path = PrepareClip("clip.mp4", "game", "Team Fortress 2");

        var records = ClipMetaExporter.GetRecords(new[] { path });

        Assert.IsTrue(records[0].Fields.Any(f => f.Field == "game" && f.Value == "Team Fortress 2"));
    }

    [TestMethod]
    public void GetRecords_ExcludesSchemaField()
    {
        string path = PrepareClip("clip.mp4", "game", "TF2");

        var records = ClipMetaExporter.GetRecords(new[] { path });

        Assert.IsFalse(records[0].Fields.Any(f => f.Field.Equals("schema", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void GetRecords_PristineFile_ReturnsRecordWithNoFields()
    {
        string source = TestClipsLocator.AllPristine().First();

        var records = ClipMetaExporter.GetRecords(new[] { source });

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(0, records[0].Fields.Count);
    }

    [TestMethod]
    public void GetRecords_MultipleFiles_ReturnsAll()
    {
        string p1 = PrepareClip("clip1.mp4", "game", "TF2");
        string p2 = PrepareClip("clip2.mp4", "game", "CS2");

        var records = ClipMetaExporter.GetRecords(new[] { p1, p2 });

        Assert.AreEqual(2, records.Count);
    }

    [TestMethod]
    public void GetRecords_MalformedFile_IsSkipped()
    {
        string corrupt = Path.Combine(_tempDir, "corrupt.mp4");
        File.WriteAllBytes(corrupt, Array.Empty<byte>());

        var records = ClipMetaExporter.GetRecords(new[] { corrupt });

        Assert.AreEqual(0, records.Count);
    }

    [TestMethod]
    public void GetRecords_EmptyInput_ReturnsEmpty()
    {
        var records = ClipMetaExporter.GetRecords(Array.Empty<string>());

        Assert.AreEqual(0, records.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```
cd C:\Users\srfin\Dropbox\Dev\repos\peckworks-clipmeta
dotnet test clipmetascribe.Tests --filter "ClipMetaExporterTests" --verbosity minimal
```

Expected: build error, `ClipMetaExporter` does not exist.

- [ ] **Step 3: Implement ClipMetaExporter**

Create `clipmeta.core/Read/ClipMetaExporter.cs`:

```csharp
using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Read;

/// <summary>A metadata snapshot for a single MP4 file, ready for export.</summary>
public record ExportRecord(
    /// <summary>Full path to the MP4 file.</summary>
    string FilePath,
    /// <summary>All user-facing clipmeta fields. The internal schema field is excluded.</summary>
    IReadOnlyList<(string Field, string Value)> Fields);

/// <summary>Reads clipmeta metadata from a collection of MP4 files for bulk export.</summary>
public static class ClipMetaExporter
{
    /// <summary>
    /// Reads clipmeta fields from each path in <paramref name="filePaths"/> and returns one
    /// <see cref="ExportRecord"/> per successfully parsed file.
    /// The internal schema field is excluded. Malformed or unreadable files are silently skipped.
    /// </summary>
    /// <param name="filePaths">File paths to read. May be empty.</param>
    /// <returns>One record per file that was parsed successfully, in input order.</returns>
    public static IReadOnlyList<ExportRecord> GetRecords(IEnumerable<string> filePaths)
    {
        var records = new List<ExportRecord>();
        foreach (string path in filePaths)
        {
            BoxNode root;
            try { root = Mp4Parser.ParseFile(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            catch (InvalidDataException) { continue; }

            var fields = ClipMetaReader.GetFields(root)
                .Where(f => !f.Field.Equals(ClipMetaSchema.Schema, StringComparison.Ordinal))
                .ToList();
            records.Add(new ExportRecord(path, fields));
        }
        return records;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```
dotnet test clipmetascribe.Tests --filter "ClipMetaExporterTests" --verbosity minimal
```

Expected: 7 tests pass, 0 failures.

- [ ] **Step 5: Commit**

```
git add clipmeta.core/Read/ClipMetaExporter.cs clipmetascribe.Tests/ClipMetaExporterTests.cs
git commit -m "feat: add ClipMetaExporter.GetRecords for bulk MP4 metadata export"
```

---

## Task 2: ExportCommand + Program.cs + Tests

**Files:**
- Create: `clipmetascribe/Commands/ExportCommand.cs`
- Modify: `clipmetascribe/Program.cs`
- Create: `clipmetascribe.Tests/ExportCommandTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/ExportCommandTests.cs`:

```csharp
using ClipMetaCore.Read;
using ClipMetaScribe.Commands;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ExportCommandTests
{
    // Helper: build an ExportRecord without needing real MP4 files
    private static ExportRecord MakeRecord(string path, params (string Field, string Value)[] fields)
        => new ExportRecord(path, fields.ToList());

    // ── JSON ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Run_Json_OutputsBrackets()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "TF2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "json", writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "[");
        StringAssert.Contains(output, "]");
    }

    [TestMethod]
    public void Run_Json_ContainsFileKey()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "TF2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "json", writer);

        StringAssert.Contains(writer.ToString(), "\"file\":");
    }

    [TestMethod]
    public void Run_Json_ContainsFieldNameAndValue()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "Team Fortress 2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "json", writer);

        string output = writer.ToString();
        StringAssert.Contains(output, "\"game\":");
        StringAssert.Contains(output, "Team Fortress 2");
    }

    [TestMethod]
    public void Run_Json_EmptyRecords_OutputsEmptyArray()
    {
        using var writer = new StringWriter();

        ExportCommand.Run(new List<ExportRecord>(), "json", writer);

        string output = writer.ToString().Trim();
        Assert.IsTrue(output.StartsWith("["));
        Assert.IsTrue(output.EndsWith("]"));
    }

    [TestMethod]
    public void Run_Json_EscapesBackslashesInPath()
    {
        var records = new List<ExportRecord> { MakeRecord(@"C:\clips\clip.mp4") };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "json", writer);

        StringAssert.Contains(writer.ToString(), @"C:\\clips\\clip.mp4");
    }

    [TestMethod]
    public void Run_Json_ReturnsZero()
    {
        using var writer = new StringWriter();

        int exitCode = ExportCommand.Run(new List<ExportRecord>(), "json", writer);

        Assert.AreEqual(0, exitCode);
    }

    // ── CSV ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Run_Csv_FirstLineIsHeader()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "TF2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "csv", writer);

        string firstLine = writer.ToString().Split(Environment.NewLine)[0];
        Assert.IsTrue(firstLine.StartsWith("file,"), $"First line was: {firstLine}");
        StringAssert.Contains(firstLine, "game");
    }

    [TestMethod]
    public void Run_Csv_HeaderContainsAllKnownFields()
    {
        using var writer = new StringWriter();

        ExportCommand.Run(new List<ExportRecord>(), "csv", writer);

        string firstLine = writer.ToString().Split(Environment.NewLine)[0];
        foreach (string field in new[] { "game", "players", "tags", "timecode", "rating", "notes" })
            StringAssert.Contains(firstLine, field);
    }

    [TestMethod]
    public void Run_Csv_DataRowContainsFilePath()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "TF2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "csv", writer);

        string[] lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        StringAssert.Contains(lines[1], "clip.mp4");
    }

    [TestMethod]
    public void Run_Csv_DataRowContainsFieldValue()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("game", "Team Fortress 2")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "csv", writer);

        StringAssert.Contains(writer.ToString(), "Team Fortress 2");
    }

    [TestMethod]
    public void Run_Csv_EmptyRecords_OutputsHeaderOnly()
    {
        using var writer = new StringWriter();

        ExportCommand.Run(new List<ExportRecord>(), "csv", writer);

        string[] lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(1, lines.Length);
        Assert.IsTrue(lines[0].StartsWith("file,"));
    }

    [TestMethod]
    public void Run_Csv_QuotesValuesContainingCommas()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4", ("notes", "hello, world")) };
        using var writer = new StringWriter();

        ExportCommand.Run(records, "csv", writer);

        StringAssert.Contains(writer.ToString(), "\"hello, world\"");
    }

    [TestMethod]
    public void Run_Csv_ReturnsZero()
    {
        using var writer = new StringWriter();

        int exitCode = ExportCommand.Run(new List<ExportRecord>(), "csv", writer);

        Assert.AreEqual(0, exitCode);
    }

    // ── Format validation ────────────────────────────────────────────────────

    [TestMethod]
    public void Run_UnknownFormat_ReturnsOne()
    {
        using var writer = new StringWriter();

        int exitCode = ExportCommand.Run(new List<ExportRecord>(), "xml", writer);

        Assert.AreEqual(1, exitCode);
    }

    // ── Default output ───────────────────────────────────────────────────────

    [TestMethod]
    public void Run_DefaultOutput_UsesConsoleOut()
    {
        var records = new List<ExportRecord> { MakeRecord("clip.mp4") };
        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);

            int exitCode = ExportCommand.Run(records, "json");

            Assert.AreEqual(0, exitCode);
            StringAssert.Contains(writer.ToString(), "[");
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
dotnet test clipmetascribe.Tests --filter "ExportCommandTests" --verbosity minimal
```

Expected: build error, `ExportCommand` does not exist.

- [ ] **Step 3: Implement ExportCommand**

Create `clipmetascribe/Commands/ExportCommand.cs`:

```csharp
using System.Text;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;

namespace ClipMetaScribe.Commands;

/// <summary>Exports clipmeta metadata records to JSON or CSV format.</summary>
internal static class ExportCommand
{
    private static readonly string[] KnownFields =
    [
        ClipMetaSchema.Game, ClipMetaSchema.Players, ClipMetaSchema.Tags,
        ClipMetaSchema.Timecode, ClipMetaSchema.Rating, ClipMetaSchema.Notes,
    ];

    /// <summary>
    /// Formats <paramref name="records"/> as JSON or CSV and writes to <paramref name="output"/>
    /// (defaults to <see cref="Console.Out"/>).
    /// </summary>
    /// <param name="records">Records to export.</param>
    /// <param name="format">"json" or "csv" (case-insensitive).</param>
    /// <param name="output">Output writer; defaults to <see cref="Console.Out"/> when null.</param>
    /// <returns>0 on success, 1 if format is unrecognized.</returns>
    internal static int Run(IReadOnlyList<ExportRecord> records, string format, TextWriter? output = null)
    {
        output ??= Console.Out;

        return format.ToLowerInvariant() switch
        {
            "json" => WriteJson(records, output),
            "csv"  => WriteCsv(records, output),
            _      => WriteFormatError(format),
        };
    }

    private static int WriteJson(IReadOnlyList<ExportRecord> records, TextWriter output)
    {
        output.WriteLine("[");
        for (int i = 0; i < records.Count; i++)
        {
            output.WriteLine("  {");
            var r = records[i];
            var pairs = new List<string> { $"\"file\": \"{JsonEscape(r.FilePath)}\"" };
            pairs.AddRange(r.Fields.Select(f => $"\"{JsonEscape(f.Field)}\": \"{JsonEscape(f.Value)}\""));

            for (int j = 0; j < pairs.Count; j++)
                output.WriteLine($"    {pairs[j]}{(j < pairs.Count - 1 ? "," : "")}");

            output.WriteLine($"  }}{(i < records.Count - 1 ? "," : "")}");
        }
        output.WriteLine("]");
        return 0;
    }

    private static int WriteCsv(IReadOnlyList<ExportRecord> records, TextWriter output)
    {
        var customFields = records
            .SelectMany(r => r.Fields.Select(f => f.Field))
            .Where(f => !KnownFields.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var columns = new[] { "file" }.Concat(KnownFields).Concat(customFields).ToList();
        output.WriteLine(string.Join(",", columns.Select(CsvEscape)));

        foreach (var r in records)
        {
            var fieldMap = r.Fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.OrdinalIgnoreCase);
            var row = columns.Select(col => col == "file" ? r.FilePath : fieldMap.GetValueOrDefault(col, ""));
            output.WriteLine(string.Join(",", row.Select(CsvEscape)));
        }
        return 0;
    }

    private static int WriteFormatError(string format)
    {
        Console.Error.WriteLine($"Error: Unknown format '{format}'. Use 'json' or 'csv'.");
        return 1;
    }

    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string CsvEscape(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }
}
```

- [ ] **Step 4: Wire --export in Program.cs**

Read `clipmetascribe/Program.cs` to confirm current content, then make two changes.

**Change A, add `--export` block.**

Find this text in Program.cs (it is the `--vocab` closing brace followed by the `File.Exists` check):
```csharp
        }

        if (filePath == null || !File.Exists(filePath))
        {
            if (filePath != null && Path.HasExtension(filePath))
```

Replace with:
```csharp
        }

        if (ContainsFlag(args, "--export"))
        {
            if (filePath == null)
            {
                Console.Error.WriteLine("Error: --export requires a file or directory path as the first argument.");
                return 1;
            }
            string exportFormat = GetFlag(args, "--format") ?? "json";
            if (exportFormat.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Error: --format requires a value: --format json|csv");
                return 1;
            }
            string? outputPath = GetFlag(args, "--output");
            if (outputPath?.StartsWith("--", StringComparison.Ordinal) == true)
            {
                Console.Error.WriteLine("Error: --output requires a file path.");
                return 1;
            }

            IEnumerable<string> exportPaths;
            if (Directory.Exists(filePath))
                exportPaths = Directory.EnumerateFiles(filePath, "*.mp4", SearchOption.AllDirectories);
            else if (File.Exists(filePath))
                exportPaths = new[] { filePath };
            else
            {
                Console.Error.WriteLine($"Error: Path not found: {filePath}");
                return 1;
            }

            StreamWriter? fileWriter = null;
            try
            {
                TextWriter exportOutput = Console.Out;
                if (outputPath != null)
                {
                    fileWriter = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
                    exportOutput = fileWriter;
                }
                var records = ClipMetaExporter.GetRecords(exportPaths);
                return ExportCommand.Run(records, exportFormat, exportOutput);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
            finally
            {
                fileWriter?.Dispose();
            }
        }

        if (filePath == null || !File.Exists(filePath))
        {
            if (filePath != null && Path.HasExtension(filePath))
```

**Change B, update PrintUsage.** Replace the entire `Console.WriteLine("""...""");` block with:

```csharp
        Console.WriteLine("""
            clipmetascribe, MP4 metadata writer (Peckworks Lab)

            Usage:
              clipmetascribe "clip.mp4" --list
              clipmetascribe "clip.mp4" --stats
              clipmetascribe "clip.mp4" --export [--format json|csv] [--output <path>]
              clipmetascribe "clip.mp4" --set <field> <value>
              clipmetascribe "clip.mp4" --append <field> <value>
              clipmetascribe "clip.mp4" --clear <field>
              clipmetascribe "clip.mp4" --clear-all [--yes]
              clipmetascribe "C:\clips\" --find <field> <value>
              clipmetascribe "C:\clips\" --vocab <field>
              clipmetascribe "C:\clips\" --export [--format json|csv] [--output <path>]

            Fields:  game  players  tags  timecode  rating  notes  (or any custom name)

            Examples:
              clipmetascribe "clip.mp4" --list
              clipmetascribe "clip.mp4" --stats
              clipmetascribe "clip.mp4" --export
              clipmetascribe "clip.mp4" --export --format csv
              clipmetascribe "clip.mp4" --export --format json --output metadata.json
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
              clipmetascribe "C:\clips\" --export --format csv --output library.csv

            Options:
              --dry-run         Preview changes without writing
              --backup          Keep .bak copy of original before write
              --verbose         Verbose logging (requires --log)
              --log <path>      Write structured log to file
              --yes             Skip confirmation prompts
              --version         Print version and exit
              --format json|csv Export format (default: json). Use with --export.
              --output <path>   Write export to file instead of stdout. Use with --export.

            Exit codes:  0=success  1=bad args / not found  2=write failure  3=verification failure
            """);
```

**Change C, add `using ClipMetaCore.Read;` if not already present.**

Check the top of Program.cs. It currently imports: `ClipMetaCore`, `ClipMetaCore.Abstractions`, `ClipMetaCore.Logging`, `ClipMetaCore.Schema`, `ClipMetaCore.Write`, `ClipMetaScribe.Commands`. Add `using ClipMetaCore.Read;` after the existing using directives.

- [ ] **Step 5: Run ExportCommandTests to verify they pass**

```
dotnet test clipmetascribe.Tests --filter "ExportCommandTests" --verbosity minimal
```

Expected: 14 tests pass, 0 failures.

- [ ] **Step 6: Run full test suite**

```
dotnet test --verbosity minimal
```

Expected: all tests pass (previous 231 + 7 + 14 = 252 total), 0 failures.

- [ ] **Step 7: Commit**

```
git add clipmetascribe/Commands/ExportCommand.cs clipmetascribe/Program.cs clipmetascribe.Tests/ExportCommandTests.cs
git commit -m "feat: add --export command for JSON and CSV metadata dump"
```

---

## Self-Review

**Spec coverage:**
- ✅ `--export` on single file, Program.cs `File.Exists` branch → `new[] { filePath }`
- ✅ `--export` on directory, Program.cs `Directory.Exists` branch → `Directory.EnumerateFiles`
- ✅ JSON output, `WriteJson` with hand-written serialization
- ✅ CSV output, `WriteCsv` with known-field columns + custom fields appended
- ✅ `--format json|csv`, `GetFlag(args, "--format") ?? "json"`; `--flag` guard
- ✅ `--output <path>`, `StreamWriter` opened in Program.cs; disposed in `finally`
- ✅ Internal `schema` field excluded, `ClipMetaExporter.GetRecords` filters it
- ✅ Malformed files skipped, three-exception catch in `GetRecords`
- ✅ Pristine file with no fields, includes record with empty Fields list
- ✅ JSON escaping, `JsonEscape` handles `"`, `\`, `\n`, `\r`, `\t`, control chars
- ✅ CSV escaping, `CsvEscape` wraps in quotes when `,`, `"`, newline present
- ✅ CSV header always contains all six known fields, `KnownFields` array in `ExportCommand`
- ✅ Unknown format returns exit code 1, `WriteFormatError`
- ✅ IOException handling at Program.cs level, wraps `ExportCommand.Run` call
- ✅ PrintUsage updated with `--export`, `--format`, `--output`

**Placeholder scan:** No TBD, TODO, "similar to Task N", or vague steps. All code is complete.

**Type consistency:**
- `ExportRecord(string FilePath, IReadOnlyList<(string Field, string Value)> Fields)`, defined in Task 1, used in Task 2 `ExportCommand.Run` signature and test `MakeRecord` helper. Consistent.
- `ClipMetaExporter.GetRecords(IEnumerable<string>)` → `IReadOnlyList<ExportRecord>`, defined in Task 1, called in Program.cs in Task 2. Consistent.
- `ExportCommand.Run(IReadOnlyList<ExportRecord>, string, TextWriter?)` → `int`, defined and tested in Task 2. Consistent.
- `KnownFields` in `ExportCommand` matches `ClipMetaSchema.Game/Players/Tags/Timecode/Rating/Notes`, same 6 fields used in `StatsCommand`. Consistent.
