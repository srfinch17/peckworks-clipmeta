# clipmetamcp — MCP Server Design Spec
**Date:** 2026-06-11
**Round:** 3 (write engine hardened and audited; this round delivers the MCP server)
**Author:** Peckworks Lab

---

## Problem Statement

The CLI tools work, but they serve people who open terminals. The target user for Round 3 is
someone who talks to Claude Desktop and says *"tag my last three clips as market-garden fails,
rating 4"* — and never sees a command line at all.

That requires a Model Context Protocol (MCP) server: a local program Claude Desktop launches
and talks to over stdio, exposing clipmeta's read/search/write operations as tools the model
can call.

Two non-negotiable constraints shape everything in this spec:

1. **Pure C#, zero external dependencies — including at install time.** The project's identity
   is one language, one runtime, no NuGet in production code. That extends to the user's
   machine: installing clipmetamcp must not require Node, Python, or a .NET runtime install.
   A user who can download a file and double-click it must be fully served.
2. **Bulletproof for ordinary users.** No JSON config editing, no PATH setup, no "open an
   elevated terminal." The failure mode of every step must be a clear message, never a silent
   half-install. And because the caller is now an LLM rather than a human typing a filename,
   the write tools get a *higher* safety floor than the CLI, not the same one.

**Verified groundwork (2026-06-11):** MCP's stdio transport is newline-delimited JSON-RPC 2.0 —
no framing, no length prefixes — trivially implementable on the BCL. Claude Desktop installs
local servers via one-click MCP Bundles (`.mcpb`), which officially support `"type": "binary"`
(compiled executables). A `.mcpb` is a zip archive containing the server plus a `manifest.json`
— buildable with `Compress-Archive`; even the packaging step needs no Node.

---

## Scope of This Round

**In scope:**
- New `clipmetamcp` console project in the solution; references `clipmeta.core` only
- Hand-rolled MCP protocol layer (stdio, JSON-RPC 2.0) on pure BCL — `System.Text.Json`
- Tools-only capability surface: read, search, and write tools mapping 1:1 onto Core
- LLM-grade write safety: backup-on-by-default, explicit confirmation for clear-all,
  library-root sandbox, single-flight write lock
- Self-contained single-file publish (`win-x64`) — one exe, runtime bundled
- `.mcpb` bundle packaging via a PowerShell script in `tools/` (zip + manifest, no npm)
- `--install` / `--uninstall` self-registration fallback for hosts without bundle support
- `--selftest` command: the server handshakes with itself and reports pass/fail
- `clipmetamcp.Tests` (MSTest) — protocol and tool coverage

**Out of scope this round:**
- HTTP / Streamable-HTTP transport (stdio only; local single-user)
- MCP `resources`, `prompts`, and sampling capabilities (tools only)
- Code signing and auto-update (recorded in the risk table; revisit before public distribution)
- macOS / Linux builds (architecture must not preclude them; only win-x64 is published)
- Batch write tools (one clip per write call; the model can loop)
- Voice input, GUI (later rounds)

---

## 1. Solution Structure

| Project | Purpose |
|---------|---------|
| `clipmetamcp` | Thin MCP shell: stdio loop, JSON-RPC dispatch, tool registry. **No business logic** — every tool delegates to Core, exactly as the CLIs do. |
| `clipmetamcp.Tests` | MSTest. Drives the server in-process over piped streams; no Claude required. |

Internal layout:

```
clipmetamcp/
  Program.cs           # arg parsing (--install/--uninstall/--selftest) or stdio serve loop
  Protocol/
    JsonRpc.cs         # request/response/error records, (de)serialization
    McpSession.cs      # initialize handshake, capability negotiation, dispatch loop
  Tools/
    ToolRegistry.cs    # name → (schema, handler) map; serves tools/list and tools/call
    ReadTools.cs       # clip_get_metadata, library_find, library_vocab, library_export …
    WriteTools.cs      # clip_set_fields, clip_append_field, clip_clear_fields, clip_clear_all
  Install/
    ClaudeConfigInstaller.cs   # --install/--uninstall: locate, back up, edit Claude config
  SelfTest.cs          # spawn self, run initialize + tools/list, report
tools/
  pack-mcpb.ps1        # dotnet publish + manifest + Compress-Archive → clipmeta.mcpb
```

The **CLIs-are-thin-shells rule applies unchanged**: `clipmetamcp` is a third thin shell over
the same Core. If a tool needs logic Core doesn't have, the logic goes into Core.

---

## 2. Protocol Layer

### Transport

stdio. Each JSON-RPC message is one line of UTF-8 JSON terminated by `\n`. The host (Claude
Desktop) spawns the exe and owns stdin/stdout.

**THE IRON RULE: stdout belongs to the protocol.** A single stray `Console.WriteLine` — a
debug print, an exception trace, a chatty library — corrupts the channel and produces the
exact "Failed to connect" symptom documented in the ESP32 MCP lessons. Therefore:

- All diagnostics go through `IClipMetaLogger` to a **file** (`%LOCALAPPDATA%\clipmeta\mcp.log`),
  or to **stderr** (which MCP hosts capture as server logs). Never stdout.
- `Console.Out` is wrapped once at startup; the protocol writer is the only code holding it.
- A test asserts that invoking every tool produces zero non-protocol bytes on stdout.

### Methods implemented

| Method | Behavior |
|--------|----------|
| `initialize` | Validate JSON-RPC shape; reply with our `protocolVersion`, `serverInfo`, and `capabilities: { tools: {} }`. Accept any client version; respond with the latest version we support (pinned constant, checked against the live spec at implementation time). |
| `notifications/initialized` | No-op acknowledgment (notification; no response). |
| `tools/list` | Returns the registry: name, description, JSON Schema for inputs. Descriptions are written for the *model* — they state preconditions ("path must be an existing .mp4 inside the configured library") so the model self-corrects instead of erroring. |
| `tools/call` | Dispatch by name. All Core exceptions are caught and returned as MCP tool errors (`isError: true` with a human-readable message) — never JSON-RPC protocol errors, never raw stack traces. |
| `ping` | Empty success response. |
| anything else | JSON-RPC `-32601` method-not-found. Unknown notifications are ignored silently. |

### Serialization

`System.Text.Json` (BCL, not NuGet). All protocol records are explicit DTO types with
`JsonSerializerOptions` configured once. **Decision:** if single-file trimming or future
NativeAOT is enabled, switch to source-generated `JsonSerializerContext` — reflection-based
serialization is the canonical trimming casualty (risk table, R4).

---

## 3. Tool Surface

Names are verbs the model can reason about; every tool maps directly onto an existing Core
operation that is already tested.

### Read tools

| Tool | Maps to | Parameters |
|------|---------|------------|
| `clip_get_metadata` | `ClipMetaReader` (CLI `--list`) | `path` |
| `clip_get_stats` | `StatsCommand` path (CLI `--stats`) | `path` |
| `library_find` | `ClipMetaFinder` (CLI `--find`) | `field`, `value` |
| `library_vocab` | `ClipMetaVocab` (CLI `--vocab`) | `field` |
| `library_export` | `ClipMetaExporter` (CLI `--export`) | `format` (json/csv) |
| `library_search_index` | `ClipMetaIndex`/`ClipMetaSearch` (CLI `--index`, `--index-search`) | `rebuild?`, `field?`, `value?` |

Read tools return structured JSON content (not preformatted console text) so the model can
reason over fields rather than re-parse a table.

### Write tools

| Tool | Maps to | Parameters | Safety |
|------|---------|------------|--------|
| `clip_set_fields` | `Mp4Writer` set | `path`, `fields` (map), `dry_run?`, `backup?` | backup **defaults true** |
| `clip_append_field` | `Mp4Writer` append | `path`, `field`, `value`, `dry_run?`, `backup?` | backup defaults true |
| `clip_clear_fields` | `Mp4Writer` delete | `path`, `fields` (array), `dry_run?`, `backup?` | backup defaults true |
| `clip_clear_all` | `Mp4Writer` ClearAll | `path`, `confirm` (**required `true`**), `backup?` | refuses unless `confirm: true`; backup defaults true |

Write-tool results echo what changed, the backup path written, and the write-engine log line —
so the model can report to the user truthfully rather than assuming success.

### Library-root sandbox

The `.mcpb` `user_config` block prompts the user **at install time** for their clips directory
(Claude Desktop renders this as a folder picker — no JSON editing). The server receives it as
an environment variable and **refuses any write whose resolved absolute path is outside that
root** (after `Path.GetFullPath`, rejecting traversal). Read tools are similarly scoped.
A model hallucinating `C:\Windows\system.mp4` gets a refusal, not an attempt.

If no root is configured (e.g. manual `--install` flow), write tools refuse with a message
explaining how to set `CLIPMETA_LIBRARY_ROOT`.

### Single-flight writes

One write at a time, enforced with a process-wide lock. The write engine's deny-writers file
lock already makes concurrent writes *safe* (one would refuse); the MCP layer makes them
*orderly* by serializing tool calls so the model never sees a spurious sharing-violation error.

---

## 4. Packaging & Distribution

### Publish

```
dotnet publish clipmetamcp -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

One `clipmetamcp.exe` (~40–70 MB), no .NET install required on the user's machine.
`PublishTrimmed` is **off** in v1 (risk R4); revisit with source-gen serialization.

### `.mcpb` bundle

A zip named `clipmeta.mcpb`:

```
clipmeta.mcpb
├── manifest.json
├── server/clipmetamcp.exe
└── icon.png
```

Manifest essentials (`manifest_version` 0.3 schema, verified 2026-06-11):

```json
{
  "manifest_version": "0.3",
  "name": "clipmeta",
  "version": "1.0.0",
  "description": "Read, search, and write game-clip metadata stored inside MP4 files.",
  "author": { "name": "Peckworks Lab" },
  "server": {
    "type": "binary",
    "entry_point": "server/clipmetamcp.exe",
    "mcp_config": {
      "command": "${__dirname}/server/clipmetamcp.exe",
      "args": [],
      "env": { "CLIPMETA_LIBRARY_ROOT": "${user_config.library_root}" }
    }
  },
  "user_config": {
    "library_root": {
      "type": "directory",
      "title": "Clips folder",
      "description": "Only files inside this folder can be read or tagged.",
      "required": true
    }
  },
  "compatibility": { "platforms": ["win32"] }
}
```

`tools/pack-mcpb.ps1` runs publish, stages the layout, and `Compress-Archive`s it. The
official `mcpb` npm CLI is **not** used — a bundle is just a zip, and the dev loop stays
Node-free like everything else. (`mcpb validate` may be run manually as an occasional sanity
check; it is not part of the build.)

### User install story (the whole point)

1. Download `clipmeta.mcpb`.
2. Claude Desktop → Settings → Extensions → **Advanced settings** → Extension Developer →
   **Install Extension…** → pick the file. (Corrected 2026-06-12: this spec originally said
   "drag onto Settings → Extensions" — there is no such drop target; see PITFALLS.)
3. Pick the clips folder when prompted. Done — no terminal, no JSON, no runtimes.

### Fallback: `--install`

For Claude Desktop without bundle support, or other MCP hosts using the same config shape:
`clipmetamcp.exe --install` locates `%APPDATA%\Claude\claude_desktop_config.json`, backs it up
(timestamped), inserts/updates the `mcpServers.clipmeta` entry with the exe's own absolute
path, and prints next steps. `--uninstall` reverses it. `--install --library-root <dir>` sets
the sandbox env var in the entry. Both refuse (with the backup intact) if the existing JSON
fails to parse.

### `--selftest`

Spawns its own exe, performs `initialize` → `tools/list` over real pipes, and prints a
pass/fail table. This turns the ESP32-era debugging checklist ("is it even spawning? is stdout
clean?") into a one-command diagnostic a support thread can ask any user to run.

---

## 5. Test Strategy

Same two-directory clip discipline as the rest of the repo (pristine/scratch).

**Protocol tests (in-process, no child process):** `McpSession` reads from any
`TextReader`/`TextWriter`, so tests drive it with `StringReader`/`StringWriter` pairs:
- initialize handshake shape; version negotiation; capability advertisement
- `tools/list` returns every registered tool with valid JSON Schema
- unknown method → `-32601`; malformed JSON → parse error, session survives
- **zero non-protocol bytes on stdout** across every tool invocation

**Tool tests:** each tool against scratch clips — happy path, missing file, non-MP4,
outside-library-root refusal, `clear_all` without `confirm` refusal, dry-run touches nothing,
backup file appears (default) and respects `backup: false`. Write tools verified with the
existing `MediaIntegrityScanner` (media byte-identical after MCP-driven writes).

**Install tests:** `ClaudeConfigInstaller` against fixture configs in a temp dir — fresh
config, existing-other-servers config (preserved verbatim), corrupt JSON (refusal + backup),
uninstall round-trip.

**Manual E2E gate (Definition of Done):** install the bundle in real Claude Desktop on this
machine, tag a real clip conversationally, verify with `clipmetascribe --list` and the
integrity scanner.

---

## 6. Risk Table

| # | Risk | Mitigation |
|---|------|------------|
| R1 | Stray stdout output corrupts the protocol channel ("Failed to connect", the classic MCP failure) | Iron rule §2; logger goes to file/stderr only; dedicated stdout-purity test; `--selftest` detects it in the field. |
| R2 | Claude Desktop on Windows spawn quirks (the global lessons: PATH stripping, cmd-wrapper workaround) | Those lessons hit `node` resolution via Claude Code's `.mcp.json`; a bundled absolute-path binary spawned by Claude Desktop is the easy case. Verify in the E2E gate **first**, before deep implementation; keep the cmd-wrapper trick documented as fallback. |
| R3 | MCP protocol version drift (spec revisions post-date this document) | Pin the newest supported `protocolVersion` at implementation time from the live spec; tolerate-and-respond negotiation per §2; protocol layer is ~3 methods, so updates are cheap. |
| R4 | Trimming/AOT breaks reflection-based `System.Text.Json` | Trimming off in v1; adopt source-generated serializer contexts before ever enabling it. |
| R5 | Unsigned exe → SmartScreen warning on download | Accepted for v1 (friends-and-family distribution); document the "More info → Run anyway" step; code signing is the gate for public distribution (future rounds). |
| R6 | LLM hallucinates paths / destructive intent | Library-root sandbox; backup default ON; `clear_all` requires explicit `confirm: true`; the write engine beneath already refuses everything unsafe (Round 2 audit). |
| R7 | Manifest schema (`manifest_version` 0.3) evolves | Bundle build is one script + one JSON file; `mcpb validate` run manually before each release. |
| R8 | Concurrent tool calls racing a write | Single-flight write lock in the server; Core's deny-writers file lock as the backstop. |

---

## 7. Definition of Done

1. `dotnet build` — 0 warnings, 0 errors, all projects including `clipmetamcp`.
2. `dotnet test` — all tests pass, including new `clipmetamcp.Tests` and the existing 319.
3. **Zero NuGet packages** in `clipmetamcp` (MSTest in the test project remains the sole repo-wide exception).
4. `tools/pack-mcpb.ps1` produces an installable `clipmeta.mcpb` from a clean checkout with no Node present.
5. Manual E2E gate passed: bundle installed in Claude Desktop, clip tagged conversationally, media verified byte-identical, `--selftest` green.
6. Public types documented; new gotchas appended to `docs/PITFALLS.md`; README gains an install section with screenshots-level clarity.

---

## 8. Future Rounds (recorded for continuity)

- Code signing + (maybe) winget/store distribution once signed
- macOS/linux publishes (`osx-arm64`, `linux-x64`) and multi-platform bundle
- MCP `prompts` capability (canned tagging workflows) and `resources` (expose the library index)
- Streamable HTTP transport if a remote/shared-library scenario ever materializes
- Round 4+: Web GUI, standalone `clipsearch`, MKV/MOV handlers (unchanged from prior spec)

---

## 9. Addendum — backup management tools (2026-06-12)

**Why:** The first agent consumer's field report exposed a real gap: write tools create a
`<clip>.mp4.bak-<timestamp>` sibling on every write (backup default ON, per §3), but **nothing
can see those backups** — no list, no restore, no prune. A bulk tagging session buries the
folder in multi-GB `.bak` copies the server itself can't manage, and the safety net (a backup
you can't restore) is only half a net. Decision: keep backup-default-ON, close the gap with
tools (chosen over flipping the default off or a silent retention sweep).

**Core (`ClipBackup`)** — centralizes the convention currently hard-coded in `WriteTools`:
- `MakeBackupPath(clipPath)` → `clip.mp4.bak-yyyyMMdd-HHmmss`. `WriteTools` switches to calling
  this so the writer and the backup tools can never disagree on the naming scheme.
- `TryGetClipForBackup(backupPath, out clipPath)` → recognizes the `.bak-<14-digit-stamp>`
  suffix and yields the clip it belongs to; rejects anything else.
- `ListBackups(directory, clipPath?)` → `(BackupPath, ClipPath, SizeBytes, TakenUtc)[]`, newest
  first, recursive; ignores files that don't match the convention.
- `Restore(backupPath, clipPath)` → **validate-then-swap**: the backup must parse as a complete
  MP4 (`Mp4Parser` + the writer's whole-file-accounting gate) before it replaces the clip via
  the same temp+`File.Replace` atomic path the writer uses. A corrupt backup is refused, the
  clip untouched. Restoring does NOT consume the backup (it stays on disk).

**MCP tools (3):**
- `library_list_backups` (optional `clip`) — list backups across the library or for one clip:
  owning clip, size, timestamp. Read-only; requires the configured library.
- `clip_restore_backup` (`backup`, `confirm:true`) — overwrites the live clip with a backup.
  Destructive (replaces current bytes), so it takes the same literal-`confirm:true` latch as
  `clip_clear_all`; the backup is validated as real MP4 before the swap. Sandbox: both the
  backup and the target clip must resolve inside the library (write-grade).
- `clip_prune_backups` (`clip`, `keep` default 0, `confirm:true`) — deletes a clip's backups,
  keeping the newest `keep`. The only tool that DELETES files, so: confirm latch, sandbox-
  contained, and it touches only files matching the `.bak-<stamp>` convention for that clip —
  never the clip itself, never a foreign `.bak`.

**Out of scope for this addendum:** auto-pruning on write (keep the default dumb and
predictable; let the agent/user prune deliberately), and unifying the CLI's `<clip>.bak`
(no-timestamp) convention with the MCP's timestamped one — the CLI keeps its own.
