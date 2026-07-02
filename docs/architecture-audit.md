# Architecture audit: are the CLIs dead mirrors that drift?

Date: 2026-07-02 (against v1.0.1, commit on `main`).
Question asked: are `clipmetaview` / `clipmetascribe` disconnected from the running
MCP server and quietly drifting out of sync with the real code?

**Short answer: no.** The architecture is **#2, shared library.** There is exactly
one parse/read/write engine (`clipmeta.core`), and the MCP server plus both CLIs are
thin frontends over it. The engine cannot drift between the CLI and the server because
there is only one copy of it, and the MCP exercises it on every call.

## Which architecture (1 subprocess / 2 shared library / 3 divergent reimplementation)?

**#2, with hard evidence:**

- All three executables reference exactly one project and nothing else:
  - `clipmetamcp/clipmetamcp.csproj` -> `<ProjectReference Include="..\clipmeta.core\clipmeta.core.csproj" />`
  - `clipmetaview/clipmetaview.csproj` -> same single reference.
  - `clipmetascribe/clipmetascribe.csproj` -> same single reference.
  - No CLI references another CLI. There is no second copy of the parser, reader, or writer.

## Does the MCP server ever call the CLI binaries at runtime?

**No.** The only `Process.Start` / `ProcessStartInfo` in the server (or in Core) is in
`clipmetamcp/SelfTest.cs` (lines 93, 203, 218), and it spawns the **server's own exe**
for a stdio self-test. It never invokes `clipmetaview.exe` or `clipmetascribe.exe`.
A repo-wide grep for those binary names in production code returns nothing.

## Where does the box-walking logic live, and who calls it?

- Defined once: `clipmeta.core/Mp4/Mp4Parser.cs` (`Mp4Parser.ParseFile` / `ParseBoxes`),
  producing a `BoxNode` tree (`clipmeta.core/Mp4/BoxNode.cs`).
- Callers (production):
  - MCP server: `clipmetamcp/Tools/ReadTools.cs:804`, inside the shared `ParseClip`
    helper (`ReadTools.cs:800`) that every MCP metadata read funnels through.
  - clipmetascribe: `Commands/ListCommand.cs:21`, `Commands/StatsCommand.cs:18`,
    `Commands/CopyTagsCommand.cs:41`, `Program.cs:530`.
  - clipmetaview: `AppRunner.cs:58`.
  - Core internals: `ClipMetaReader`, `ClipMetaExporter`, `ClipMetaFinder`,
    `ClipMetaIndex`, `ClipMetaVocab`, `ClipMetaCopier`, `ClipBackup`, and the writer's
    post-write verify (`Mp4Writer.cs:271`).

The walker is shared by the MCP, both CLIs, and Core's own read/write layer. It is the
most-exercised code in the product.

## Where does read/write logic live, and who calls it?

- Reads: `clipmeta.core/Read/*` (`ClipMetaReader`, `ClipMetaFinder`, `ClipMetaVocab`,
  `ClipMetaExporter`, `ClipMetaIndex`, `ClipMetaSearch`, `ClipMetaCopier`).
- Writes: `clipmeta.core/Write/*` (`Mp4Writer`, `FreeformAtomWriter`, `Normalizer`,
  `ClipBackup`, `WriteGate`, `CrossProcessLock`).
- Callers: MCP tools (`clipmetamcp/Tools/ReadTools.cs`, `WriteTools.cs`, `QueueTools.cs`)
  and CLI command classes (`clipmetascribe/Commands/*`, `Program.cs`) both delegate in.
  One implementation, two frontends.

## Shared assembly and its consumers

- Shared assembly: **`clipmeta.core`** (namespace `ClipMetaCore`), zero external NuGet deps.
- Consumers: `clipmetamcp`, `clipmetascribe`, `clipmetaview`. (`Directory.Build.props`
  stamps all of them from the one `VERSION` file, so they also share a version.)

## Are the CLIs at risk of drift?

**The parse/read/write engine: no, structurally impossible.** One copy in `clipmeta.core`,
imported by all three exes, run by the MCP on every request.

**The one genuinely CLI-exclusive production code is the ASCII tree renderer**:
`clipmetaview/Rendering/TreeRenderer.cs`, called only by `clipmetaview/AppRunner.cs:59-60`.
This is *presentation* (turning a parsed `BoxNode` tree into text), not parsing. It has no
MCP seam. That is the real, narrow gap: the box tree is parsed by shared code that the MCP
already runs, but its *rendering* was never exposed as a tool. This is a missing surface on a
healthy shared engine, not divergence. It is also not untested (see below).

## Test coverage

- `clipmetaview.Tests` (109, incl. 3 clip-less skips): covers the CLI directly, including
  `TreeRendererTests` for the ASCII renderer.
- `clipmetascribe.Tests` (544): covers the scribe CLI and, through it and directly, the Core
  read/write engine, including real-clip integration and byte-level media-integrity.
- `clipmetamcp.Tests` (137): covers the MCP tool surface, protocol shape, sandbox, stdout purity.
- The shared Core engine is exercised by all three suites. No shipped-but-untested production
  path was found in the CLI/parser area. (The one documented CI coverage gap is real-clip media
  integrity, which runs locally pre-release; unrelated to CLI drift. See CLAUDE.md Build & test.)

## Implication for the `clip_get_boxtree` build

Architecture is #2, so the build is a wire-up, not a consolidation of parsing: point the new
tool at the same `Mp4Parser.ParseFile` the MCP already calls via `ReadTools.ParseClip`, and emit
the `BoxNode` tree as JSON. The only consolidation the feature implies is for `ascii` parity:
to return output byte-identical to `clipmetaview` without forking, the render logic in
`clipmetaview/Rendering/TreeRenderer.cs` should move into `clipmeta.core` so both `clipmetaview`
and the new tool call one implementation. That makes `clipmetaview` even thinner and closes the
last CLI-exclusive production code path.
