# CLAUDE.md — peckworks-clipmeta

A suite of C# command-line tools for reading and writing metadata **inside** MP4 files, so tags travel with the file. Zero external dependencies in production code — pure .NET BCL only.

> **History:** This project began as a single tree-viewer built via a 5-role "orchestrator" exercise. That original brief is archived at `docs/archive/CLAUDE-orchestrator-original.md` for reference. It no longer describes the project — trust this file, the code, and `docs/superpowers/`.

---

## Architecture (as of 2026-06)

Solution: `peckworks-clipmeta.slnx`, **.NET 10**, seven projects:

| Project | Namespace | Purpose |
|---------|-----------|---------|
| `clipmeta.core` | `ClipMetaCore` | All business logic: MP4 parse/read/write, schema, search/index, logging. Zero NuGet deps. |
| `clipmetaview` | `ClipMetaView` | Thin CLI: renders the box/atom tree. References Core. |
| `clipmetascribe` | `ClipMetaScribe` | Thin CLI: read/write/search metadata (8 commands). References Core. |
| `clipmetamcp` | `ClipMetaMcp` | Thin MCP server shell: stdio JSON-RPC 2.0, exposes clipmeta tools to MCP hosts (Claude Desktop). References Core. Packs to a `.mcpb` bundle via `tools/pack-mcpb.ps1`. |
| `clipmetaview.Tests` | — | MSTest, 80 tests. |
| `clipmetascribe.Tests` | — | MSTest, 239 tests (incl. real-clip integration and byte-level media-integrity tests). |
| `clipmetamcp.Tests` | — | MSTest, 41 tests (protocol shape, tool behavior, sandbox escapes, stdout purity). |

`clipmeta.core` layout: `Abstractions/` (`IMediaParser`, `IMediaWriter`, `IClipMetaLogger`, `MediaHandlerRegistry`), `Mp4/`, `Write/`, `Read/`, `Schema/`, `Logging/`, `Exceptions/`.

> Note: `clipmetascribe` is the tool the old brief called "clipmetaedit." There is no separate clipmetaedit.

---

## How we work here

### Planning — spec before code
Non-trivial features get a dated spec and/or plan under `docs/superpowers/` **before** implementation:
- `docs/superpowers/specs/` — design specs (problem, scope in/out, architecture, risk table, definition of done).
- `docs/superpowers/plans/` — per-feature implementation plans.

The write-engine design spec (`docs/superpowers/specs/2026-05-21-clipmeta-core-write-engine-design.md`) is the gold-standard template. Match its format.

### Mistakes — write them down
When we hit and fix a real bug or a non-obvious gotcha, append it to **`docs/PITFALLS.md`**. Consult that file before touching the parser or writer.

### Memory
Persistent project memory lives in the Claude memory store (indexed in `MEMORY.md` there). Capture durable, non-obvious facts; don't duplicate what the code or these docs already say.

---

## Code conventions (non-negotiable)

- **Zero external NuGet packages in production code** (`clipmeta.core`, both CLIs). BCL/SDK only. Test projects may use MSTest — the sole exception.
- **CLIs are thin shells.** `Program.cs` parses args and delegates to a command class or Core. No business logic in a CLI.
- **SOLID / open for extension.** New formats implement `IMediaParser`/`IMediaWriter` and register with `MediaHandlerRegistry` — no edits to existing code. Don't `sealed` types a future format or editor might extend.
- **Big-endian everywhere** for MP4 IO — go through `BigEndianReader`/`BigEndianWriter`, never raw `BinaryReader.ReadInt32()` in parse/write code.
- **Never load `mdat` into memory**; stream-copy. The source file is **never opened for writing** — mutations go to a temp file, verified by re-parse, then `File.Replace`.
- XML doc comments on all public types/methods. Named constants, no magic numbers.
- `BoxNode` keeps its name until a second media format actually earns a generic abstraction.

---

## Build & test

From the solution root:

```
dotnet build  --nologo -v q          # must be 0 warnings, 0 errors
dotnet test   --nologo --no-build -v q
```

- 360 tests total. `clipmetascribe.Tests` takes ~3–4 min (real-clip integration + media-integrity hashing) — not a hang; use a long timeout.
- Integration tests need local clips: `testclips/pristine/` (read-only ground truth) and `testclips/scratch/` (regenerated copies). Both are git-ignored.
- **New machine?** If restore fails with `NU1100`, the machine likely has no NuGet source. Run:
  `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`

---

## Metadata model

Custom fields use the reverse-domain namespace `com.peckworkslab.clipmeta`, stored in MP4 `----` freeform atoms, multi-values pipe-delimited. Well-known fields: `game`, `players`, `tags`, `timecode`, `rating`, `notes` (plus arbitrary custom names). Full schema and write semantics are in the write-engine design spec.

## Definition of Done (every change)

1. `dotnet build` — 0 warnings, 0 errors, all projects.
2. `dotnet test` — all 360 pass, including real-clip integration and media-integrity tests.
3. Zero NuGet packages added to production projects.
4. Public types documented; new gotchas recorded in `docs/PITFALLS.md`.
