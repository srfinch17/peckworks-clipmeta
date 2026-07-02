# Design: `clip_get_boxtree` MCP tool, `clipmetaview --json`, and box definitions

Date: 2026-07-02
Status: design, pending review
Feature branch: `feat/boxtree-tool`

## Problem

The MP4 box/atom hierarchy is parsed by shared Core code (`Mp4Parser.ParseFile`,
producing a `BoxNode` tree) on every metadata read. But the *structural view* of that
tree is reachable only through `clipmetaview`'s ASCII renderer, which has no MCP seam and
no machine-readable output. Two things are blocked:

1. Seeing the box tree from Claude Desktop (there is no MCP tool for it).
2. Scripting the tree, for example building a web-based tree view, from CLI output
   (the CLI only emits a human-readable ASCII tree, not structured data).

This is a gap-fill, not new parsing capability. The walker already runs; we are surfacing
its output as structured data and giving it an MCP seam.

## Architecture context (verified 2026-07-02, see `docs/architecture-audit.md`)

One shared engine: all three executables (`clipmetamcp`, `clipmetaview`, `clipmetascribe`)
reference only `clipmeta.core`. The box-walker `Mp4Parser` lives in Core and is called by
the MCP (`ReadTools.ParseClip`), both CLIs, and Core's own read/write layer. The one
genuinely CLI-exclusive piece is `clipmetaview/Rendering/TreeRenderer.cs` (ASCII
presentation). This design moves that renderer into Core so nothing is CLI-exclusive
afterward, and wires the new outputs to the same shared walker.

## Scope

### In
- New read-only MCP tool `clip_get_boxtree` (`render`: `json` | `ascii`).
- New CLI flag `clipmetaview <clip> --json`: the same structured tree as JSON.
- New CLI flag `clipmetaview --definitions`: a static box-type definitions dictionary
  (for tooltip/hover text in a scripted/web view).
- Move `TreeRenderer` from `clipmetaview` into `clipmeta.core` (shared, parity-locked).
- Consolidate the box-type reference (friendly name + category + description) into one
  Core source of truth; the ASCII legend renders from it instead of hardcoding the strings.

### Out
- No writing, editing, or backup. This tool and these flags are strictly read-only.
- No media-technical decoding (duration, codec, resolution, bitrate, frame rate). That is a
  separate feature with its own reconnaissance spec (`2026-07-02-media-technical-facts-recon.md`).
- No MCP tool for definitions: the MCP consumer is Claude, which already knows MP4 box
  semantics, so a definitions tool would be redundant surface. Definitions serve dumb
  consumers (web JS, scripts), so they live on the CLI only.
- No changes to the existing 17 tools or to `clipmetascribe`.
- No new NuGet packages (BCL only; `System.Text.Json` is BCL and is permitted).

## Components

### 1. Core DTO + mapper (`clipmeta.core`)
- `BoxTreeNode` (DTO): `type`, `offset`, `size`, `headerSize`, `contentOffset`,
  `isFullBox`, `version`, `flags`, `friendlyName`, `category`, `displayValue` (nullable),
  `isEditable`, `editableKey` (nullable), `wasClamped`, `hasReliableOffsets`,
  `isClipmetaContainer`, `children` (array, empty for leaves).
- `BoxTree` (root DTO): `path` (resolved absolute), `fileSize`, `boxes` (top-level nodes).
- `BoxTreeMapper.Map(BoxNode root, string resolvedPath, long fileSize) -> BoxTree`:
  pure transform, no IO. Sets `isClipmetaContainer` via a shared predicate (see below).

### 2. Shared clipmeta-container predicate
The check "does this node hold one of clipmeta's own fields" already exists implicitly in
`ClipMetaReader` (a `----` atom whose `EditableKey` starts with `ClipMetaSchema.Domain + ":"`)
and in the write-gate's `DetectNonCanonicalMetadata`. Factor it into ONE shared helper (e.g.
`ClipMetaSchema.IsClipmetaField(BoxNode)` or similar) and have the mapper, the reader, and
the write-gate all call it, so `isClipmetaContainer` can never disagree with what the reader
and writer consider clipmeta data.

### 3. Renderer move (`clipmetaview/Rendering/TreeRenderer.cs` -> `clipmeta.core`)
- Move the class into Core (new namespace, e.g. `ClipMetaCore.Rendering`). Its plain-text
  output (no console color; produced whenever the writer is not `Console.Out`) is the `ascii`
  parity target. Console coloring stays intact for direct CLI use (guarded by the existing
  `IsConsole` check; `System.Console` is BCL).
- `clipmetaview/AppRunner.cs` updates its `using`/call sites; no behavior change.

### 4. Box definitions (`clipmeta.core`)
- Extend the box-type reference (`MetadataKeys`, or a companion `BoxDefinitions`) so each
  known type resolves to `{ friendlyName, category, description? }`. Expose
  `GetDefinition(type)` and `AllDefinitions()`.
- The ASCII legend in `TreeRenderer` renders its descriptions FROM this data, removing the
  duplicated hardcoded legend strings (single source of truth).
- Backfill descriptions for known types that currently have a name but no description.
  Types with no description fall back to `{ friendlyName, category }` only.

### 5. MCP tool (`clipmetamcp`)
- New handler `clip_get_boxtree`. Params: `path` (string, required; same resolution as
  `clip_get_metadata`), `render` (`json` | `ascii`, default `json`).
- Parses via the existing `ReadTools.ParseClip` (which already converts Core exceptions into
  `ToolException` refusals). `json` -> `BoxTreeMapper.Map` -> serialized structured content.
  `ascii` -> shared renderer string.
- Registered in the tool list; `ToolsList_ContainsTheFullToolSurface` updated 17 -> 18 with
  the correct registration order. The FULL `clipmetamcp.Tests` project must run (surface
  assertion lives outside the diff).

### 6. CLI flags (`clipmetaview`)
- `--json`: serialize `BoxTree` (same DTO) via `System.Text.Json` to the `TextWriter`.
- `--definitions`: serialize `AllDefinitions()` via `System.Text.Json` (no clip needed).
- No flag: current ASCII tree + summary via the moved renderer (unchanged).
- Arg parsing stays in `AppRunner` (thin shell); all shaping logic is in Core.

## Data shapes

`json` tree (abridged):
```
{
  "path": "C:\\clips\\clip.mp4",
  "fileSize": 12490234,
  "boxes": [
    { "type": "ftyp", "offset": 0, "size": 32, "headerSize": 8, "contentOffset": 8,
      "isFullBox": false, "version": 0, "flags": 0, "friendlyName": "File Type",
      "category": "Header", "displayValue": "isom", "isEditable": false, "editableKey": null,
      "wasClamped": false, "hasReliableOffsets": true, "isClipmetaContainer": false,
      "children": [] },
    { "type": "moov", "offset": 32, "size": 90210, "friendlyName": "Movie",
      "category": "Structural", "children": [ /* nested */ ], "isClipmetaContainer": false, ... }
  ]
}
```

`--definitions` (static, clip-independent):
```
{
  "moov": { "friendlyName": "Movie", "category": "Structural",
            "description": "Root container for all structure and metadata." },
  "trak": { "friendlyName": "Track", "category": "Structural",
            "description": "One media stream: video, audio, timecode, or subtitle." }
  // ... every known type
}
```

## Key decisions

- **`isClipmetaContainer` marks the innermost `----` atoms** carrying clipmeta's own
  domain-prefixed fields, not the outer `udta`/`ilst`. (The handoff's JSON example marked
  `udta`, but its prose says "flag the innermost box"; prose wins, and it is the precise
  answer. A web renderer can highlight ancestor boxes itself.) No clipmeta metadata -> no box
  marked.
- **`ascii` mode is byte-identical to `clipmetaview`'s full current output** (tree + legend +
  metadata summary), so a parity test locks the CLI and the tool together. Callers wanting a
  lean payload use `json`.
- **`System.Text.Json` for CLI serialization** (BCL, correct escaping), not the hand-written
  JSON style `clipmetascribe --export` uses; the tree DTO is a clean object graph where a
  serializer is safer than hand-rolling. No trimming concern (`clipmetaview` is not trimmed).

## Error handling
- MCP: nonexistent path, path outside the library, unparseable/refused file all return the
  same structured `ToolException` the other read tools return (inherited via `ParseClip`).
- CLI: reuse `clipmetaview`'s existing exit-code convention (`ExitBadArgs` for missing file /
  bad args, `ExitParseError` for an unparseable MP4). `--json` on an unparseable file yields
  the same nonzero exit and a stderr message (not a half-written JSON document).

## Risks

| # | Risk | Mitigation |
|---|------|------------|
| R1 | Renderer move silently changes ASCII bytes | Snapshot current `clipmetaview` output for a fixture BEFORE the move; assert byte-identical after. The parity test also guards CLI-vs-tool. |
| R2 | Legend consolidation changes legend bytes | Same snapshot covers the legend (it is part of the ASCII output); assert unchanged. |
| R3 | `isClipmetaContainer` predicate drifts from reader/writer | One shared predicate helper called by mapper + reader + write-gate; a test asserts all agree on a fixture. |
| R4 | Adding a tool without updating the surface test passes a filtered run | Run the FULL `clipmetamcp.Tests`; update `ToolsList_ContainsTheFullToolSurface` (17 -> 18) and registration order. |
| R5 | `mdat` handling in JSON | The parser already does not recurse `mdat` (empty `children`); the mapper maps it as a leaf. JSON tree never expands raw media, so payload stays bounded. |
| R6 | Large/deep trees produce large JSON | Acceptable: structure only, no `mdat` expansion. Note it; no cap in v1. |
| R7 | `--json`/`--definitions` flag parsing collides with the positional path arg | Define precedence explicitly: `--definitions` needs no path; `--json` requires a path; document arg grammar and test each form. |

## Testing (MSTest)
1. `json`: non-empty `boxes`, top-level includes expected atoms (`ftyp`, `moov`, `mdat`).
2. Every node has `type`, `offset`, `size`, `children`.
3. Offsets monotonic; sizes consistent with `fileSize` (no overrun / no unexpected gap).
4. Tagged fixture: exactly the expected innermost `----` box(es) marked `isClipmetaContainer`.
5. Untagged fixture: no box marked.
6. `ascii` parity: tool `ascii` == current `clipmetaview` output for the same fixture.
7. Bad path and path-outside-library return the structured error, not an unhandled exception.
8. Renderer-move + legend-consolidation snapshot parity (R1/R2).
9. Shared-predicate agreement: mapper's `isClipmetaContainer` matches `ClipMetaReader`'s
   view on the same fixture (R3).
10. `--definitions`: returns a dictionary covering the fixture's box types; each entry has
    `friendlyName` + `category` (and `description` where defined); valid JSON.
11. CLI `--json` for a real clip parses as JSON and matches the tool's `json` shape.

Use existing pristine fixtures for structural checks; `MinimalMp4Builder` for the
tagged/untagged `isClipmetaContainer` cases (clip-less, CI-safe).

## Definition of Done
1. `dotnet build` 0 warnings / 0 errors.
2. `dotnet test` all suites green, including the FULL `clipmetamcp.Tests`.
3. Zero NuGet added.
4. ASCII parity locked (CLI unchanged; tool `ascii` matches).
5. Public types documented; any gotcha recorded in `docs/PITFALLS.md`.
6. `clip_get_boxtree` callable after a hard Desktop restart (owner-side verification).
