# Design: `clip_get_boxtree` MCP tool, `clipmetaview --json`, and box definitions

Date: 2026-07-02
Status: design, pending review (hardened by a nemesis + 2 domain-skeptic review, 2026-07-02)
Feature branch: `feat/boxtree-tool`

## Problem

The MP4 box/atom hierarchy is parsed by shared Core code (`Mp4Parser.ParseFile`,
producing a `BoxNode` tree) on every metadata read. But the *structural view* of that tree
is reachable only through `clipmetaview`'s ASCII renderer, which has no MCP seam and no
machine-readable output. Two things are blocked:

1. Seeing the box tree from Claude Desktop (there is no MCP tool for it).
2. Scripting the tree (for example a web-based tree view) from CLI output (the CLI only
   emits a human-readable ASCII tree, not structured data).

This is a gap-fill, not new parsing capability. The walker already runs; we surface its
output as structured data and give it an MCP seam.

## Architecture context (verified 2026-07-02, see `docs/architecture-audit.md`)

One shared engine: all three executables reference only `clipmeta.core`. `Mp4Parser` lives
in Core and is called by the MCP (`ReadTools.ParseClip`), both CLIs, and Core's read/write
layer. The one genuinely CLI-exclusive piece is `clipmetaview/Rendering/TreeRenderer.cs`.
This design moves that renderer into Core so nothing is CLI-exclusive afterward.

## Scope

### In
- New read-only MCP tool `clip_get_boxtree` (`render`: `json` | `ascii`).
- New CLI flag `clipmetaview <clip> --json`: the same structured tree as JSON.
- New CLI flag `clipmetaview --definitions`: a static box-type definitions dictionary.
- Move `TreeRenderer` from `clipmetaview` into `clipmeta.core` (shared, parity-locked),
  with two required behavior corrections (side-effect gating and invariant-culture number
  formatting, see Component 3).
- Add a static box-type **descriptions** table in Core for `--definitions` and the JSON.

### Out
- No writing, editing, or backup. Strictly read-only.
- No media-technical decoding (duration, codec, resolution, bitrate, frame rate). Separate
  reconnaissance spec: `2026-07-02-media-technical-facts-recon.md`.
- No MCP tool for definitions (Claude already knows box semantics; definitions serve dumb
  consumers, so they live on the CLI only).
- No changes to the existing 17 tools or to `clipmetascribe`.
- **No change to the write engine.** In particular `Mp4Writer.DetectNonCanonicalMetadata`'s
  refusal predicate is NOT modified (see Component 2).
- No new NuGet packages (`System.Text.Json` is BCL and permitted).
- Not a byte-slicer: the JSON exposes box geometry (offset, size, header size), not the
  decoded start of a leaf atom's value payload (see the `contentOffset` decision below).

## Components

### 1. Core DTO + mapper (`clipmeta.core`)
- `BoxTreeNode` (DTO): `type`, `offset`, `size`, `headerSize`, `isFullBox`, `version`,
  `flags`, `friendlyName`, `category`, `displayValue` (nullable, **unquoted**), `isEditable`,
  `editableKey` (nullable), `wasClamped`, `hasReliableOffsets`, `isClipmetaContainer`,
  `children` (array, empty for leaves).
  - **`contentOffset` is deliberately NOT exposed.** `BoxNode.ContentOffset` only accounts
    for the ISO FullBox 4-byte prefix, but `data`/`mean`/`name` atoms carry an additional
    8-byte (`data`) or 4-byte (`mean`/`name`) value prefix handled outside `IsFullBox`
    (`Mp4Parser.cs` `DataBoxOverhead`), so `contentOffset` would point before the real value
    for exactly the value-carrying leaves. Exposing `offset`, `size`, and `headerSize` (all
    honest box geometry) avoids shipping a field that lies for the interesting nodes.
  - **`category` is editable-aware in the mapper:** `IsEditable ? EditableMeta : GetCategory(type)`.
    `MetadataKeys.GetCategory` never returns `EditableMeta` (it is the renderer's color
    bucket and maps editable fields to `Unknown`), so the mapper computes the semantic
    category itself. Rendering and `GetCategory` are untouched. `category` serializes as its
    string name (see Component 6).
  - **`displayValue` is unquoted** in the DTO. `Mp4Parser` stores string display values
    wrapped in quotes (`$"\"{v}\""`); the mapper strips them with the same logic
    `ClipMetaReader.UnquoteDisplayValue` uses.
  - **Leaf vs non-expanded container:** `mdat` is the only box deliberately not recursed; a
    consumer detects it by `type == "mdat"` (explicit and stable), not by an incidental
    category. Documented in the DTO XML docs; no extra field.
- `BoxTree` (root DTO): `path` (resolved absolute), `fileSize`, `boxes` (top-level nodes).
- `BoxTreeMapper.Map(BoxNode root, string resolvedPath, long fileSize) -> BoxTree`: pure
  transform, no IO.

### 2. Shared clipmeta-container predicate (intrinsic only; write engine untouched)
Introduce ONE pure helper `ClipMetaSchema.IsClipmetaFreeformAtom(BoxNode)` =
`node.Type == "----" && node.EditableKey is not null && node.EditableKey.StartsWith(Domain + ":")`.
The mapper uses it to set `isClipmetaContainer`.

Critically, this shares only the **intrinsic** test. It does NOT unify the reader's and
writer's predicates, because they are deliberately different and context-scoped, and a
`BoxNode` has no parent pointer to reproduce their scoping:
- `ClipMetaReader.CollectFromNode` additionally requires `DisplayValue != null` and only
  runs on direct children of an `ilst` (canonical-scoped).
- `Mp4Writer.DetectNonCanonicalMetadata` requires only the prefix (no `Type`/`DisplayValue`
  check) and fires only OUTSIDE the canonical subtree; it is a write-refusal fail-safe.

Rerouting either through a helper that changed its firing set would be a load-bearing change
(e.g. adding the reader's `DisplayValue != null` clause to the gate would let a non-canonical
clipmeta atom with an unextractable value slip past the refusal). Therefore: the reader and
the write gate are left exactly as they are; only the mapper adopts the intrinsic helper. If
`ClipMetaReader`/`DetectNonCanonicalMetadata` are refactored to call the helper at all, it
must be provably behavior-identical (their extra clauses/scoping stay in place), guarded by
their existing tests.

`isClipmetaContainer` therefore means "this box is a clipmeta-namespaced freeform atom." For
a normal canonical file that is exactly the set the reader reads. On a spec-legal
non-canonical file it may also mark an atom the reader ignores; that is honest structural
information (the atom is physically present), not a disagreement to hide. The spec makes no
"can never disagree with the reader" claim.

### 3. Renderer move (`clipmetaview/Rendering/TreeRenderer.cs` -> `clipmeta.core`)
Move the class into Core (namespace `ClipMetaCore.Rendering`). Two required corrections,
because the class becomes reachable from the headless stdio MCP server:
- **Gate the console reset.** `Render`'s `finally { Console.ResetColor(); }` is currently
  unconditional; it must become `finally { if (useColor) Console.ResetColor(); }` so a
  non-console writer (the MCP `ascii` path, every test) has zero process-global side effect.
  This protects the server's stdout-purity guarantee.
- **Invariant-culture numbers.** The ASCII uses `{Size:N0}` and `{...:F1} MB`, which bind to
  `CurrentCulture`. Force `CultureInfo.InvariantCulture` so output is locale-independent and
  identical across the CLI, the MCP tool, and any CI runner. (This changes current CLI bytes
  only on non-en-US locales; snapshot the invariant output as the new golden.)

The **ASCII parity target is the non-color redirected capture** (what a `StringWriter`
receives), which is what both the CLI-to-file path and the MCP tool produce. The colored
terminal view is not a parity target. `clipmetaview/AppRunner.cs` updates its `using`/call
sites; behavior is otherwise unchanged (guarded by the snapshot test, R1).

### 4. Box definitions (`clipmeta.core`)
- Add a static per-type descriptions table (e.g. `BoxDefinitions`) exposing
  `GetDefinition(type) -> { friendlyName, category, description? }` and `AllDefinitions()`.
  `category` here is editable-aware (same rule as the mapper). `description` is present for
  documented types and omitted otherwise.
- **The ASCII legend is left exactly as it is.** Its labels are hand-abbreviated to fit
  fixed-width columns (e.g. "Movie Container" vs `MetadataKeys.GetName("moov")` = "Movie";
  "Edit List Cont." vs "Edit List Container") and its descriptions differ in case/punctuation
  from the definitions table. The legend and the definitions table are therefore NOT a single
  source; the legend keeps its bespoke strings so ASCII parity is preserved trivially, and
  the definitions table is its own data for JSON consumers. (This drops the earlier
  "single source of truth for the legend" idea, which was internally contradictory.)
- Backfill descriptions for known types that lack one; undocumented types return
  `{ friendlyName, category }` only.

### 5. MCP tool (`clipmetamcp`)
- New handler `clip_get_boxtree`. Params: `path` (string, required; resolved via the same
  `sandbox.ResolveClipPath` that `clip_get_metadata` uses, including junction/ADS/containment
  checks), `render` (`json` | `ascii`, default `json`).
- Parses via `ReadTools.ParseClip` (existing exception -> `ToolException` mapping). `json`
  serializes `BoxTreeMapper.Map(...)` to a `JsonObject` via the shared options (Component 6),
  so the MCP bytes equal the CLI `--json` bytes, not merely the same shape. `ascii` returns
  the shared renderer string.
- **Registration:** placed at the END of the read-tool block, immediately after
  `library_watching`, before the write tools. `Phase2ReadToolsTests.ToolsList_ContainsTheFullToolSurface`
  updated 17 -> 18 at that exact position. The FULL `clipmetamcp.Tests` project must run.

### 6. Serialization contract (shared, `clipmeta.core`)
One shared `JsonSerializerOptions` used by BOTH the CLI `--json`/`--definitions` and the MCP
tool:
- `PropertyNamingPolicy = CamelCase` (the DTO's C# PascalCase members serialize as `type`,
  `friendlyName`, `isClipmetaContainer`, etc.).
- `Converters = { new JsonStringEnumConverter() }` so `category` serializes as its string
  name, not an integer.
- `DefaultIgnoreCondition = WhenWritingNull` so an absent `description`/`displayValue`/
  `editableKey` is an omitted key, not `"description": null` (dumb-consumer friendly).
The MCP handler serializes through `JsonSerializer.SerializeToNode(dto, SharedOptions)`; the
CLI writes `JsonSerializer.Serialize(dto, SharedOptions)`. Same options -> identical bytes.

### 7. CLI flags (`clipmetaview`) and arg grammar
`AppRunner` currently treats `args[0]` as the path unconditionally. Add flag-aware parsing
with this decided grammar (tested per form):
- `clipmetaview <path.mp4>` -> ASCII tree + summary (default, unchanged).
- `clipmetaview <path.mp4> --json` and `clipmetaview --json <path.mp4>` -> `BoxTree` JSON.
  `--json` requires a path; missing/invalid path -> `ExitBadArgs`; unparseable MP4 ->
  `ExitParseError` with a stderr message and NO partial JSON on stdout.
- `clipmetaview --definitions` -> the static definitions dictionary; needs no path. Any extra
  args are ignored (definitions are clip-independent).
- `--json` and `--definitions` together -> `ExitBadArgs` (ambiguous request).
- Unknown flags -> `ExitBadArgs` with usage.
Parsing lives in `AppRunner` (thin shell); all shaping is in Core.

## Data shapes

`json` tree (abridged; note unquoted `displayValue`, no `contentOffset`, a
`hasReliableOffsets:false` Xtra node):
```
{
  "path": "C:\\clips\\clip.mp4",
  "fileSize": 12490234,
  "boxes": [
    { "type": "ftyp", "offset": 0, "size": 32, "headerSize": 8, "isFullBox": false,
      "version": 0, "flags": 0, "friendlyName": "File Type", "category": "Header",
      "displayValue": "isom", "isEditable": false, "wasClamped": false,
      "hasReliableOffsets": true, "isClipmetaContainer": false, "children": [] },
    { "type": "moov", "offset": 32, "size": 90210, "headerSize": 8, "friendlyName": "Movie",
      "category": "Structural", "hasReliableOffsets": true, "isClipmetaContainer": false,
      "children": [ /* nested; a clipmeta "----" child would have
        "category":"EditableMeta", "editableKey":"com.peckworkslab.clipmeta:game",
        "isClipmetaContainer": true */ ] },
    { "type": "WM/Category", "offset": 71234, "size": 44, "headerSize": 0,
      "friendlyName": "Tags", "category": "WindowsMedia", "displayValue": "montage",
      "hasReliableOffsets": false, "isClipmetaContainer": false, "children": [] }
  ]
}
```
Note the `mdat` box appears as a leaf (`"children": []`); consumers detect the
non-expanded-container case by `type == "mdat"`.

`--definitions` (static, clip-independent; camelCase; editable-aware category):
```
{
  "moov": { "friendlyName": "Movie", "category": "Structural",
            "description": "Root container for all structure and metadata." },
  "©nam": { "friendlyName": "Title", "category": "EditableMeta",
            "description": "iTunes title field." }
}
```

## Key decisions
- `isClipmetaContainer` marks the innermost clipmeta-namespaced `----` atoms (the intrinsic
  helper), not `udta`/`ilst`. The parser exposes each freeform atom as one `----` `BoxNode`
  whose `EditableKey` is `domain:field` (verified `Mp4Parser.cs:204-229`).
- `ascii` mode is byte-identical to `clipmetaview`'s non-color redirected output (tree +
  legend + summary), locked by a parity test. `json` is the lean path.
- One shared `JsonSerializerOptions` makes CLI and MCP JSON byte-identical.
- The write engine is not touched; only the mapper adopts the intrinsic predicate helper.

## Error handling
- MCP: nonexistent path, path outside the library, unparseable/refused file all return the
  structured `ToolException` the other read tools return (via `ParseClip` + `ResolveClipPath`).
- CLI: reuse `clipmetaview`'s exit-code convention (`ExitBadArgs`, `ExitParseError`). `--json`
  never emits a partial JSON document on a parse failure.

## Risks

| # | Risk | Mitigation |
|---|------|------------|
| R1 | Renderer move / invariant-culture change alters ASCII bytes | Snapshot the (invariant-culture) `clipmetaview` output for a fixture; assert byte-identical after the move. Parity test also guards CLI-vs-tool. |
| R2 | `isClipmetaContainer` helper drifts the reader/writer behavior | Helper shares ONLY the intrinsic check; reader and write-gate are not rerouted in a behavior-changing way. A test asserts the mapper's flag matches the reader on a canonical fixture; the write-gate's existing refusal tests remain green (regression guard). |
| R3 | Adding a tool without updating the surface test passes a filtered run | Run FULL `clipmetamcp.Tests`; update `ToolsList_ContainsTheFullToolSurface` (17 -> 18) at the pinned position. |
| R4 | STJ default naming/enum shape differs from the documented contract | One shared `JsonSerializerOptions` (camelCase + `JsonStringEnumConverter` + omit-null); a test asserts the wire shape (camelCase keys, string `category`, unquoted `displayValue`, omitted null keys) and that CLI `--json` bytes equal the tool's `json` bytes. |
| R5 | Console side effect from Core on the stdio server | Gate `Console.ResetColor()` behind `useColor`; a test renders to a `StringWriter` and asserts no stray output; existing stdout-purity tests remain green. |
| R6 | `mdat` / large trees | `mdat` never expanded (leaf); JSON is structure-only, bounded. |

## Testing (MSTest)
1. `json`: non-empty `boxes`; top-level includes expected atoms (`ftyp`, `moov`, `mdat`).
2. Every node has `type`, `offset`, `size`, `children`.
3. **Structural sanity, correctly scoped:** among TOP-LEVEL boxes that are
   `hasReliableOffsets && !wasClamped`, offsets are ascending and non-overlapping. Do NOT
   assert exact `fileSize` coverage (trailing padding is legal) and do NOT assert deep-tree
   tiling (Xtra `WM/*` and clamped boxes have approximate/synthetic geometry). Add a note:
   structural self-consistency does not prove completeness (a size-0-not-last box can
   truncate the parsed tree).
4. Tagged fixture: exactly the expected innermost `----` box(es) marked `isClipmetaContainer`.
5. Untagged fixture: no box marked.
6. `ascii` parity: tool `ascii` == the CLI's non-color output for the same fixture.
7. Bad path and path-outside-library return the structured error, not an unhandled exception.
8. Renderer-move snapshot parity (invariant culture): CLI ASCII unchanged after the move.
9. Mapper `isClipmetaContainer` matches `ClipMetaReader`'s view on a canonical fixture; the
   write-gate refusal tests stay green (predicate-share regression guard).
10. Wire-shape: camelCase keys, `category` as a string, `displayValue` unquoted, null
    `description`/`editableKey` omitted; CLI `--json` bytes == tool `json` bytes.
11. `--definitions`: dictionary covers the fixture's box types; each entry has
    `friendlyName` + `category` (+ `description` where defined); valid JSON.
12. Arg grammar: `<path> --json`, `--json <path>`, `--definitions` (no path),
    `--json`+`--definitions` (ExitBadArgs), unknown flag (ExitBadArgs), `--json` on an
    unparseable file (ExitParseError, no partial JSON).
13. `category` is editable-aware: an editable field reports `EditableMeta`, not `Unknown`.

Use pristine fixtures for structural checks; `MinimalMp4Builder` for tagged/untagged and
wire-shape cases (clip-less, CI-safe).

## Definition of Done
1. `dotnet build` 0 warnings / 0 errors.
2. `dotnet test` all suites green, including the FULL `clipmetamcp.Tests`.
3. Zero NuGet added.
4. ASCII parity locked (CLI unchanged modulo the documented invariant-culture normalization;
   tool `ascii` matches).
5. Write-engine behavior unchanged (its refusal tests green).
6. Public types documented; any gotcha recorded in `docs/PITFALLS.md`.
7. `clip_get_boxtree` callable after a hard Desktop restart (owner-side verification).
