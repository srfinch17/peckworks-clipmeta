# clipmetamcp Implementation Plan

> **Spec:** `docs/superpowers/specs/2026-06-11-clipmetamcp-server-design.md`, read it first; this
> plan sequences that design, it does not restate it.

**Goal:** Deliver the `clipmetamcp` MCP server in five phases, front-loading the riskiest unknown
(does Claude Desktop cleanly spawn a bundled win-x64 binary and keep the stdio channel intact?)
into a walking-skeleton spike before the full tool surface is built.

**Tech stack:** C# / .NET 10, MSTest, **zero external NuGet packages** (System.Text.Json is BCL).
Self-contained single-file publish; `.mcpb` packaging via PowerShell only.

---

## Decisions pinned at implementation time

These resolve the spec's "check the live spec when implementing" items (risks R3/R4):

- **`protocolVersion`: `2025-11-25`**, verified current at modelcontextprotocol.io on 2026-06-11.
  Negotiation rule (per spec): if the client's requested version is one we support, echo it back;
  otherwise respond with our latest. Supported set is a constant in `McpSession`:
  `["2025-11-25", "2025-06-18", "2025-03-26"]`, our surface (lifecycle + tools) is unchanged
  across these revisions.
- **`tools/call` results** carry both a `content: [{type:"text", text:<json>}]` block (universal
  compatibility) and `structuredContent` (so the model gets real JSON, per spec §3). We do **not**
  declare `outputSchema` in v1, declaring it makes `structuredContent` mandatory and adds schema
  maintenance for no negotiation benefit.
- **Serialization:** reflection-based `System.Text.Json` writing to/from explicit DTO records.
  `PublishTrimmed` stays **off** (risk R4); the csproj carries a comment forbidding it until
  source-generated `JsonSerializerContext` is adopted.
- **Request `id`** is `number | string` in JSON-RPC 2.0, stored as a cloned `JsonElement` and
  echoed back verbatim, never re-typed.

## Phase ordering and why

| Phase | Delivers | Retires risk |
|-------|----------|--------------|
| 1 | Walking skeleton: protocol layer, **one** read tool, `--selftest`, publish + `.mcpb` pack scripts, tests | R1 (stdout purity), R2 (Windows spawn), R7 (manifest schema), the E2E spike gate |
| 2 | Remaining 5 read tools |, (low risk; same pattern ×5) |
| 3 | 4 write tools + single-flight lock + integrity verification | R6, R8 |
| 4 | `--install` / `--uninstall`, full `--selftest` table |, |
| 5 | README install section, PITFALLS entries, full manual E2E gate | Definition of Done items 5–6 |

**Phase 1 ends with a manual go/no-go gate:** install the freshly packed `clipmeta.mcpb` in real
Claude Desktop on this machine and ask Claude for a clip's metadata. Nothing in phases 2–5 starts
until that works, if the binary-bundle bet fails, the fallback (cmd-wrapper, `--install` flow)
gets designed *before* ten tools exist, not after.

---

## Codebase context (verified 2026-06-11)

Core already provides everything the tools delegate to, no Core changes expected in phases 1–2:

- `Mp4Parser.ParseFile(path)` → `BoxNode`; throws `IOException` / `UnauthorizedAccessException` /
  `InvalidDataException` on bad input
- `ClipMetaReader.GetFields(root)` → `IReadOnlyList<(string Field, string Value)>`
- `ClipMetaFinder.Find(dir, field, value, recursive)`, `ClipMetaVocab.Enumerate(dir, field, recursive)`,
  `ClipMetaExporter.GetRecords(paths)`, `ClipMetaIndex.Build/WriteToFile/ReadFromFile`,
  `ClipMetaSearch.Find(index, field, value)`
- `Mp4Writer.WriteMetadata(path, MetadataMutation, IClipMetaLogger)`, `MetadataMutation` already
  carries `SetFields`/`AppendFields`/`DeleteFields`/`ClearAll`/`DryRun`/`BackupPath`, so the MCP
  write tools are parameter mapping, not new write logic
- `ClipMetaSchema`, field constants, `AtomName(field)`, internal `Schema` field to exclude from output
- `FileLogger(path, level)`, rotating file logger, creates its directory
- Test pattern: `TestClipsLocator` walks up from `AppContext.BaseDirectory` to find
  `testclips/pristine`; tests copy a pristine clip into a `_tempDir` per test

---

## File map (phase 1)

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `clipmetamcp/clipmetamcp.csproj` | net10.0 exe, refs clipmeta.core only, trimming-forbidden comment |
| Create | `clipmetamcp/Program.cs` | arg dispatch (`--selftest` vs serve); stdout lockdown; logger setup |
| Create | `clipmetamcp/Protocol/JsonRpc.cs` | message parse + response/error DTOs and writer |
| Create | `clipmetamcp/Protocol/McpSession.cs` | dispatch loop over `TextReader`/`TextWriter`; initialize/tools/list/tools/call/ping |
| Create | `clipmetamcp/Tools/ToolRegistry.cs` | name → (description, input schema, handler); `ToolException` for friendly refusals |
| Create | `clipmetamcp/Tools/LibrarySandbox.cs` | `CLIPMETA_LIBRARY_ROOT` containment checks |
| Create | `clipmetamcp/Tools/ReadTools.cs` | `clip_get_metadata` (phase 1); rest in phase 2 |
| Create | `clipmetamcp/SelfTest.cs` | spawn own exe, real-pipe handshake, pass/fail report |
| Create | `clipmetamcp.Tests/*` | protocol, tool, sandbox, stdout-purity tests |
| Create | `tools/pack-mcpb.ps1` | publish + stage + `Compress-Archive` → `dist/clipmeta.mcpb` |
| Create | `tools/mcpb-manifest.json` | manifest_version 0.3, binary entry point, `library_root` user_config |
| Modify | `peckworks-clipmeta.slnx` | add both projects |

---

## Phase 1, walking skeleton + spike gate

- [ ] **Task 1: projects + slnx wiring**, `clipmetamcp` (console, refs Core) and
  `clipmetamcp.Tests` (MSTest, refs clipmetamcp). Build green before any code.
- [ ] **Task 2: protocol layer.** `JsonRpc.cs`: parse one line into
  `(JsonElement? id, string? method, JsonElement? params)`; writers for result / error / nothing
  (notifications). `McpSession.cs`: constructor takes `TextReader in, TextWriter out, ToolRegistry,
  IClipMetaLogger`; `Run()` loops until EOF. Behavior table is spec §2 verbatim. Malformed JSON →
  `-32700`, session survives. Unknown method with id → `-32601`; unknown notification → ignored.
- [ ] **Task 3: tool registry + sandbox + first tool.** `ToolRegistry` maps name →
  (description-for-the-model, JSON Schema as a `JsonElement`, `Func<JsonElement, JsonNode>` handler).
  Handlers throw `ToolException(message)` for refusals; `McpSession` converts any handler exception
  into `isError: true` text, never a protocol error, never a stack trace.
  `LibrarySandbox.ResolveReadPath(path)`: `Path.GetFullPath`, must be under root when root is set,
  must exist, must be `.mp4`. `clip_get_metadata` → ParseFile + GetFields, schema field excluded,
  returns `{ path, fields: { name: value, ... } }`.
- [ ] **Task 4: Program.cs + stdout lockdown.** Serve mode: capture
  `Console.OpenStandardOutput()` into the one protocol `StreamWriter` (UTF-8 **without BOM**, a
  BOM is a protocol-corrupting stray byte, `AutoFlush = true`), then `Console.SetOut(TextWriter.Null)`
  so any stray `Console.WriteLine` anywhere vanishes instead of corrupting the channel.
  `FileLogger` → `%LOCALAPPDATA%\clipmeta\mcp.log`. Fatal startup errors → stderr + log, exit 1.
- [ ] **Task 5: minimal `--selftest`.** Spawn `Environment.ProcessPath` with no args; drive
  initialize → initialized → tools/list → ping over real pipes; print a pass/fail table to the
  console (selftest mode owns stdout, it is the human-facing mode). Non-zero exit on failure.
- [ ] **Task 6: tests.** In-process via `StringReader`/`StringWriter`:
  initialize shape + version negotiation (exact echo for supported, latest for unknown);
  tools/list contains `clip_get_metadata` with schema; `-32601`; `-32700` then session survives;
  **stdout purity**, invoke every registered tool with `Console.Out` captured, assert zero bytes;
  tool happy path against a scratch-copied pristine clip; missing file / non-mp4 / outside-root →
  `isError: true` with helpful message, schema field never present in results.
- [ ] **Task 7: packaging.** `tools/pack-mcpb.ps1`: `dotnet publish -c Release -r win-x64
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`,
  stage `manifest.json` + `server/clipmetamcp.exe`, `Compress-Archive`, rename to
  `dist/clipmeta.mcpb`. Manifest exactly as spec §4 (no icon in v1, optional in the 0.3 schema).
- [ ] **Task 8: local verification.** Full build 0/0, full test suite green, pack script runs
  from clean checkout, `--selftest` green **on the published single-file exe** (not just the
  framework-dependent build, single-file extraction is its own failure surface).
- [x] **Task 9 (manual, user): E2E spike gate.** Install `dist/clipmeta.mcpb` in Claude Desktop,
  pick the clips folder, ask for a clip's metadata. Record outcome (and any workaround needed)
  in `docs/PITFALLS.md` before phase 2.
  **PASSED 2026-06-12 (R2 retired), with one workaround:** the Microsoft Store build of Claude
  Desktop silently fails to install packed `.mcpb` files (upstream bug, routed to the unpacked
  installer); installing the extracted `dist/clipmeta-unpacked/` folder via **Install Unpacked
  Extension** worked first try. Binary spawn, handshake (2025-11-25), folder-picker sandbox,
  and a real tagged-clip `clip_get_metadata` round-trip all verified in the live app. Details
  in PITFALLS 2026-06-12.

## Phase 2, read tools

**Completed 2026-06-12** (all items; `library_list` added by user request after the E2E gate, 
he asked Claude to "show me the list of files in the directory" and no tool could: file
*discovery* by name is a different need than metadata *search*).

- [x] `clip_get_stats`, `ClipMetaStats.Categorize` over `GetUserFields` + `FileInfo`,
  structured JSON (`sizeBytes`, `fieldsSet`, `knownUnset`, `customFields`).
- [x] **`library_list` (added 2026-06-12)** (`subfolder?`, `pattern?`, `recursive?`, `limit?`) →
  new Core `ClipMetaLibrary.ListClips`: name-only wildcard match, newest first, no parsing
  (listing N clips costs N stat calls, not N MP4 parses). Capped (default 200, max 1000) with
  an explicit `truncated` flag so the model knows to narrow. `subfolder` goes through the same
  canonical containment check as clip paths (`LibrarySandbox.ResolveLibraryDirectory`).
- [x] `library_find` (`field`, `value`) → `ClipMetaFinder.Find` over the library root.
- [x] `library_vocab` (`field`) → `ClipMetaVocab.Enumerate`; counts ordered most-used-first.
- [x] `library_export` (`format`: json|csv, `subfolder?`) → `ClipMetaExporter.GetRecords`; json
  returns the records as structured content, csv returns the CSV text, via the new Core
  `ClipMetaExporter.WriteCsv` (hoisted from the CLI's ExportCommand so both emit identical CSV).
- [x] `library_search_index` (`rebuild?`, `field?`, `value?`) → `ClipMetaIndex` / `ClipMetaSearch`;
  corrupt/unreadable index self-heals by rebuilding (it's a cache, never the source of truth).
- [x] All directory-scoped tools operate on the sandbox root and **require** it to be configured
  (refusal with the `CLIPMETA_LIBRARY_ROOT` explanation otherwise), `LibrarySandbox.RequireRoot`.
- [x] Tests per tool: happy path, empty library, unset root refusal (+ subfolder traversal
  probe, CSV byte-equality vs the CLI, corrupt-index self-heal, truncation flag). Suite 385.

## Phase 3, write tools

**Completed 2026-06-12.**

- [x] `WriteTools.cs`: `clip_set_fields`, `clip_append_field`, `clip_clear_fields`,
  `clip_clear_all`, each builds a `MetadataMutation` (field names through
  `ClipMetaSchema.AtomName`) and calls `Mp4Writer.WriteMetadata`.
- [x] Safety semantics (spec §3): `backup` defaults **true** (timestamped sibling
  `<name>.mp4.bak-yyyyMMdd-HHmmss`); `dry_run` honored; `clip_clear_all` refuses without literal
  boolean `confirm: true` (the string `"true"` refuses too, tested); sandbox check uses a
  **write**-grade message (`LibrarySandbox.ResolveWritePath`; writes hard-refuse with no root,
  unlike single-clip reads).
- [x] Single-flight: one process-wide `SemaphoreSlim(1,1)` around write execution (R8).
- [x] Result payload echoes: what changed (set/deleted/appended/cleared), backup path (or
  null), dry-run flag, plus a **post-write read-back** of the clip's metadata (one extra
  parse buys the model ground truth instead of an assumption).
- [x] Tests (13): each write verified by re-read; backup appears by default and respects
  `backup: false`; dry-run leaves bytes hash-identical and creates no backup; `confirm`
  refusal (missing AND string-typed); no-root and outside-root refusals; invalid rating →
  friendly refusal + session survives; **media-integrity scanner** (source-linked from
  clipmetascribe.Tests) proves media bytes + chunk offsets survive a full MCP-driven
  set→append→clear→clear-all lifecycle.

## Post-phase-2 field report from the first agent consumer (2026-06-12)

The user had Claude Desktop (Opus, using the live extension) critique the tool surface after
real use. Triage, folded into the phase-3 PR:

**Accepted:**
- Cross-references in tool descriptions ("for many clips use library_export / search_index"), 
  the agent never found library_export because nothing routed it there; agents route on
  descriptions.
- `clip_get_metadata` enriched with `sizeBytes` + `knownUnset` + `customFields`, and
  **`clip_get_stats` removed**, values + categorization previously cost 2 calls and 2 full
  parses of an already-parsed file; a tighter surface also routes better.
- `staleClipCount` in every `library_search_index` response (stat calls only), the agent had
  no signal for when to pass `rebuild:true`.

**Deferred (with reasons):**
- Per-clip `hasTags`/`fieldCount` in `library_list`, would put parsing back into the one tool
  designed never to parse; the description cross-refs + `library_export` answer "what's
  tagged?" in one call already. Revisit only if field reports still show N+1 loops.
- ASCII atom-tree format option, MCP Apps `ui://` HTML, MCP prompts, `outputSchema`, UI/
  presentation track, exploratory; spec deliberately skipped `outputSchema` in v1. Backlog.
- `library_find` performance at scale, needs a real-sized library; user doesn't have one yet.

## Phase 4, install fallback + full selftest

**Completed 2026-06-12.**

- [x] `Install/ClaudeConfigInstaller.cs`: timestamped backup, insert/update
  `mcpServers.clipmeta` (absolute exe path, `--library-root` → env var; omitted env = writes
  disabled, said so in the report), refuse on unparseable JSON with the original untouched;
  `--uninstall` reverses, graceful no-op when absent. **Discovery updated from the spec:** the
  Microsoft Store build virtualizes `%APPDATA%\Claude` into its package container (E2E gate
  finding), `DiscoverConfigPath` prefers `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\
  Claude\` when present, else classic `%APPDATA%\Claude`; `--config <path>` overrides.
  Idempotent: re-running --install replaces the entry, never duplicates it.
- [x] `--selftest` grows: tools/call round-trip (`library_list` against a disposable empty
  sandbox passed via the child's env, exercises dispatch → sandbox → Core, not just protocol
  scaffolding) and a dedicated **stdout purity verdict** (any non-JSON stdout line fails the
  run). 11 checks total.
- [x] Installer tests (11) against fixture configs in a temp dir: fresh-create,
  existing-other-servers preserved value-for-value, backup is byte-exact original, idempotent
  re-install, no-root warns writes disabled, corrupt refusal (install AND uninstall) with the
  file untouched, uninstall round-trip + graceful no-ops.

## Phase 5, polish + Definition of Done

- [x] README install section (bundle install via Settings → Extensions → Advanced settings →
  Install Extension…, Store-build unpacked fallback, SmartScreen note per R5, `--install`
  fallback, `--selftest` for support, tool inventory + safety summary). Done 2026-06-12.
- [x] PITFALLS entries for anything learned in phases 1–4, swept 2026-06-12: phase-1 era
  entries (no drag-drop, Store packed-install bug, zip separators, garbage-parse leniency)
  were recorded as they happened; phases 2–4 produced no new field gotchas to record.
- [ ] **Full manual E2E gate (user):** tag a REAL clip conversationally in the actual clips
  folder (not the test copies), then verify from the repo: `clipmetascribe --list` shows the
  fields, and the media-integrity scanner proves the clip byte-identical to its pre-tag backup.
  This also gives the first real-sized-library datapoint for `library_find` performance (field
  report follow-up). Spec §7 status at 2026-06-12: items 1–4 and 6 green (409 tests, 0
  warnings, zero NuGet, pack works, README done); item 5 passed on the test library, the
  real-clip pass is what remains.

---

## Post-merge review addendum (2026-06-12)

Phase 1 went through a multi-agent review after merge (PR #5): an MCP-spec compliance audit
against the live 2025-11-25 spec, an adversarial sandbox probe with empirical Windows
filesystem tests, and a 7-angle code review. **Fixed immediately** (hardening PR): junction
escape via canonical-path containment, ADS suffix bypass, drive-root containment breakage,
crash-proof SafeLogger around the shared log file, 2025-03-26 claim dropped (no batch-receive),
`id: null` → -32600, duplicate-field surfacing in clip_get_metadata, selftest stderr
drain + timeout poisoning + dotnet-host spawn, ZipFile packaging.

**Deferred, fold into phase 2 as pre-work (all completed in PR #7, 2026-06-12):**

- [x] **Core `ClipMetaReader.GetUserFields`** (or `ClipMetaSchema.IsInternal`): the schema-field
  exclusion filter now exists in four places (Exporter, Index, StatsCommand, ReadTools); Core
  should own it once before phase 2 multiplies the copies. Note ListCommand does NOT filter it, 
  decide deliberately whether that's a feature.
- [x] **Hoist stats categorization to Core** (`ClipMetaStats`) before building `clip_get_stats`,
  instead of re-implementing StatsCommand's fieldsSet/knownUnset/customFields in a second shell.
- [x] **`ToolDefinition` example-arguments member** so the stdout-purity test stops hand-mapping
  per-tool args, it must scale to the 9 tools of phases 2–3, especially write tools.
- [x] Build tool descriptions' multi-value-field sentence from `ClipMetaSchema.PipeFields`
  instead of prose listing the fields.
- [x] Derive `McpSession.ServerVersion` from the assembly informational version and make
  pack-mcpb.ps1 verify the manifest version matches.
- [x] Consider restructuring `Dispatch` so notification-vs-request is decided once (removes the
  repeated guards and `Id!` operators before more methods are added).

**Robustness backlog (not gating):** bounded stdin line length (a no-newline multi-GB line
OOMs `ReadLine`; host-controlled input, so low risk), explicit `MaxDepth` on `JsonNode.Parse`
(framework default ~64 already returns a clean -32700), JSON-RPC -32600-vs--32700/-32601
nuances for malformed-but-valid-JSON requests (documented MINOR deviations, only misbehaving
clients ever see them).

## Self-review checkpoints

- Every tool delegates to Core; if logic wants to live in `clipmetamcp`, it moves to Core first
  (thin-shell rule).
- Zero NuGet in `clipmetamcp`; MSTest only in the test project.
- No `Console.WriteLine` anywhere in serve-mode code paths, enforced by the purity test, not by
  vigilance.
- `BoxNode`/parser internals never leak into protocol DTOs.
