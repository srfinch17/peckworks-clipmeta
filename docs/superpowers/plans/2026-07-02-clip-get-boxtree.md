# clip_get_boxtree Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the parsed MP4 box tree as structured data through a new read-only MCP tool `clip_get_boxtree` and CLI flags `clipmetaview --json` / `--definitions`, moving the tree renderer into Core so nothing is CLI-exclusive.

**Architecture:** The parser (`Mp4Parser.ParseFile`) and its `BoxNode` tree already exist and are shared by all three executables (see `docs/architecture-audit.md`). This feature is a wire-up: a pure DTO + mapper over `BoxNode`, one shared JSON serializer, a static box-definitions table, and the renderer relocated from `clipmetaview` into `clipmeta.core`. No new parsing, no writes.

**Tech Stack:** .NET 10, C#, `System.Text.Json` (BCL). MSTest for tests. Solution: `peckworks-clipmeta.slnx`.

## Global Constraints

Every task's requirements implicitly include this section. Values are copied verbatim from the approved spec (`docs/superpowers/specs/2026-07-02-boxtree-tool-and-cli-json-design.md`) and `CLAUDE.md`.

- **.NET 10; zero external NuGet packages** in production projects (`clipmeta.core`, `clipmetaview`, `clipmetamcp`). `System.Text.Json` is BCL and allowed. Test projects may use MSTest only.
- **No em-dash character (U+2014) anywhere** in docs, code, comments, string literals, or commit messages. Use commas, colons, periods, parentheses, or "to" for ranges.
- **CLIs are thin shells.** All shaping logic lives in `clipmeta.core`; `AppRunner` only parses args and delegates.
- **The write engine is NOT touched.** Do not modify `Mp4Writer.DetectNonCanonicalMetadata` or its firing set, and do not reroute `ClipMetaReader.CollectFromNode`'s predicate. The new shared helper shares only the *intrinsic* `----`+domain check and is called by the mapper.
- **`contentOffset` is NOT exposed** (it is wrong for `data`/`mean`/`name` value atoms). Expose `offset`, `size`, `headerSize` only.
- **`displayValue` is unquoted** in the DTO; **`category` is editable-aware** (`IsEditable ? EditableMeta : GetCategory(type)`).
- **One shared `JsonSerializerOptions`** in Core: `PropertyNamingPolicy = CamelCase`, `Converters = { new JsonStringEnumConverter() }`, `DefaultIgnoreCondition = WhenWritingNull`, `WriteIndented = false` (compact). CLI `--json` and the MCP `json` output must be byte-identical, achieved by routing both through the same `SerializeToNode(dto, Options).ToJsonString()` path.
- **ASCII parity:** `clipmetaview`'s non-color output (tree + summary) is unchanged after the renderer move, except for the deliberate invariant-culture number normalization (which only changes bytes on non-en-US locales). The renderer's `Console.ResetColor()` becomes side-effect-free when not writing to the console.
- **New MCP tool registration order:** `clip_get_boxtree` registers at the END of the read-tool block in `ReadTools.RegisterAll`, immediately after `library_watching`, before the write tools. The surface test `ToolsList_ContainsTheFullToolSurface` goes 17 -> 18 at that exact position.
- **Surface change rule:** after changing the MCP tool set or a CLI command surface, run the FULL relevant test project (`clipmetamcp.Tests`, `clipmetaview.Tests`), never a `--filter`.
- **Build/test:** `dotnet build --nologo -v q` (0 warnings, 0 errors); `dotnet test --nologo --no-build -v q` in the foreground with a long timeout (~10 min). XML doc comments on all public types/methods; named constants, no magic numbers.

---

### Task 1: Intrinsic clipmeta-atom predicate

**Files:**
- Modify: `clipmeta.core/Schema/ClipMetaSchema.cs` (add a const and a method near the existing `Domain` const at line 7)
- Test: `clipmetascribe.Tests/ClipMetaSchemaTests.cs` (create if absent; otherwise add to the existing schema test file)

**Interfaces:**
- Consumes: `ClipMetaCore.Mp4.BoxNode`, `ClipMetaSchema.Domain` (`"com.peckworkslab.clipmeta"`).
- Produces: `bool ClipMetaSchema.IsClipmetaFreeformAtom(BoxNode node)` and `const string ClipMetaSchema.DomainFieldPrefix`. Used by `BoxTreeMapper` (Task 3).

- [ ] **Step 1: Write the failing test**

Create `clipmetascribe.Tests/ClipMetaSchemaTests.cs` (if the class already exists, add these methods to it):

```csharp
using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaSchemaTests
{
    [TestMethod]
    public void IsClipmetaFreeformAtom_TrueForDomainPrefixedFreeformAtom()
    {
        var node = new BoxNode { Type = "----", EditableKey = ClipMetaSchema.Domain + ":game" };
        Assert.IsTrue(ClipMetaSchema.IsClipmetaFreeformAtom(node));
    }

    [TestMethod]
    public void IsClipmetaFreeformAtom_FalseForForeignFreeformAtom()
    {
        var node = new BoxNode { Type = "----", EditableKey = "com.apple.iTunes:CDDB1" };
        Assert.IsFalse(ClipMetaSchema.IsClipmetaFreeformAtom(node));
    }

    [TestMethod]
    public void IsClipmetaFreeformAtom_FalseForNonFreeformNode()
    {
        var node = new BoxNode { Type = "©nam", EditableKey = ClipMetaSchema.Domain + ":game" };
        Assert.IsFalse(ClipMetaSchema.IsClipmetaFreeformAtom(node));
    }

    [TestMethod]
    public void IsClipmetaFreeformAtom_FalseWhenEditableKeyNull()
    {
        var node = new BoxNode { Type = "----", EditableKey = null };
        Assert.IsFalse(ClipMetaSchema.IsClipmetaFreeformAtom(node));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo --filter "ClassName~ClipMetaSchemaTests"`
Expected: FAIL to compile with "IsClipmetaFreeformAtom does not exist".

- [ ] **Step 3: Write minimal implementation**

In `clipmeta.core/Schema/ClipMetaSchema.cs`, add `using ClipMetaCore.Mp4;` at the top if not present, and add next to the `Domain` const:

```csharp
    /// <summary>The domain namespace followed by the field separator: "com.peckworkslab.clipmeta:".</summary>
    public const string DomainFieldPrefix = Domain + ":";

    /// <summary>
    /// True when <paramref name="node"/> is a freeform ("----") atom whose key is in the
    /// clipmeta domain namespace. This is the INTRINSIC clipmeta-atom test only: it carries no
    /// location scoping and no display-value requirement, so it is safe to share with the box-tree
    /// mapper without altering the reader's or the write gate's own (deliberately different) checks.
    /// </summary>
    /// <param name="node">The parsed box node to test.</param>
    public static bool IsClipmetaFreeformAtom(BoxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Type == "----"
            && node.EditableKey is not null
            && node.EditableKey.StartsWith(DomainFieldPrefix, StringComparison.Ordinal);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test clipmetascribe.Tests --nologo --filter "ClassName~ClipMetaSchemaTests"`
Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Schema/ClipMetaSchema.cs clipmetascribe.Tests/ClipMetaSchemaTests.cs
git commit -m "feat(core): add intrinsic clipmeta freeform-atom predicate"
```

---

### Task 2: Box definitions table

**Files:**
- Create: `clipmeta.core/Mp4/BoxDefinitions.cs`
- Test: `clipmetascribe.Tests/BoxDefinitionsTests.cs`

**Interfaces:**
- Consumes: `MetadataKeys.GetName`, `MetadataKeys.GetCategory`, `MetadataKeys.All`, `BoxCategory`.
- Produces:
  - `sealed class BoxDefinition { string FriendlyName; BoxCategory Category; string? Description; }`
  - `BoxDefinition BoxDefinitions.GetDefinition(string type)`
  - `IReadOnlyDictionary<string, BoxDefinition> BoxDefinitions.AllDefinitions()`
  - `BoxCategory BoxDefinitions.CategoryFor(string type)` (editable-aware: iTunes/editable field types return `EditableMeta`)
  Used by the CLI `--definitions` (Task 6) and available to the mapper's category rule.

- [ ] **Step 1: Write the failing test**

Create `clipmetascribe.Tests/BoxDefinitionsTests.cs`:

```csharp
using ClipMetaCore.Mp4;

namespace ClipMetaScribe.Tests;

[TestClass]
public class BoxDefinitionsTests
{
    [TestMethod]
    public void GetDefinition_KnownStructuralType_HasNameCategoryAndDescription()
    {
        BoxDefinition d = BoxDefinitions.GetDefinition("moov");
        Assert.AreEqual("Movie", d.FriendlyName);
        Assert.AreEqual(BoxCategory.Structural, d.Category);
        Assert.IsFalse(string.IsNullOrEmpty(d.Description), "known types should carry a description");
    }

    [TestMethod]
    public void GetDefinition_ItunesField_IsEditableMeta()
    {
        BoxDefinition d = BoxDefinitions.GetDefinition("©nam");
        Assert.AreEqual("Title", d.FriendlyName);
        Assert.AreEqual(BoxCategory.EditableMeta, d.Category);
    }

    [TestMethod]
    public void GetDefinition_UnknownType_FallsBackToTypeNameAndNoDescription()
    {
        BoxDefinition d = BoxDefinitions.GetDefinition("zzzz");
        Assert.AreEqual("zzzz", d.FriendlyName);
        Assert.IsNull(d.Description);
    }

    [TestMethod]
    public void AllDefinitions_CoversEveryKnownMetadataKey()
    {
        var all = BoxDefinitions.AllDefinitions();
        foreach (string type in MetadataKeys.All.Keys)
            Assert.IsTrue(all.ContainsKey(type), $"missing definition for {type}");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo --filter "ClassName~BoxDefinitionsTests"`
Expected: FAIL to compile with "BoxDefinitions does not exist".

- [ ] **Step 3: Write minimal implementation**

Create `clipmeta.core/Mp4/BoxDefinitions.cs`:

```csharp
namespace ClipMetaCore.Mp4;

/// <summary>A static, clip-independent description of one MP4 box type for structured consumers.</summary>
public sealed class BoxDefinition
{
    /// <summary>Human-readable name, or the raw FourCC when the type is unknown.</summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>Semantic category. Editable metadata field types report <see cref="BoxCategory.EditableMeta"/>.</summary>
    public BoxCategory Category { get; init; }

    /// <summary>One-line explanation of the box type, or null when undocumented.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Static reference data describing MP4 box types: friendly name, semantic category, and a
/// one-line description. Serves the CLI <c>--definitions</c> dictionary and any structured
/// consumer. This is the single source of the JSON <c>description</c> layer; the ASCII legend
/// keeps its own hand-tuned column strings and is intentionally NOT rendered from here.
/// </summary>
public static class BoxDefinitions
{
    // iTunes/editable field types: the © prefix plus the non-© editable fields the tagger writes.
    private static readonly HashSet<string> EditableFieldTypes = new(StringComparer.Ordinal)
    {
        "desc", "covr", "trkn", "disk", "aART", "tmpo", "cpil", "name",
        "gnre", "ldes", "purd", "cprt", "stik", "rtng", "pgap", "hdvd", "shwm",
    };

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["ftyp"] = "File type: MP4 brand and compatible variants (isom, mp42, M4V).",
        ["moov"] = "Root container for all structure and metadata.",
        ["mvhd"] = "Movie header: total duration, creation date, and playback rate.",
        ["trak"] = "One media stream: video, audio, timecode, or subtitle.",
        ["tkhd"] = "Track header: track ID, flags, duration, and pixel dimensions.",
        ["edts"] = "Edit list container for a track.",
        ["elst"] = "Edit list mapping the presentation timeline to the media timeline.",
        ["mdia"] = "Media-type container for a track.",
        ["mdhd"] = "Media header: per-track timescale, language, and duration.",
        ["hdlr"] = "Handler reference declaring media type: Video, Sound, Timecode, or Text.",
        ["minf"] = "Media information: links the sample table to the track's media type.",
        ["vmhd"] = "Video media header; marks the track as video.",
        ["smhd"] = "Sound media header; marks the track as audio.",
        ["dinf"] = "Data information: where the media data is located.",
        ["dref"] = "Data reference: URL or URN pointing to the media data.",
        ["stbl"] = "Sample table: master index mapping playback time to file offsets.",
        ["stsd"] = "Sample description: codec parameters (avc1=H.264, hvc1=H.265, mp4a=AAC).",
        ["stts"] = "Time-to-sample: duration of each sample in decoding order.",
        ["stss"] = "Sync sample table of keyframes; audio tracks omit this.",
        ["stsc"] = "Sample-to-chunk: groups samples into storage chunks.",
        ["stsz"] = "Sample size: byte size of every individual media sample.",
        ["stco"] = "Chunk offset (32-bit); co64 is the 64-bit form for large files.",
        ["co64"] = "Chunk offset (64-bit).",
        ["udta"] = "User data: optional container for custom or vendor metadata.",
        ["meta"] = "Metadata header (also a FullBox).",
        ["ilst"] = "Item list holding the editable iTunes-style tag fields.",
        ["data"] = "Value payload of a metadata item.",
        ["mean"] = "Namespace name of a freeform (----) metadata atom.",
        ["name"] = "Field name of a freeform (----) atom, or a track/handler label.",
        ["----"] = "Freeform metadata atom (holds custom/extended fields, including clipmeta).",
        ["mdat"] = "Media data: raw encoded audio and video samples, not expanded by this tool.",
        ["free"] = "Free space padding.",
        ["skip"] = "Skip padding.",
        ["Xtra"] = "Windows Media attributes written by Windows File Explorer.",
    };

    /// <summary>Returns the editable-aware category for a box type.</summary>
    /// <param name="type">The FourCC to classify.</param>
    public static BoxCategory CategoryFor(string type)
    {
        if (type.StartsWith("©", StringComparison.Ordinal) || EditableFieldTypes.Contains(type))
            return BoxCategory.EditableMeta;
        return MetadataKeys.GetCategory(type);
    }

    /// <summary>Returns the definition for a single box type; unknown types fall back to the raw FourCC and no description.</summary>
    /// <param name="type">The FourCC to describe.</param>
    public static BoxDefinition GetDefinition(string type) => new()
    {
        FriendlyName = MetadataKeys.GetName(type),
        Category = CategoryFor(type),
        Description = Descriptions.TryGetValue(type, out string? d) ? d : null,
    };

    /// <summary>Returns definitions for every box type with a registered friendly name, keyed by FourCC.</summary>
    public static IReadOnlyDictionary<string, BoxDefinition> AllDefinitions()
    {
        var result = new Dictionary<string, BoxDefinition>(StringComparer.Ordinal);
        foreach (string type in MetadataKeys.All.Keys)
            result[type] = GetDefinition(type);
        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test clipmetascribe.Tests --nologo --filter "ClassName~BoxDefinitionsTests"`
Expected: PASS (4/4).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Mp4/BoxDefinitions.cs clipmetascribe.Tests/BoxDefinitionsTests.cs
git commit -m "feat(core): add static box-type definitions table"
```

---

### Task 3: BoxTree DTOs and mapper

**Files:**
- Create: `clipmeta.core/Read/BoxTree.cs` (both DTOs)
- Create: `clipmeta.core/Read/BoxTreeMapper.cs`
- Modify: `clipmeta.core/Read/ClipMetaReader.cs` (change `UnquoteDisplayValue` from `private` to `internal`, no logic change)
- Test: `clipmetascribe.Tests/BoxTreeMapperTests.cs`

**Interfaces:**
- Consumes: `BoxNode`, `MetadataKeys`, `BoxCategory`, `ClipMetaSchema.IsClipmetaFreeformAtom` (Task 1), `ClipMetaReader.UnquoteDisplayValue`.
- Produces:
  - `sealed class BoxTreeNode` with: `string Type`, `long Offset`, `ulong Size`, `int HeaderSize`, `bool IsFullBox`, `byte Version`, `uint Flags`, `string FriendlyName`, `BoxCategory Category`, `string? DisplayValue`, `bool IsEditable`, `string? EditableKey`, `bool WasClamped`, `bool HasReliableOffsets`, `bool IsClipmetaContainer`, `IReadOnlyList<BoxTreeNode> Children`.
  - `sealed class BoxTree` with: `string Path`, `long FileSize`, `IReadOnlyList<BoxTreeNode> Boxes`.
  - `BoxTree BoxTreeMapper.Map(BoxNode root, string resolvedPath, long fileSize)`.
  Used by `BoxTreeJson` (Task 4), the CLI (Task 6), and the MCP tool (Task 7).

- [ ] **Step 1: Write the failing test**

Create `clipmetascribe.Tests/BoxTreeMapperTests.cs`:

```csharp
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;

namespace ClipMetaScribe.Tests;

[TestClass]
public class BoxTreeMapperTests
{
    private static BoxNode Root(params BoxNode[] children) =>
        new() { Type = "ROOT", Children = children.ToList() };

    [TestMethod]
    public void Map_CopiesGeometryAndFriendlyName()
    {
        var ftyp = new BoxNode { Type = "ftyp", Size = 32, FileOffset = 0, HeaderSize = 8 };
        BoxTree tree = BoxTreeMapper.Map(Root(ftyp), @"C:\clips\a.mp4", 32);

        Assert.AreEqual(@"C:\clips\a.mp4", tree.Path);
        Assert.AreEqual(32, tree.FileSize);
        BoxTreeNode n = tree.Boxes.Single();
        Assert.AreEqual("ftyp", n.Type);
        Assert.AreEqual(0, n.Offset);
        Assert.AreEqual(32ul, n.Size);
        Assert.AreEqual(8, n.HeaderSize);
        Assert.AreEqual("File Type", n.FriendlyName);
        Assert.AreEqual(BoxCategory.Header, n.Category);
    }

    [TestMethod]
    public void Map_UnquotesStringDisplayValue()
    {
        var brand = new BoxNode { Type = "ftyp", DisplayValue = "\"isom\"" };
        BoxTreeNode n = BoxTreeMapper.Map(Root(brand), "p", 0).Boxes.Single();
        Assert.AreEqual("isom", n.DisplayValue);
    }

    [TestMethod]
    public void Map_ClipmetaFreeformAtom_IsFlaggedAndEditableAwareCategory()
    {
        var atom = new BoxNode
        {
            Type = "----",
            EditableKey = ClipMetaSchema.Domain + ":game",
            DisplayValue = "\"TF2\"",
            IsEditable = true,
        };
        BoxTreeNode n = BoxTreeMapper.Map(Root(atom), "p", 0).Boxes.Single();
        Assert.IsTrue(n.IsClipmetaContainer);
        Assert.AreEqual(BoxCategory.EditableMeta, n.Category);
        Assert.AreEqual(ClipMetaSchema.Domain + ":game", n.EditableKey);
    }

    [TestMethod]
    public void Map_ForeignFreeformAtom_NotFlagged()
    {
        var atom = new BoxNode { Type = "----", EditableKey = "com.apple.iTunes:X", DisplayValue = "\"y\"" };
        BoxTreeNode n = BoxTreeMapper.Map(Root(atom), "p", 0).Boxes.Single();
        Assert.IsFalse(n.IsClipmetaContainer);
    }

    [TestMethod]
    public void Map_RecursesChildrenAndLeavesAreEmpty()
    {
        var leaf = new BoxNode { Type = "mvhd" };
        var moov = new BoxNode { Type = "moov", Children = { leaf } };
        BoxTreeNode m = BoxTreeMapper.Map(Root(moov), "p", 0).Boxes.Single();
        Assert.AreEqual(1, m.Children.Count);
        Assert.AreEqual(0, m.Children.Single().Children.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo --filter "ClassName~BoxTreeMapperTests"`
Expected: FAIL to compile with "BoxTree / BoxTreeMapper does not exist".

- [ ] **Step 3a: Create the DTOs**

Create `clipmeta.core/Read/BoxTree.cs`:

```csharp
using ClipMetaCore.Mp4;

namespace ClipMetaCore.Read;

/// <summary>One node in the structured box tree returned by <see cref="BoxTreeMapper"/>.</summary>
public sealed class BoxTreeNode
{
    /// <summary>Four-character box type (FourCC).</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Absolute byte offset of the box's first byte in the file.</summary>
    public long Offset { get; init; }

    /// <summary>Total box size in bytes, including the header.</summary>
    public ulong Size { get; init; }

    /// <summary>Header byte count: 8 for a standard box, 16 with an extended-size field.</summary>
    public int HeaderSize { get; init; }

    /// <summary>True when the box carries an ISO FullBox version byte and 24-bit flags after the header.</summary>
    public bool IsFullBox { get; init; }

    /// <summary>FullBox version, or 0.</summary>
    public byte Version { get; init; }

    /// <summary>FullBox flags, or 0.</summary>
    public uint Flags { get; init; }

    /// <summary>Friendly name for the type, or the raw FourCC when unknown.</summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>Semantic category; editable metadata fields report <see cref="BoxCategory.EditableMeta"/>.</summary>
    public BoxCategory Category { get; init; }

    /// <summary>Decoded human-readable value for leaf metadata boxes (unquoted), or null.</summary>
    public string? DisplayValue { get; init; }

    /// <summary>True for metadata items the tagger can add, update, or delete.</summary>
    public bool IsEditable { get; init; }

    /// <summary>Raw editable key for editable items, or null.</summary>
    public string? EditableKey { get; init; }

    /// <summary>True when the box's on-disk size claimed more bytes than its container held and was clamped.</summary>
    public bool WasClamped { get; init; }

    /// <summary>False when Offset/Size are approximate (e.g. Xtra child items) and must not be byte-trusted.</summary>
    public bool HasReliableOffsets { get; init; } = true;

    /// <summary>True when this is a clipmeta-namespaced freeform ("----") atom.</summary>
    public bool IsClipmetaContainer { get; init; }

    /// <summary>Child boxes; empty for leaves (including mdat, which is never expanded).</summary>
    public IReadOnlyList<BoxTreeNode> Children { get; init; } = [];
}

/// <summary>Structured box tree for one MP4 file.</summary>
public sealed class BoxTree
{
    /// <summary>Resolved absolute path of the parsed file.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>Top-level boxes, in file order.</summary>
    public IReadOnlyList<BoxTreeNode> Boxes { get; init; } = [];
}
```

- [ ] **Step 3b: Widen `UnquoteDisplayValue` visibility**

In `clipmeta.core/Read/ClipMetaReader.cs`, change the signature at line 59 from `private static string UnquoteDisplayValue` to `internal static string UnquoteDisplayValue` (no body change). Update its position/doc if needed; the logic stays identical.

- [ ] **Step 3c: Create the mapper**

Create `clipmeta.core/Read/BoxTreeMapper.cs`:

```csharp
using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Read;

/// <summary>Maps a parsed <see cref="BoxNode"/> tree into the serializable <see cref="BoxTree"/> DTO. Pure; no IO.</summary>
public static class BoxTreeMapper
{
    /// <summary>Builds a <see cref="BoxTree"/> from a parsed root, a resolved path, and the file size.</summary>
    /// <param name="root">The root node from <see cref="Mp4Parser.ParseFile"/>.</param>
    /// <param name="resolvedPath">Absolute path to report in the output.</param>
    /// <param name="fileSize">File size in bytes.</param>
    public static BoxTree Map(BoxNode root, string resolvedPath, long fileSize)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new BoxTree
        {
            Path = resolvedPath,
            FileSize = fileSize,
            Boxes = root.Children.Select(MapNode).ToList(),
        };
    }

    private static BoxTreeNode MapNode(BoxNode node) => new()
    {
        Type = node.Type,
        Offset = node.FileOffset,
        Size = node.Size,
        HeaderSize = node.HeaderSize,
        IsFullBox = node.IsFullBox,
        Version = node.Version,
        Flags = node.Flags,
        FriendlyName = MetadataKeys.GetName(node.Type),
        Category = node.IsEditable ? BoxCategory.EditableMeta : MetadataKeys.GetCategory(node.Type),
        DisplayValue = node.DisplayValue is null ? null : ClipMetaReader.UnquoteDisplayValue(node.DisplayValue),
        IsEditable = node.IsEditable,
        EditableKey = node.EditableKey,
        WasClamped = node.WasClamped,
        HasReliableOffsets = node.HasReliableOffsets,
        IsClipmetaContainer = ClipMetaSchema.IsClipmetaFreeformAtom(node),
        Children = node.Children.Select(MapNode).ToList(),
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test clipmetascribe.Tests --nologo --filter "ClassName~BoxTreeMapperTests"`
Expected: PASS (5/5).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Read/BoxTree.cs clipmeta.core/Read/BoxTreeMapper.cs clipmeta.core/Read/ClipMetaReader.cs clipmetascribe.Tests/BoxTreeMapperTests.cs
git commit -m "feat(core): add box-tree DTO and mapper"
```

---

### Task 4: Shared JSON serialization

**Files:**
- Create: `clipmeta.core/Read/BoxTreeJson.cs`
- Test: `clipmetascribe.Tests/BoxTreeJsonTests.cs`

**Interfaces:**
- Consumes: `BoxTree`, `BoxTreeNode` (Task 3), `BoxDefinition` (Task 2).
- Produces:
  - `JsonSerializerOptions BoxTreeJson.Options` (camelCase, string enums, omit-null, compact)
  - `string BoxTreeJson.ToJson(BoxTree tree)`
  - `JsonObject BoxTreeJson.ToJsonObject(BoxTree tree)` (for the MCP handler)
  - `string BoxTreeJson.DefinitionsToJson(IReadOnlyDictionary<string, BoxDefinition> defs)`
  - `JsonObject BoxTreeJson.DefinitionsToJsonObject(IReadOnlyDictionary<string, BoxDefinition> defs)`
  Used by the CLI (Task 6) and the MCP tool (Task 7). `ToJson(tree)` and `ToJsonObject(tree).ToJsonString()` are byte-identical by construction (same `SerializeToNode` call).

- [ ] **Step 1: Write the failing test**

Create `clipmetascribe.Tests/BoxTreeJsonTests.cs`:

```csharp
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;

namespace ClipMetaScribe.Tests;

[TestClass]
public class BoxTreeJsonTests
{
    private static BoxTree SampleTree()
    {
        var ftyp = new BoxNode { Type = "ftyp", Size = 32, FileOffset = 0, HeaderSize = 8, DisplayValue = "\"isom\"" };
        var atom = new BoxNode { Type = "----", EditableKey = ClipMetaSchema.Domain + ":game", DisplayValue = "\"TF2\"", IsEditable = true };
        var root = new BoxNode { Type = "ROOT", Children = { ftyp, atom } };
        return BoxTreeMapper.Map(root, @"C:\clips\a.mp4", 32);
    }

    [TestMethod]
    public void ToJson_UsesCamelCaseKeys()
    {
        string json = BoxTreeJson.ToJson(SampleTree());
        StringAssert.Contains(json, "\"fileSize\":");
        StringAssert.Contains(json, "\"isFullBox\":");
        StringAssert.Contains(json, "\"isClipmetaContainer\":");
        Assert.IsFalse(json.Contains("\"FileSize\""), "keys must be camelCase, not PascalCase");
    }

    [TestMethod]
    public void ToJson_SerializesCategoryAsStringName()
    {
        string json = BoxTreeJson.ToJson(SampleTree());
        StringAssert.Contains(json, "\"category\":\"Header\"");
        StringAssert.Contains(json, "\"category\":\"EditableMeta\"");
    }

    [TestMethod]
    public void ToJson_OmitsNullDisplayValueAndUnquotesPresentOne()
    {
        var leaf = new BoxNode { Type = "moov" }; // no DisplayValue
        var tree = BoxTreeMapper.Map(new BoxNode { Type = "ROOT", Children = { leaf } }, "p", 0);
        string json = BoxTreeJson.ToJson(tree);
        Assert.IsFalse(json.Contains("displayValue"), "null displayValue must be omitted");
        // and a present one is unquoted:
        StringAssert.Contains(BoxTreeJson.ToJson(SampleTree()), "\"displayValue\":\"isom\"");
    }

    [TestMethod]
    public void ToJson_DoesNotEmitContentOffset()
    {
        Assert.IsFalse(BoxTreeJson.ToJson(SampleTree()).Contains("contentOffset"));
    }

    [TestMethod]
    public void ToJsonObject_ToJsonString_EqualsToJson_ByteIdentical()
    {
        BoxTree tree = SampleTree();
        Assert.AreEqual(BoxTreeJson.ToJson(tree), BoxTreeJson.ToJsonObject(tree).ToJsonString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo --filter "ClassName~BoxTreeJsonTests"`
Expected: FAIL to compile with "BoxTreeJson does not exist".

- [ ] **Step 3: Write minimal implementation**

Create `clipmeta.core/Read/BoxTreeJson.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClipMetaCore.Mp4;

namespace ClipMetaCore.Read;

/// <summary>
/// The single JSON serialization contract for the box tree and box definitions. Both the CLI
/// (<c>clipmetaview --json</c>/<c>--definitions</c>) and the MCP <c>clip_get_boxtree</c> tool
/// route through here, so their output is byte-identical: camelCase keys, string enum names,
/// omitted null properties, compact (no indentation).
/// </summary>
public static class BoxTreeJson
{
    /// <summary>Shared serializer options. Do not mutate; construct a new instance if a variant is ever needed.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    /// <summary>Serializes a box tree to a compact JSON string.</summary>
    public static string ToJson(BoxTree tree) =>
        JsonSerializer.SerializeToNode(tree, Options)!.ToJsonString();

    /// <summary>Serializes a box tree to a <see cref="JsonObject"/> for the MCP handler result.</summary>
    public static JsonObject ToJsonObject(BoxTree tree) =>
        JsonSerializer.SerializeToNode(tree, Options)!.AsObject();

    /// <summary>Serializes the box-definitions dictionary to a compact JSON string.</summary>
    public static string DefinitionsToJson(IReadOnlyDictionary<string, BoxDefinition> defs) =>
        JsonSerializer.SerializeToNode(defs, Options)!.ToJsonString();

    /// <summary>Serializes the box-definitions dictionary to a <see cref="JsonObject"/>.</summary>
    public static JsonObject DefinitionsToJsonObject(IReadOnlyDictionary<string, BoxDefinition> defs) =>
        JsonSerializer.SerializeToNode(defs, Options)!.AsObject();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test clipmetascribe.Tests --nologo --filter "ClassName~BoxTreeJsonTests"`
Expected: PASS (5/5).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Read/BoxTreeJson.cs clipmetascribe.Tests/BoxTreeJsonTests.cs
git commit -m "feat(core): add shared box-tree JSON serializer"
```

---

### Task 5: Move TreeRenderer into Core (gate console reset, invariant culture)

**Files:**
- Move: `clipmetaview/Rendering/TreeRenderer.cs` -> `clipmeta.core/Rendering/TreeRenderer.cs`
- Modify: the moved file (namespace, two behavior fixes)
- Modify: `clipmetaview/AppRunner.cs` (using directive)
- Modify: `clipmetaview.Tests/TreeRendererTests.cs` (using directive)
- (Modify any other file that references `ClipMetaView.Rendering`, verified by grep)

**Interfaces:**
- Produces: `ClipMetaCore.Rendering.TreeRenderer` with unchanged public methods `Render(BoxNode, string, TextWriter?)` and `RenderSummary(BoxNode, TextWriter?)`. Used by `AppRunner` (Task 6) and the MCP tool (Task 7).

- [ ] **Step 1: Move the file and update its namespace**

```bash
git mv clipmetaview/Rendering/TreeRenderer.cs clipmeta.core/Rendering/TreeRenderer.cs
```

In `clipmeta.core/Rendering/TreeRenderer.cs`, change the namespace from `ClipMetaView.Rendering` to `ClipMetaCore.Rendering`. Keep `using ClipMetaCore.Mp4;`, and add `using System.Globalization;` at the top.

- [ ] **Step 2: Gate the console reset (side-effect fix)**

In the moved file, change the `Render` method's `finally` block:

```csharp
        finally
        {
            if (useColor) Console.ResetColor();
        }
```

(Previously `Console.ResetColor();` unconditionally. `useColor` is in scope; this makes a StringWriter render side-effect-free, protecting the MCP server's stdout.)

- [ ] **Step 3: Force invariant-culture number formatting**

In `BuildNodeLine`, replace the size/offset line:

```csharp
        string sizeAndOffset =
            $"[{node.Size.ToString("N0", CultureInfo.InvariantCulture)} bytes @ 0x{node.FileOffset:X}]";
```

In `FormatFileSize`, replace the two formatted returns:

```csharp
        if (bytes >= MB) return $"{((double)bytes / MB).ToString("F1", CultureInfo.InvariantCulture)} MB";
        if (bytes >= KB) return $"{((double)bytes / KB).ToString("F1", CultureInfo.InvariantCulture)} KB";
```

- [ ] **Step 4: Update all references to the old namespace**

Update `clipmetaview/AppRunner.cs` line 2: `using ClipMetaView.Rendering;` -> `using ClipMetaCore.Rendering;`.
Update `clipmetaview.Tests/TreeRendererTests.cs`: `using ClipMetaView.Rendering;` -> `using ClipMetaCore.Rendering;`.
Then confirm nothing else references the old namespace:

Run: `git grep -n "ClipMetaView.Rendering"`
Expected: no matches. Fix any that remain.

- [ ] **Step 5: Write the invariant-culture regression test**

Add to `clipmetaview.Tests/TreeRendererTests.cs`:

```csharp
[TestMethod]
public void Render_NumberFormatting_IsInvariantAcrossCultures()
{
    var big = new BoxNode { Type = "mdat", Size = 1234567, FileOffset = 4096, HeaderSize = 8 };
    var root = new BoxNode { Type = "ROOT", Children = { big } };

    var original = System.Threading.Thread.CurrentThread.CurrentCulture;
    try
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        var sw = new StringWriter();
        TreeRenderer.Render(root, "test.mp4", sw);
        // Invariant grouping uses a comma; a de-DE default would have used a period.
        StringAssert.Contains(sw.ToString(), "1,234,567 bytes");
    }
    finally
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = original;
    }
}
```

Add `using System.Globalization;` and `using ClipMetaCore.Mp4;` to the test file if not present.

- [ ] **Step 6: Build and run the FULL view test project (parity)**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors.
Run: `dotnet test clipmetaview.Tests --nologo --no-build -v q`
Expected: all pass (existing `TreeRendererTests` prove ASCII parity is preserved; the new test proves locale independence).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor: move tree renderer into core, gate console reset, invariant-culture numbers"
```

---

### Task 6: CLI flags --json and --definitions

**Files:**
- Modify: `clipmetaview/AppRunner.cs`
- Test: `clipmetaview.Tests/AppRunnerTests.cs` (add to the existing file; create if absent)

**Interfaces:**
- Consumes: `Mp4Parser.ParseFile`, `BoxTreeMapper.Map`, `BoxTreeJson.ToJson`, `BoxDefinitions.AllDefinitions`, `BoxTreeJson.DefinitionsToJson`, `ClipMetaCore.Rendering.TreeRenderer`.
- Produces: extended `AppRunner.RunAsync` arg grammar (see Global Constraints registration/grammar). Exit codes unchanged (`ExitSuccess`=0, `ExitBadArgs`=1, `ExitParseError`=2).

Grammar (decided in the spec):
- `<path.mp4>` -> ASCII tree + summary (unchanged default).
- `<path.mp4> --json` or `--json <path.mp4>` -> box-tree JSON. `--json` requires a path.
- `--definitions` -> definitions JSON; needs no path; extra args ignored.
- `--json` and `--definitions` together -> `ExitBadArgs`.
- Unknown `--flag` -> `ExitBadArgs`.

- [ ] **Step 1: Write the failing tests**

Add to `clipmetaview.Tests/AppRunnerTests.cs`:

```csharp
[TestMethod]
public async Task Definitions_EmitsJsonDictionary_NoPathNeeded()
{
    var sw = new StringWriter();
    int code = await AppRunner.RunAsync(new[] { "--definitions" }, sw);
    Assert.AreEqual(AppRunner.ExitSuccess, code);
    string outp = sw.ToString();
    StringAssert.Contains(outp, "\"moov\":");
    StringAssert.Contains(outp, "\"friendlyName\":\"Movie\"");
}

[TestMethod]
public async Task Json_WithPath_EmitsBoxTree_EitherFlagPosition()
{
    string clip = TestClips.AnyTaggedOrSynthetic(); // existing helper or MinimalMp4Builder path
    var a = new StringWriter();
    var b = new StringWriter();
    Assert.AreEqual(AppRunner.ExitSuccess, await AppRunner.RunAsync(new[] { clip, "--json" }, a));
    Assert.AreEqual(AppRunner.ExitSuccess, await AppRunner.RunAsync(new[] { "--json", clip }, b));
    StringAssert.Contains(a.ToString(), "\"boxes\":");
    Assert.AreEqual(a.ToString(), b.ToString(), "flag position must not change output");
}

[TestMethod]
public async Task JsonAndDefinitions_Together_IsBadArgs()
{
    var sw = new StringWriter();
    int code = await AppRunner.RunAsync(new[] { "--json", "--definitions" }, sw);
    Assert.AreEqual(AppRunner.ExitBadArgs, code);
}

[TestMethod]
public async Task UnknownFlag_IsBadArgs()
{
    var sw = new StringWriter();
    int code = await AppRunner.RunAsync(new[] { "--frobnicate" }, sw);
    Assert.AreEqual(AppRunner.ExitBadArgs, code);
}

[TestMethod]
public async Task Json_OnUnparseableFile_IsParseError_NoPartialJson()
{
    string bad = Path.Combine(Path.GetTempPath(), "notreal.mp4");
    File.WriteAllBytes(bad, new byte[] { 1, 2, 3 });
    try
    {
        var sw = new StringWriter();
        int code = await AppRunner.RunAsync(new[] { bad, "--json" }, sw);
        Assert.AreEqual(AppRunner.ExitParseError, code);
        Assert.IsFalse(sw.ToString().Contains("\"boxes\""), "no partial JSON on parse failure");
    }
    finally { File.Delete(bad); }
}
```

Note: if `TestClips.AnyTaggedOrSynthetic()` does not exist, use the project's existing minimal-clip helper (the same one `TreeRendererTests` uses) or build one with `MinimalMp4Builder`. Match the existing test's clip-acquisition pattern.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test clipmetaview.Tests --nologo --filter "ClassName~AppRunnerTests"`
Expected: FAIL (new grammar not implemented; `--definitions` currently hits "File not found").

- [ ] **Step 3: Rewrite `AppRunner.RunAsync` with flag-aware parsing**

Replace the body of `RunAsync` in `clipmetaview/AppRunner.cs` (keep the XML docs and exit-code constants). Add `using ClipMetaCore.Read;` and `using ClipMetaCore.Rendering;` and `using ClipMetaCore.Mp4;`:

```csharp
    public static Task<int> RunAsync(string[] args, TextWriter? writer = null)
    {
        writer ??= Console.Out;

        bool wantJson = false;
        bool wantDefinitions = false;
        string? path = null;

        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--json": wantJson = true; break;
                case "--definitions": wantDefinitions = true; break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"Error: Unknown option: {arg}");
                        return Task.FromResult(ExitBadArgs);
                    }
                    path ??= arg;
                    break;
            }
        }

        if (wantJson && wantDefinitions)
        {
            Console.Error.WriteLine("Error: --json and --definitions cannot be combined.");
            return Task.FromResult(ExitBadArgs);
        }

        // --definitions: clip-independent, needs no path.
        if (wantDefinitions)
        {
            writer.WriteLine(BoxTreeJson.DefinitionsToJson(BoxDefinitions.AllDefinitions()));
            return Task.FromResult(ExitSuccess);
        }

        if (path is null)
        {
            Console.Error.WriteLine("Usage: clipmetaview <path-to-file.mp4> [--json]");
            Console.Error.WriteLine("       clipmetaview --definitions");
            Console.Error.WriteLine("  Displays the internal box/atom structure of an MP4 file.");
            return Task.FromResult(ExitBadArgs);
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Error: File not found: {path}");
            return Task.FromResult(ExitBadArgs);
        }

        if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Error: Only .mp4 files are supported. Got: {path}");
            return Task.FromResult(ExitBadArgs);
        }

        try
        {
            var root = Mp4Parser.ParseFile(path);

            if (wantJson)
            {
                long fileSize = new FileInfo(path).Length;
                writer.WriteLine(BoxTreeJson.ToJson(BoxTreeMapper.Map(root, path, fileSize)));
            }
            else
            {
                TreeRenderer.Render(root, path, writer);
                TreeRenderer.RenderSummary(root, writer);
            }
            return Task.FromResult(ExitSuccess);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            Console.Error.WriteLine($"Error: Failed to parse MP4 file: {ex.Message}");
            return Task.FromResult(ExitParseError);
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test clipmetaview.Tests --nologo --filter "ClassName~AppRunnerTests"`
Expected: PASS.

- [ ] **Step 5: Run the FULL view test project (surface change)**

Run: `dotnet build --nologo -v q && dotnet test clipmetaview.Tests --nologo --no-build -v q`
Expected: 0 warnings, all pass.

- [ ] **Step 6: Commit**

```bash
git add clipmetaview/AppRunner.cs clipmetaview.Tests/AppRunnerTests.cs
git commit -m "feat(cli): add clipmetaview --json and --definitions"
```

---

### Task 7: MCP tool clip_get_boxtree

**Files:**
- Modify: `clipmetamcp/Tools/ReadTools.cs` (register the tool after `library_watching`; add handler + schema)
- Modify: `clipmetamcp.Tests/Phase2ReadToolsTests.cs` (surface test 17 -> 18; add behavior tests)

**Interfaces:**
- Consumes: `LibrarySandbox.ResolveClipPath`, `ReadTools.ParseClip`, `BoxTreeMapper.Map`, `BoxTreeJson.ToJsonObject`, `ClipMetaCore.Rendering.TreeRenderer`, `ToolDefinition`.
- Produces: registered tool `clip_get_boxtree` with args `path` (required) and `render` (`json` default | `ascii`). `json` returns the box-tree JSON object (byte-identical to CLI `--json`); `ascii` returns `{ "ascii": "<tree + summary text>" }`.

- [ ] **Step 1: Write the failing tests**

In `clipmetamcp.Tests/Phase2ReadToolsTests.cs`, update the surface test array and add behavior tests:

```csharp
// In ToolsList_ContainsTheFullToolSurface, the expected array becomes (note the added entry
// immediately after "library_watching"):
//   "library_watching", "clip_get_boxtree",
//   "clip_set_fields", ...

[TestMethod]
public void GetBoxTree_Json_ReturnsTopLevelBoxes()
{
    JsonObject result = Call("clip_get_boxtree", new JsonObject { ["path"] = _taggedPath });
    AssertOk(result);
    JsonObject s = Structured(result);
    Assert.IsTrue(s["boxes"]!.AsArray().Count > 0, "expected top-level boxes");
    var types = s["boxes"]!.AsArray().Select(b => b!["type"]!.GetValue<string>()).ToList();
    CollectionAssert.Contains(types, "ftyp");
}

[TestMethod]
public void GetBoxTree_Ascii_MatchesCliNonColorOutput()
{
    JsonObject result = Call("clip_get_boxtree",
        new JsonObject { ["path"] = _taggedPath, ["render"] = "ascii" });
    AssertOk(result);
    string toolAscii = Structured(result)["ascii"]!.GetValue<string>();

    var root = ClipMetaCore.Mp4.Mp4Parser.ParseFile(_taggedPath);
    var sw = new StringWriter();
    ClipMetaCore.Rendering.TreeRenderer.Render(root, _taggedPath, sw);
    ClipMetaCore.Rendering.TreeRenderer.RenderSummary(root, sw);
    Assert.AreEqual(sw.ToString(), toolAscii);
}

[TestMethod]
public void GetBoxTree_Json_ByteIdenticalToSharedSerializer()
{
    JsonObject result = Call("clip_get_boxtree", new JsonObject { ["path"] = _taggedPath });
    string toolText = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();

    var root = ClipMetaCore.Mp4.Mp4Parser.ParseFile(_taggedPath);
    long size = new FileInfo(_taggedPath).Length;
    string canonical = ClipMetaCore.Read.BoxTreeJson.ToJson(
        ClipMetaCore.Read.BoxTreeMapper.Map(root, _taggedPath, size));
    Assert.AreEqual(canonical, toolText, "MCP json text must equal the shared serializer output");
}

[TestMethod]
public void GetBoxTree_BadPath_Refuses()
{
    JsonObject result = Call("clip_get_boxtree", new JsonObject { ["path"] = "does-not-exist.mp4" });
    AssertRefused(result, "does-not-exist");
}
```

Note: match the existing test file's helpers (`Call`, `AssertOk`, `AssertRefused`, `Structured`, `_taggedPath`). If `_taggedPath` resolves via the sandbox, the path passed to `TreeRenderer` in the parity test must be the SAME resolved absolute path the tool uses; if the harness exposes only a library-relative `_taggedPath`, resolve it the same way the other read tests do before comparing.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test clipmetamcp.Tests --nologo --filter "ClassName~Phase2ReadToolsTests"`
Expected: FAIL (surface array mismatch; `clip_get_boxtree` not registered).

- [ ] **Step 3: Register the tool and add the handler + schema**

In `clipmetamcp/Tools/ReadTools.cs`, add `using ClipMetaCore.Read;` and `using ClipMetaCore.Rendering;` at the top. Immediately AFTER the `library_watching` registration block (the last read-tool `registry.Register(...)` call, near line 131-...), add:

```csharp
        registry.Register(new ToolDefinition(
            "clip_get_boxtree",
            "Returns the internal MP4 box/atom structure of one clip. 'path' must be an existing " +
            ".mp4 file inside the configured clips library; relative paths resolve against the " +
            "library root. 'render' is 'json' (default): a structured tree of boxes with type, " +
            "offset, size, header size, friendly name, category, decoded value, and a flag marking " +
            "clipmeta's own metadata atoms; or 'ascii': the same human-readable tree the " +
            "clipmetaview CLI prints, returned as the 'ascii' field. Read-only; never writes.",
            BoxTreeSchema(),
            args => GetBoxTree(args, sandbox),
            clipPath => new JsonObject { ["path"] = clipPath }));
```

Then add the handler and schema to the same class (place the handler with the other handlers, and the schema with the other `*Schema()` methods near `SinglePathSchema`):

```csharp
    /// <summary>Handler for clip_get_boxtree: structured box tree ('json') or the CLI ASCII tree ('ascii').</summary>
    internal static JsonObject GetBoxTree(JsonObject? args, LibrarySandbox sandbox)
    {
        string fullPath = sandbox.ResolveClipPath(GetRequiredString(args, "path"));
        string render = args?["render"]?.GetValue<string>() ?? "json";
        BoxNode root = ParseClip(fullPath);

        if (render == "ascii")
        {
            var sw = new StringWriter();
            TreeRenderer.Render(root, fullPath, sw);
            TreeRenderer.RenderSummary(root, sw);
            return new JsonObject { ["ascii"] = sw.ToString() };
        }

        if (render != "json")
            throw new ToolException($"'render' must be 'json' or 'ascii'; got '{render}'.");

        long fileSize = new FileInfo(fullPath).Length;
        return BoxTreeJson.ToJsonObject(BoxTreeMapper.Map(root, fullPath, fileSize));
    }

    private static JsonObject BoxTreeSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Path to an .mp4 file inside the clips library. Absolute, or relative to the library root.",
            },
            ["render"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("json", "ascii"),
                ["description"] = "Output format: 'json' (default, structured tree) or 'ascii' (the clipmetaview text tree).",
            },
        },
        ["required"] = new JsonArray("path"),
    };
```

- [ ] **Step 4: Update the surface test array**

In `ToolsList_ContainsTheFullToolSurface`, insert `"clip_get_boxtree"` immediately after `"library_watching",` so the read block reads:

```csharp
                "library_watching", "clip_get_boxtree",
                "clip_set_fields", "clip_append_field", "clip_clear_fields", "clip_clear_all",
```

- [ ] **Step 5: Run the FULL MCP test project (surface change)**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors.
Run: `dotnet test clipmetamcp.Tests --nologo --no-build -v q`
Expected: ALL pass, including `ToolsList_ContainsTheFullToolSurface` and the stdout-purity suite (which auto-drives the new tool via its `ExampleArguments`).

- [ ] **Step 6: Commit**

```bash
git add clipmetamcp/Tools/ReadTools.cs clipmetamcp.Tests/Phase2ReadToolsTests.cs
git commit -m "feat(mcp): add clip_get_boxtree read tool"
```

---

### Task 8: Full-suite verification and pitfalls note

**Files:**
- Modify: `docs/PITFALLS.md` (append any gotcha found during the build; if none, add the one below)

- [ ] **Step 1: Full build and test**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors, all projects.
Run: `dotnet test --nologo --no-build -v q`
Expected: all suites pass (foreground, long timeout; `clipmetascribe.Tests` takes a few minutes).

- [ ] **Step 2: Em-dash sweep**

Run: `git grep -nP '\x{2014}'` (the em-dash codepoint, U+2014)
Expected: no matches in anything this branch added. Remove any hit.

- [ ] **Step 3: Record the pitfall**

Append to `docs/PITFALLS.md`:

```markdown
## contentOffset is not the payload start for value atoms (2026-07)

`BoxNode.ContentOffset` only accounts for the ISO FullBox 4-byte prefix. The `data`, `mean`,
and freeform `name` atoms carry an additional value prefix (8 bytes for `data`, 4 for
`mean`/`name`) that the parser reads manually, outside `IsFullBox`. So `ContentOffset` points
BEFORE the real value for those atoms. `clip_get_boxtree` deliberately does not expose it; it
publishes `offset`, `size`, and `headerSize` (honest box geometry) only. If you ever add a
"payload start" field, special-case those three atom types.
```

- [ ] **Step 4: Commit**

```bash
git add docs/PITFALLS.md
git commit -m "docs: record contentOffset pitfall for the box-tree surface"
```

---

## Self-Review

**Spec coverage:**
- Component 1 (DTO + mapper): Tasks 3 (DTO, mapper), with `contentOffset` dropped, `category` editable-aware, `displayValue` unquoted, mdat-as-leaf covered. ✓
- Component 2 (intrinsic predicate; write gate untouched): Task 1; Global Constraints forbid touching the gate/reader. ✓
- Component 3 (renderer move; gate reset; invariant culture): Task 5. ✓
- Component 4 (definitions; legend untouched): Task 2 (table), Task 6 (`--definitions`). Legend is explicitly not rendered from the table. ✓
- Component 5 (MCP tool; registration position; surface test): Task 7. ✓
- Component 6 (shared serializer; byte-identical CLI==MCP): Task 4 (helper), asserted in Task 4 and Task 7. ✓
- Component 7 (CLI flags; arg grammar): Task 6. ✓
- Testing items 1-13: distributed across Tasks 1-7 (structural sanity item 3 is covered by the mapper/JSON tests plus the existing parser tests; the plan does not add a separate brittle offset-tiling test, per the spec's rescoping of test 3). ✓

**Placeholder scan:** No TBD/TODO; all code steps carry complete code. The only deferred choice is the clip-acquisition helper name in Tasks 6 and 7, which explicitly instructs matching the existing test project's pattern. ✓

**Type consistency:** `BoxTreeJson.ToJson`/`ToJsonObject`/`DefinitionsToJson`, `BoxTreeMapper.Map`, `BoxDefinitions.GetDefinition`/`AllDefinitions`/`CategoryFor`, `ClipMetaSchema.IsClipmetaFreeformAtom`, `TreeRenderer.Render`/`RenderSummary` are used with identical signatures across tasks. `BoxCategory.EditableMeta` is a real enum member (verified). ✓

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-02-clip-get-boxtree.md`.
