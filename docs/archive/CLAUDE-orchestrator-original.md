# CLAUDE.md — clipmetaview Orchestration Instructions

## Project Overview

You are building **clipmetaview**, a C# command-line application that reads an `.mp4` file and displays its internal box/atom structure as a human-readable tree. This is the first deliverable in the **peckworks-clipmeta** suite.

**Solution root:** `C:\Users\srfin\Dropbox\Dev\repos\peckworks-clipmeta`
**CLI project name:** `clipmetaview`
**Target framework:** .NET 8 (or whatever is present in the stub .csproj — do not downgrade)
**External packages:** NONE. Zero. Only types from the Microsoft base SDK and BCL. No NuGet packages beyond what ships with the SDK.
**Test clips:** `C:\Users\srfin\Dropbox\Dev\repos\peckworks-clipmeta\testclips\` — real `.mp4` files are available here for integration testing and manual validation. Always use files from this folder when you need a real MP4. Do not hardcode any specific filename; enumerate the folder at test time so new clips added later are picked up automatically.

---

## Orchestrator Pattern — How You Will Work

You are the **Orchestrator**. You will coordinate five roles in sequence. Each role is a focused pass over the work. Do not blend roles. Complete each one before starting the next.

```
ORCHESTRATOR
├── ROLE 1 — ARCHITECT     (design decisions, file layout, type definitions)
├── ROLE 2 — CODER         (write all implementation files)
├── ROLE 3 — TESTER        (write xUnit-free test validation, use MSTest)
├── ROLE 4 — REVIEWER      (read every file you wrote, self-critique, fix issues)
└── ROLE 5 — DOCUMENTER    (XML doc comments, README.md, usage examples)
```

Announce each role transition with a header line, e.g.:
```
=== ROLE 2: CODER — beginning implementation ===
```

---

## Role 1 — ARCHITECT

Before writing any code, produce a short design document in your response covering:

1. **File layout** — list every `.cs` file you will create, one sentence on its purpose.
2. **Public API surface** — the key types and their responsibilities.
3. **Tree rendering strategy** — how you will produce the ASCII tree output.
4. **Error handling contract** — what happens on bad files, missing files, non-mp4 input.
5. **Extension seams** — how this codebase will be extended later by `clipmetaedit` (the write/edit tool). Make types easy to reuse.

Do not write any `.cs` files during this role.

---

## Role 2 — CODER

Implement the application by creating or editing files inside the `clipmetaview` project folder. Follow every constraint below precisely.

### Absolute Technical Constraints

- **No external NuGet packages.** If you find yourself wanting a library, implement the functionality yourself. The MP4 format document below gives you everything you need.
- **No `System.Drawing`, `SkiaSharp`, or any imaging library.** This is a terminal text tool only.
- **Big-endian reading is required everywhere.** MP4 files are big-endian; .NET on Windows is little-endian. All multi-byte reads must reverse bytes.
- **Do not load the entire file into memory.** Use `FileStream` with seeking. Read only what you need.
- **Use `using` declarations and `BinaryReader` for all binary reading.**
- **Target `async`-capable entry point** (`Task<int> Main`) so the tool can be extended later.
- **Exit codes:** 0 = success, 1 = file not found / bad args, 2 = parse error.

### Required Files to Create

#### `Program.cs`
Entry point. Parse args. Validate the file exists and has `.mp4` extension. Call the parser. Call the renderer. Handle exceptions and set exit codes.

#### `Mp4/BoxHeader.cs`
```
namespace ClipMetaView.Mp4;

public readonly record struct BoxHeader(
    ulong Size,
    string Type,
    int HeaderSize  // 8 for normal, 16 for extended-size
);
```

#### `Mp4/FullBoxHeader.cs`
```
namespace ClipMetaView.Mp4;

public readonly record struct FullBoxHeader(
    BoxHeader Box,
    byte Version,
    uint Flags
);
```

#### `Mp4/BigEndianReader.cs`
Static utility. Must implement at minimum:
- `ReadUInt16(BinaryReader)`
- `ReadUInt32(BinaryReader)`
- `ReadUInt64(BinaryReader)`
- `ReadFourCC(BinaryReader)` — reads 4 bytes, returns ASCII string
- `ReadBoxHeader(BinaryReader)` — handles size==1 (extended) and size==0 (to-EOF) cases
- `ReadFullBoxHeader(BinaryReader)` — reads box header + 1 version byte + 3 flag bytes

#### `Mp4/BoxNode.cs`
An in-memory tree node representing a parsed box:
```
public class BoxNode
{
    public string Type { get; init; }
    public ulong Size { get; init; }
    public long FileOffset { get; init; }
    public int HeaderSize { get; init; }
    public bool IsFullBox { get; init; }
    public byte Version { get; init; }
    public uint Flags { get; init; }
    public List<BoxNode> Children { get; init; } = new();

    // Metadata extracted from leaf nodes (for display)
    public string? DisplayValue { get; set; }
    public bool IsEditable { get; set; }  // marks udta/meta/ilst leaf nodes
    public string? EditableKey { get; set; }
}
```

#### `Mp4/Mp4Parser.cs`
Core parser. Must implement:
- `ParseFile(string path) : BoxNode` — returns root node whose children are the top-level boxes
- `ParseContainerBox(BinaryReader, BoxHeader, long fileOffset) : BoxNode`
- `ParseBoxes(BinaryReader, long start, long end) : IEnumerable<BoxNode>`
- Recursion into all known container types (see list below)
- Recognition of FullBox types (meta, mvhd, tkhd, etc.)
- Extraction of display values for known metadata leaf boxes (©nam, ©ART, ©alb, ©day, ©cmt, ©gen, trkn, desc, covr)
- Marking editable nodes (anything inside `ilst`) with `IsEditable = true`

**Known container box types (recurse into these):**
`moov`, `trak`, `mdia`, `minf`, `stbl`, `udta`, `ilst`, `edts`, `dinf`, `moof`, `traf`

**Known FullBox types (consume extra 4 bytes after header):**
`meta`, `mvhd`, `tkhd`, `mdhd`, `hdlr`, `stsd`, `stts`, `stsc`, `stsz`, `stco`, `co64`, `elst`, `dref`, `smhd`, `vmhd`, `nmhd`

**Special metadata handling:**
- `©nam` (0xA9 6E 61 6D) = Title → mark `IsEditable = true`, extract UTF-8 value from inner `data` box
- `©ART` = Artist
- `©alb` = Album
- `©day` = Year
- `©cmt` = Comment
- `©gen` = Genre
- `trkn` = Track Number (integer)
- `desc` = Description
- `covr` = Cover Art (binary — display as `[JPEG image, N bytes]` or `[PNG image, N bytes]`)
- Any unknown box inside `ilst` → mark `IsEditable = true`, display as `[unknown key]`

**Data box parsing (inside ilst items):**
The `data` child box has structure:
- 1 byte version
- 3 bytes type indicator (1=UTF-8, 13=JPEG, 14=PNG, 21=signed int, 22=unsigned int)
- 4 bytes locale
- remaining bytes = value

#### `Mp4/MetadataKeys.cs`
A static class with a `Dictionary<string, string>` mapping FourCC → friendly name:
```
©nam → "Title"
©ART → "Artist"
©alb → "Album"
©day → "Year"
©cmt → "Comment"
©gen → "Genre"
trkn → "Track Number"
desc → "Description"
covr → "Cover Art"
moov → "Movie"
trak → "Track"
mdat → "Media Data"
ftyp → "File Type"
udta → "User Data"
meta → "Metadata"
ilst → "Metadata Items"
... (fill in reasonable names for all common FourCCs)
```

#### `Rendering/TreeRenderer.cs`
Renders a `BoxNode` tree to the console using box-drawing characters. Must:
- Use `├──`, `└──`, `│` characters for the tree branches
- Show: FourCC type, friendly name (from MetadataKeys), size in bytes, file offset
- For editable nodes, append `  ← [EDITABLE]` in a contrasting color (use `Console.ForegroundColor`)
- For nodes with a `DisplayValue`, show the value on the same line: `©nam  Title  "My Video Title"  ← [EDITABLE]`
- For `covr`, show `[JPEG image, 14532 bytes]` instead of the raw value
- For `mdat`, do NOT recurse — just show it as a leaf with its size (it is raw media bytes)
- Emit a legend at the bottom explaining the `[EDITABLE]` marker and what it means
- Reset console color to default before exiting

**Example output format:**
```
myvideo.mp4  (14.2 MB)
├── ftyp  File Type  [32 bytes @ 0x0]
├── moov  Movie  [45231 bytes @ 0x20]
│   ├── mvhd  Movie Header  [108 bytes @ 0x28]
│   ├── trak  Track  [23451 bytes @ 0x94]  (video)
│   │   ├── tkhd  Track Header  [92 bytes @ 0x9C]
│   │   └── mdia  Media  [...]
│   │       └── ...
│   └── udta  User Data  [1234 bytes @ 0xB123]
│       └── meta  Metadata  [1000 bytes @ 0xB12B]
│           ├── hdlr  Handler  [45 bytes]
│           └── ilst  Metadata Items  [900 bytes]
│               ├── ©nam  Title  "My Vacation 2024"  ← [EDITABLE]
│               ├── ©ART  Artist  "Scott Finley"  ← [EDITABLE]
│               └── desc  Description  ""  ← [EDITABLE]
└── mdat  Media Data  [12398012 bytes @ 0xB4AF]  (raw media, not expanded)

Legend:
  ← [EDITABLE]  This field can be added, updated, or deleted with clipmetaedit (coming soon)

```

---

## Role 3 — TESTER

Create a test project `clipmetaview.Tests` inside the solution using MSTest (the Microsoft testing framework — this ships with the SDK, no external packages needed).

### Test Coverage Required

1. **`BigEndianReaderTests`**
   - Reading `uint` from big-endian bytes returns correct value
   - Reading `ulong` from big-endian bytes returns correct value
   - Reading `ushort` from big-endian bytes returns correct value
   - FourCC bytes → string round-trips correctly
   - Size==1 triggers extended 64-bit read
   - Size==0 computes size from stream length

2. **`BoxNodeTests`**
   - A node with no children reports correct type and size
   - IsEditable defaults to false
   - Children collection is mutable and traversable

3. **`MetadataKeysTests`**
   - Known FourCCs return friendly names
   - Unknown FourCCs return the raw FourCC (or a default)

4. **`TreeRendererTests`** (use `Console.Out` redirect via `StringWriter`)
   - A single-node tree renders with correct branch character
   - An editable node includes `[EDITABLE]` in output
   - A node with DisplayValue shows the value in output
   - Nested children render with correct indentation

5. **`ProgramIntegrationTests`** — backed by real files from the `testclips` folder
   - Missing file → exit code 1
   - Wrong extension → exit code 1
   - Each `.mp4` in `testclips\` parses without throwing an exception
   - Each `.mp4` in `testclips\` produces a root node with at least one child box
   - Each `.mp4` in `testclips\` contains a `moov` box at the top level
   - Any file in `testclips\` that has an `ilst` box has at least one child node marked `IsEditable = true`

   **Test clip path helper — add this to the test project:**
   ```csharp
   internal static class TestClips
   {
       /// <summary>
       /// Returns all .mp4 files in the solution-level testclips folder.
       /// Tests that call this will naturally pick up any new clips added later.
       /// </summary>
       public static IEnumerable<string> All()
       {
           // Walk up from the test assembly's bin folder to the solution root
           var dir = new DirectoryInfo(AppContext.BaseDirectory);
           while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "testclips")))
               dir = dir.Parent;

           string clipsPath = dir != null
               ? Path.Combine(dir.FullName, "testclips")
               : throw new DirectoryNotFoundException("testclips folder not found");

           return Directory.EnumerateFiles(clipsPath, "*.mp4");
       }
   }
   ```

   Use `[DynamicData]` or a `[DataTestMethod]` with a data source method to run each clip through the parser:
   ```csharp
   [TestClass]
   public class ProgramIntegrationTests
   {
       public static IEnumerable<object[]> TestClipPaths()
           => TestClips.All().Select(p => new object[] { p });

       [DataTestMethod]
       [DynamicData(nameof(TestClipPaths), DynamicDataSourceType.Method)]
       public void ParseFile_RealClip_DoesNotThrow(string clipPath)
       {
           var root = Mp4Parser.ParseFile(clipPath);
           Assert.IsNotNull(root);
           Assert.IsTrue(root.Children.Count > 0, $"Expected boxes in {clipPath}");
       }
   }
   ```

**Test class template:**
```csharp
[TestClass]
public class BigEndianReaderTests
{
    [TestMethod]
    public void ReadUInt32_BigEndianBytes_ReturnsCorrectValue()
    {
        // Arrange
        byte[] bytes = { 0x00, 0x00, 0x00, 0x20 }; // big-endian 32
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        // Act
        uint result = BigEndianReader.ReadUInt32(reader);

        // Assert
        Assert.AreEqual(32u, result);
    }
}
```

---

## Role 4 — REVIEWER

Read every file you have written. Check each item on this list and fix any violations:

### Code Quality Checklist
- [ ] No external NuGet packages referenced in any `.csproj`
- [ ] All multi-byte reads go through `BigEndianReader` — no raw `BinaryReader.ReadInt32()` calls in parser code
- [ ] `FileStream` is properly disposed in all paths (use `using`)
- [ ] Exit codes are set correctly (0/1/2)
- [ ] No `Console.WriteLine` calls inside library classes (only in `Program.cs` and `TreeRenderer.cs`)
- [ ] `mdat` is NOT recursed into — it must be treated as a leaf
- [ ] Console colors are reset after rendering (use `try/finally`)
- [ ] `BoxNode.Children` is never null — initialized to `new List<BoxNode>()`
- [ ] The `©` prefix (0xA9) is handled correctly — FourCC parsing must handle non-ASCII bytes
- [ ] FullBox 4-byte skip is applied correctly to `meta` and all other FullBox types
- [ ] When `size == 0`, the box correctly extends to end of file/container
- [ ] Parser does not crash on files where `udta`/`meta`/`ilst` are absent (graceful no-metadata display)
- [ ] All public types have XML doc comments
- [ ] No magic numbers — all constants are named

### Self-Critique Format
After reviewing, produce a short list:
```
ISSUES FOUND AND FIXED:
1. [description of issue] → [how you fixed it]
2. ...

NO ISSUES FOUND IN:
- [list of files that were clean]
```

---

## Role 5 — DOCUMENTER

### XML Doc Comments
Every `public` type, method, and property must have a `<summary>` doc comment. For complex methods, add `<param>` and `<returns>` tags.

### README.md
Create `README.md` in the `clipmetaview` project folder. Include:

```markdown
# clipmetaview

Part of the **peckworks-clipmeta** suite.

## What it does
Displays the internal box/atom structure of an MP4 file as a tree. Editable metadata fields are clearly marked.

## Usage
    clipmetaview "path/to/video.mp4"

## Output
[paste an example tree here]

## Exit Codes
| Code | Meaning |
|------|---------|
| 0    | Success |
| 1    | Invalid arguments or file not found |
| 2    | File parse error |

## Technical Notes
- Pure native C# — zero external dependencies
- Reads only box headers; never loads media data (mdat) into memory
- Big-endian byte order handled throughout
- Designed as the foundation for clipmetaedit (add/update/delete metadata)

## MP4 Metadata You Can Edit (coming in clipmetaedit)
| Key   | Friendly Name | Type   |
|-------|--------------|--------|
| ©nam  | Title        | Text   |
| ©ART  | Artist       | Text   |
| ©alb  | Album        | Text   |
| ©day  | Year         | Text   |
| ©cmt  | Comment      | Text   |
| ©gen  | Genre        | Text   |
| desc  | Description  | Text   |
| covr  | Cover Art    | Binary |
| trkn  | Track Number | Integer|
```

---

## MP4 Format Reference (distilled for implementation)

### Box Structure
Every box:
```
[4 bytes: size (big-endian uint32)]
[4 bytes: FourCC type (ASCII)]
[if size==1: 8 bytes extended size (big-endian uint64)]
[payload / child boxes]
```

Special size values:
- `size == 1` → real size in next 8 bytes (extended)
- `size == 0` → box runs to end of file

### FullBox (extra header bytes after the 8-byte box header)
```
[1 byte: version]
[3 bytes: flags]
```
Types that are FullBoxes: `meta`, `mvhd`, `tkhd`, `mdhd`, `hdlr`, `stsd`, `stts`, `stsc`, `stsz`, `stco`, `co64`, `elst`, `dref`, `smhd`, `vmhd`, `nmhd`

### Metadata Path
```
moov → udta → meta (FullBox) → ilst → [©nam, ©ART, ...] → data (FullBox-like)
```

### data Box Payload Structure
```
[1 byte: version = 0]
[3 bytes: type indicator]  1=UTF-8, 13=JPEG, 14=PNG, 21=signed int, 22=unsigned int
[4 bytes: locale = 0]
[remaining: the actual value]
```

### The © Prefix
The `©` character is byte `0xA9` (169). `©nam` is bytes `A9 6E 61 6D`. When reading FourCCs, use `Encoding.Latin1` (ISO-8859-1) not ASCII to preserve this byte. When comparing to known keys, compare the raw string — `"\u00A9nam"` equals the parsed string.

### Big-Endian Requirement
Every multi-byte integer in MP4 is big-endian. Use this pattern:
```csharp
var bytes = reader.ReadBytes(4);
if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
return BitConverter.ToUInt32(bytes, 0);
```

---

## Extension Seams for clipmetaedit

Design with these future needs in mind (do not implement them now, just do not block them):

1. `BoxNode.FileOffset` must be accurate — the editor will seek to these positions.
2. `BoxNode.Size` must include the header — the editor needs to know the full span of each box.
3. `Mp4Parser` should be in a separate project/assembly (`ClipMetaView.Core`) if that structure exists, so the editor can reference it.
4. `IsEditable` and `EditableKey` on `BoxNode` are the hooks the editor will use to find targetable fields.
5. Avoid `sealed` on any class the editor might subclass.

---

## Definition of Done

The orchestrator considers the task complete when:

1. `dotnet build` succeeds with zero errors and zero warnings in the `clipmetaview` project.
2. `dotnet test` passes all tests in `clipmetaview.Tests`, including all integration tests against files in `testclips\`.
3. Running `clipmetaview` against each file in `testclips\` produces a readable tree with editable fields marked and no exceptions.
4. Running `clipmetaview "missing.mp4"` exits with code 1 and a useful error message.
5. Running `clipmetaview` with no arguments exits with code 1 and prints usage instructions.
6. `README.md` exists in the project folder.
7. All public types have XML doc comments.
8. Zero NuGet packages are referenced beyond what the SDK provides.
