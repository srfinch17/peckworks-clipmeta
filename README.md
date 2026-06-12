# peckworks-clipmeta

A suite of C# tools for reading and writing metadata **inside** MP4 files, so tags travel with the file. Zero external dependencies in production code — pure .NET BCL only.

Custom fields live in the `com.peckworkslab.clipmeta` reverse-domain namespace inside standard MP4 `----` freeform atoms. Well-known fields: `game`, `players`, `tags`, `timecode`, `rating`, `notes` — plus arbitrary custom names. Multi-value fields are pipe-delimited.

## Tools

### clipmetaview
Displays the internal box/atom structure of an MP4 file as a human-readable tree. Editable metadata fields are marked.

```
clipmetaview "video.mp4"
```

### clipmetascribe
Reads, writes, and searches MP4 metadata.

```
clipmetascribe "video.mp4" --list
clipmetascribe "video.mp4" --stats
clipmetascribe "video.mp4" --set game "Team Fortress 2"
clipmetascribe "video.mp4" --append tags "competitive|payload"
clipmetascribe "video.mp4" --clear tags
clipmetascribe "video.mp4" --clear-all --yes
clipmetascribe "C:\clips\" --find game "TF2"
clipmetascribe "C:\clips\" --vocab tags
clipmetascribe "C:\clips\" --export --format csv --output library.csv
clipmetascribe "C:\clips\" --index
clipmetascribe "C:\clips\" --index-search tags "competitive"
```

Run with no arguments for full usage, including `--dry-run`, `--backup`, and `--log`.

### clipmetamcp
An MCP (Model Context Protocol) server exposing the clipmeta tools to MCP hosts such as Claude Desktop, so you can read and tag clips conversationally. Ships as a self-contained `.mcpb` bundle (built by `tools/pack-mcpb.ps1`); no .NET install needed on the target machine. Install in Claude Desktop via Settings → Extensions → **Advanced settings** → Extension Developer → **Install Extension…**, pick the `.mcpb` file, then choose your clips folder when prompted. On the **Microsoft Store build** of Claude Desktop the packed install silently fails (upstream bug, see `docs/PITFALLS.md`) — use **Install Unpacked Extension** on the `dist/clipmeta-unpacked/` folder instead. All file access is sandboxed to the chosen folder.

**Status: in development.** Phase 1 (protocol layer + `clip_get_metadata`) is built; the remaining read/write tools are planned — see `docs/superpowers/plans/2026-06-11-clipmetamcp-server.md`.

## Safety model

The write engine never opens the source file for writing. Mutations go to a temp file, are verified by re-parse (including byte-level media-integrity checks: the parse must account for the whole file, the rebuilt `moov` must match its predicted size, chunk-offset tables are cross-checked), then swapped in atomically with `File.Replace`. Files the parser can't fully account for are refused for writing, never silently truncated. See `docs/PITFALLS.md` for the bugs this design guards against.

## Structure

| Project | Purpose |
|---------|---------|
| `clipmeta.core` | Shared library: MP4 parser, reader, writer, schema, search/index, logging |
| `clipmetaview` | Tree viewer CLI |
| `clipmetascribe` | Read/write/search CLI |
| `clipmetamcp` | MCP server (stdio JSON-RPC 2.0) |
| `clipmetaview.Tests` | MSTest |
| `clipmetascribe.Tests` | MSTest, incl. real-clip integration and media-integrity tests |
| `clipmetamcp.Tests` | MSTest: protocol shape, sandbox, stdout purity |

## Build & test

```
dotnet build  --nologo -v q
dotnet test   --nologo --no-build -v q
```

Requirements:

- .NET 10 SDK
- Real `.mp4` files in `testclips/pristine/` for integration tests (not included in repo; `testclips/scratch/` is regenerated)
- A registered NuGet source for the MSTest packages (fresh machines: `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`)

The `clipmetascribe.Tests` suite hashes real clips and takes a few minutes — that's normal.
