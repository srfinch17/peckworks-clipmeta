# peckworks-clipmeta

**🌐 Project site: https://srfinch17.github.io/peckworks-clipmeta/** — what it is, how voice-tagging works, and the full gaming-vs-review decision trees.

**⬇ Download (Windows 10/11, 64-bit): [latest release](https://github.com/srfinch17/peckworks-clipmeta/releases/latest)** — `clipmeta.mcpb` for one-click install into Claude Desktop, plus optional command-line tools. Self-contained; no .NET install needed.

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
clipmetascribe "C:\clips\" --watching
```

`--watching` resolves the clip currently open in a media player. Supported players (extensible): MPC-HC (title is the full path) and VLC (title is the bare file name). Resolve-only — use `--set` with the returned path to tag. If a player is open on a file outside the configured library, the tool returns a wrong-directory warning and does not guess a candidate. VLC bare-name matches (where only the file name is available) are confirmed via the file lock — a match is high-confidence only when the library file is locked by the player.

Run with no arguments for full usage, including `--dry-run`, `--backup`, and `--log`.

### clipmetamcp
An MCP (Model Context Protocol) server exposing the clipmeta tools to MCP hosts such as Claude Desktop, so you can read, search, and **tag clips conversationally** ("tag that last clip with game TF2 and rating 5"). Pure C#, zero dependencies, self-contained — the person installing it needs no .NET, Node, Python, terminal, or JSON editing.

**Seventeen tools.** Reads: `clip_get_metadata` (everything about one clip in one call), `library_list` (find clips by file name), `library_find` / `library_search_index` (find clips by metadata), `library_vocab` (every value used for a field), `library_export` (the whole library as JSON or CSV), `library_watching` (resolve the clip currently open in a media player — returns ranked candidates; supported players are extensible, with MPC-HC supplying the full path in the title and VLC the bare file name; if a player is open on a file outside the configured library the tool returns a wrong-directory warning and does not guess, and VLC bare-name matches are confirmed via the file lock; also drains any queued tags whose clips have since been freed). Writes: `clip_set_fields`, `clip_append_field`, `clip_clear_fields`, `clip_clear_all` — every write keeps a timestamped backup next to the file unless told otherwise, supports dry-run previews, and `clip_clear_all` refuses without an explicit confirmation argument. Deferred-tag queue: `library_queue_tag` (queue a tag for a clip that is currently locked by a player — written automatically when the lock clears), `library_flush_queue` (write all queued tags whose clips are no longer locked), `library_queue_status` (list pending queued tags, their fields, and lock state). Backups: `library_list_backups` (see what backups exist), `clip_restore_backup` (roll a clip back to a backup — validated as a real MP4 first, confirmation required), `clip_prune_backups` (clean up old backups, confirmation required). All file access is sandboxed to the clips folder you pick at install time; nothing outside it can be read or written.

#### Installing in Claude Desktop

1. **Download `clipmeta.mcpb`** from the [latest release](https://github.com/srfinch17/peckworks-clipmeta/releases/latest). (Developers can build it instead with `tools/pack-mcpb.ps1` → `dist/clipmeta.mcpb` and `dist/clipmeta-unpacked/`.)
2. In Claude Desktop: **Settings → Extensions → Advanced settings** → under *Extension Developer*, click **Install Extension…** and pick `clipmeta.mcpb`.
3. When prompted, pick your **clips folder** — this becomes the sandbox. (You can change it later on the extension's settings card.)
4. Open a new conversation and ask Claude about your clips.

Known wrinkles:
- **Microsoft Store build of Claude Desktop:** the packed `.mcpb` install silently does nothing (upstream bug — details in `docs/PITFALLS.md`). Use **Install Unpacked Extension** and select the `dist/clipmeta-unpacked/` folder instead. Updating later = reinstall over the top, same button.
- **SmartScreen:** the executable is not code-signed, so Windows may warn "unknown publisher" on first run — "More info → Run anyway". (Real code signing needs a paid certificate.)

#### If something misbehaves

`clipmetamcp.exe --selftest` (in `dist/clipmeta-unpacked/server/`) spawns the server exactly the way Claude Desktop does and prints an 11-point pass/fail table — handshake, a real tool round-trip, stdout purity, clean shutdown. That output is the first thing to look at (or send) when the extension won't connect. Server-side details land in `%LOCALAPPDATA%\clipmeta\mcp.log`.

#### Manual fallback for other setups

`clipmetamcp.exe --install --library-root "C:\path\to\clips"` writes the server entry directly into `claude_desktop_config.json` (it finds the right config, including the Store build's virtualized one; `--config <path>` overrides). It backs up the config first, refuses to touch one it can't parse, and leaves your other MCP servers exactly as they were. `--uninstall` reverses it.

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
