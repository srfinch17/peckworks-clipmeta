# CLAUDE.md, peckworks-clipmeta

A suite of C# command-line tools for reading and writing metadata **inside** MP4 files, so tags travel with the file. Zero external dependencies in production code, pure .NET BCL only.

> **History:** This project began as a single tree-viewer built via a 5-role "orchestrator" exercise. That original brief is archived at `docs/archive/CLAUDE-orchestrator-original.md` for reference. It no longer describes the project, trust this file, the code, and `docs/superpowers/`.

---

## Architecture (as of 2026-06)

Solution: `peckworks-clipmeta.slnx`, **.NET 10**, seven projects:

| Project | Namespace | Purpose |
|---------|-----------|---------|
| `clipmeta.core` | `ClipMetaCore` | All business logic: MP4 parse/read/write, schema, search/index, logging. Zero NuGet deps. |
| `clipmetaview` | `ClipMetaView` | Thin CLI: renders the box/atom tree. References Core. |
| `clipmetascribe` | `ClipMetaScribe` | Thin CLI: read/write/search/copy metadata (12 commands, incl. `--copy-from` and `--flush-queue`; write ops also batch over a directory). References Core. |
| `clipmetamcp` | `ClipMetaMcp` | Thin MCP server shell: stdio JSON-RPC 2.0, exposes 17 clipmeta tools to MCP hosts (Claude Desktop). References Core. Packs to a `.mcpb` bundle via `tools/pack-mcpb.ps1`. Product version **1.0.0** (first public release; latest feature work is pass-7, see Versioning). |
| `clipmetaview.Tests` | | MSTest, 101 tests. |
| `clipmetascribe.Tests` | | MSTest, 494 tests (incl. real-clip integration and byte-level media-integrity tests). |
| `clipmetamcp.Tests` | | MSTest, 132 tests (protocol shape, tool behavior, sandbox escapes, stdout purity). |

> `clipmetascribe` `--watching` and the MCP tool `library_watching` resolve the currently/just-watched clip from open media players (resolve-only, no write; call a write tool with the returned path to tag). Resolution is **library-aware**: a player title is matched against KNOWN library basenames (`LibraryTitleMatcher`, pass-3) rather than extract-then-exact-match, which fixed intermittent MPC-HC detection. `library_watching` returns `anyLiveTarget`, when false, NOTHING is open/locked and callers must not auto-tag a recency guess. **Two modes (pass-4, v1.3.0):** *review mode*, a background `ReviewWatcher` records player title-segments so a spoken tag binds to the clip that was playing **when the user spoke**, not when the tool call ran (fixes the poll-at-call-time binding race); an optional `spoken_at` (ISO-8601) does an exact segment lookup and clears a dictation backlog (fire-N-ahead, oldest-first). *gaming mode*, when no player is open, a `RecentWriteSignal` resolves a clip just **saved to disk**; a single fresh save (≤5 min by **NTFS creation-time**, not in the `.clipmeta-index` baseline, and not self-written) is a high-confidence auto-taggable live target (**Policy A**, it intentionally reverses the no-player-no-lock "don't auto-tag" rule for that case), several at once stay low/confirm. Non-blocking `review[]` advisories (`autoCorrected`/`sameClipTwice`/`sequenceSkip`/`multiplePlayersActive`/`timestampUnmatched`) ride the response. **Pass-5 (v1.4.0) trust hardening:** a session `SelfActionLedger` records every clip ClipMeta itself wrote/read, so `recent_write` no longer false-positives on our own tag-write (which bumps mtime) and `access_time` no longer floats a clip we just read/exported; the background `QueueDrainPump`'s otherwise-silent auto-flushes now surface as `autoFlushed` (a report-once `DrainJournal`) on `library_watching`/`library_flush_queue`/`library_queue_status`; and write/queue responses carry a **soft** `unknownPlayer` advisory (`PlayerRosterGuard`) when a `players` value isn't in `library_vocab players` ∪ an optional `roster` arg, the write still lands. **Pass-6 (v1.5.0) resolver/advisory hardening:** (1) a foreign player open on an out-of-library file no longer hides a fresh game-save, a single unambiguous `recent_write` (Policy A) survives the wrong-directory suppression, and the `player_outside_library` **`warning`** ("do not tag") demotes to a non-blocking **`advisory`** (type `player_outside_library_ignored`) when a gaming candidate is present (decided by the pure `ReadTools.ForeignNoticeIsBlocking`); (2) the `multiplePlayersActive` review advisory now fires whenever **≥2 players have an open segment** (not only near-simultaneous starts) and `ResolveReview` **caps confidence** (`anyLiveTarget=false`, no auto-bind) so the caller confirms; (3) `review[]` advisory clip names are resolved to clean library basenames and deduped by `ReviewFlagResolver` (no more raw VLC titles / bare `vlc`). **Pass-7 (v1.6.0) review-mode time-base split:** `ResolveReview` was conflating two clocks, it built BOTH the foreign-player diagnostics and the watched-clip *bind* from one synthetic window made from the chosen segment (which may belong to a since-**closed** or foreign player), so it could return `anyLiveTarget:true` beside an *empty* candidate list and let a *closed* player raise a ghost `player_outside_library` warning. Now it runs `ResolveCore` over a **live** player poll for diagnostics + gaming/access, resolves the bind from the chosen segment over the **shared** library (`WatchContext.WithPlayerWindows`, no second enumeration), and derives `anyLiveTarget` from the **final** candidate list via a shared `IsLiveTarget` predicate, making `anyLiveTarget:true`+empty structurally impossible. (Both of the dogfood's *suspected* mechanisms were wrong, one root cause; the live `Resolve` path was untouched. Note: production always runs `ResolveReview`; the no-watcher `Resolve` path is effectively test-only, and the MCP harness can't inject a watcher or an open-player live source, so review-mode resolver fixes are verified at the **Core** level.) Separately, `library_queue_tag` wakes the background pump **only when the clip is locked** (`LockProbe.IsInUse`), so an unlocked queue→flush reports the write under `written` (not `autoFlushed`). A clip that is **playing is locked against writing** (`File.Replace`), so the **deferred-tag queue** (`clipmeta.core/Watching/TagQueue`, persisted as `.clipmeta-queue` in the library root) holds confirmed tags and writes them when the lock clears: MCP `library_queue_tag` / `library_flush_queue` / `library_queue_status`, CLI `--flush-queue`, `library_watching` drains opportunistically, **and a background `QueueDrainPump` (pass-3) auto-flushes the last clip the moment its player closes** (zero-touch). The queue only ever stores an already-resolved, in-library path, it never resolves or guesses. Re-tagging the same clip **accumulates**: `notes`/`tags`/`players` append (notes as prose, tags/players pipe-merge), the rest replace.

`clipmeta.core` layout: `Abstractions/` (`IMediaParser`, `IMediaWriter`, `IClipMetaLogger`, `MediaHandlerRegistry`), `Mp4/`, `Write/`, `Read/`, `Watching/` (watched-clip resolution: signals incl. `PlayerTitleSignal`/`AccessTimeSignal`/`RecentWriteSignal` (gaming mode, keyed on NTFS creation-time + index baseline + `SelfActionLedger`, pass-5), `SelfActionLedger` (self-write/read session ledger), process seam, `LibraryTitleMatcher`, resolver (`WatchingResolver`: `ResolveCore` shared by live `Resolve` and review `ResolveReview`; pass-7 split, review mode runs `ResolveCore` over a live poll for diagnostics/gaming/access then overlays a chosen-segment bind via `WatchContext.WithPlayerWindows`; `anyLiveTarget` from the final list via shared `IsLiveTarget`), plus review mode: `ReviewWatcher`/`ReviewBindingResolver`/`ReviewFlagResolver` (pass-6: resolves+dedups advisory clip names)/`TitleSegment`/`ReviewBinding`/`ReviewFlag`, plus the deferred-tag queue: `TagQueue`, `QueueDrainPump`, `DrainJournal`/`DrainedTag` (pass-5 auto-flush telemetry), `QueuedMutation`/`QueuedTag`/`TagQueueData`/`DrainReport`), `Schema/` (incl. `PlayerRosterGuard`, pass-5), `Logging/`, `Exceptions/`.

> Note: `clipmetascribe` is the tool the old brief called "clipmetaedit." There is no separate clipmetaedit.

---

## How we work here

### Planning, spec before code
Non-trivial features get a dated spec and/or plan under `docs/superpowers/` **before** implementation:
- `docs/superpowers/specs/`, design specs (problem, scope in/out, architecture, risk table, definition of done).
- `docs/superpowers/plans/`, per-feature implementation plans.

The write-engine design spec (`docs/superpowers/specs/2026-05-21-clipmeta-core-write-engine-design.md`) is the gold-standard template. Match its format.

### Mistakes, write them down
When we hit and fix a real bug or a non-obvious gotcha, append it to **`docs/PITFALLS.md`**. Consult that file before touching the parser or writer.

### Memory
Persistent project memory lives in the Claude memory store (indexed in `MEMORY.md` there). Capture durable, non-obvious facts; don't duplicate what the code or these docs already say.

### Public landing page (`docs/index.html`), LIVE
A self-contained GitHub Pages info/landing page lives at `docs/index.html` (build record + decisions in `docs/BUILD-LOG.md`) and is published at https://srfinch17.github.io/peckworks-clipmeta/ (GitHub Pages serving `main` /docs, with `docs/.nojekyll` so it's served verbatim instead of through Jekyll/README). Attribution is "Peckworks Lab" (no personal-name placeholder). Treat it as a curated artifact, don't clobber or regenerate it casually.

---

## Code conventions (non-negotiable)

- **Zero external NuGet packages in production code** (`clipmeta.core`, both CLIs). BCL/SDK only. Test projects may use MSTest, the sole exception.
- **CLIs are thin shells.** `Program.cs` parses args and delegates to a command class or Core. No business logic in a CLI.
- **SOLID / open for extension.** New formats implement `IMediaParser`/`IMediaWriter` and register with `MediaHandlerRegistry`, no edits to existing code. Don't `sealed` types a future format or editor might extend.
- **Big-endian everywhere** for MP4 IO, go through `BigEndianReader`/`BigEndianWriter`, never raw `BinaryReader.ReadInt32()` in parse/write code.
- **Never load `mdat` into memory**; stream-copy. The source file is **never opened for writing**, mutations go to a temp file, verified by re-parse, then `File.Replace`.
- XML doc comments on all public types/methods. Named constants, no magic numbers.
- **No em-dashes.** Do not use the em-dash character (Unicode U+2014, the long dash) anywhere: docs, code comments, string literals, README, the landing page, commit messages, or release notes. Use commas, colons, periods, or parentheses (and "to" for ranges). Search the working tree for U+2014 and remove every hit before committing or publishing. Keep cleanup commit messages neutral.
- `BoxNode` keeps its name until a second media format actually earns a generic abstraction.
- **Testable surfaces.** A thin shell that builds its own dependency internally (e.g. `WatchingCommand` / the MCP handler calling `ProcessWindowSource.ForCurrentPlatform()`) takes a *trailing optional* injectable that defaults to the real impl, so rendering/output can be tested with a fake without changing production wiring.

---

## Build & test

From the solution root:

```
dotnet build  --nologo -v q          # must be 0 warnings, 0 errors
dotnet test   --nologo --no-build -v q
```

- `clipmetascribe.Tests` takes a few minutes (real-clip integration + media-integrity hashing), not a hang; use a long timeout. Wall-time scales with the pristine corpus, so keep it curated (below).
- Integration tests need local clips: `testclips/pristine/` (read-only ground truth) and `testclips/scratch/` (regenerated copies). Both are git-ignored; CI runs clip-less and graceful-skips.
- **The pristine corpus is curated and documented.** `testclips/PRISTINE-MANIFEST.md` (checked in) records every clip's source, structure, and the code path it uniquely covers. **Adding a clip:** drop it in `pristine/`, run the scribe tests (it rides every `[DynamicData]` integration test automatically), then add a manifest row describing what it *uniquely* covers, don't keep same-shape duplicates (they only slow the suite). Keep clips small: write correctness is structural, not size-driven (64-bit `co64`/`largesize` is the only size-gated path and triggers at 4 GB, covered synthetically, not by giant clips). See `docs/superpowers/specs/2026-06-15-pristine-test-corpus-baseline-design.md`.
- **New machine?** If restore fails with `NU1100`, the machine likely has no NuGet source. Run:
  `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`
- **Changed an MCP tool registration or a CLI command surface? Run the FULL relevant test project, not a `--filter`.** Surface-wide assertions live OUTSIDE your diff, e.g. `clipmetamcp.Tests` `ToolsList_ContainsTheFullToolSurface` asserts the exact tool set and registration order, so adding a tool without updating it passes a filtered run and fails only on the full suite. (It bit us registering `library_watching`.)

---

## Versioning (product-scoped, one canonical source)

The repo-root **`VERSION`** file is the single source of truth for the whole product (SemVer). Nothing else is authoritative, everything is stamped *from* it:

- **Assemblies:** `Directory.Build.props` reads `VERSION` into `<Version>`/`<InformationalVersion>` for **every** project. Never put a `<Version>`/`<AssemblyVersion>` in a csproj, it would shadow the canonical source.
- **Self-reports (no hardcoded version literals anywhere):** all three binaries answer `--version` by reading their stamped assembly value via `ClipMetaCore.ClipMetaVersion.Current`; the MCP server also advertises it in `serverInfo.version` (`McpSession.ServerVersion`).
- **The `.mcpb` bundle manifest** (`tools/mcpb-manifest.json`) is stamped from `VERSION` by `bump-version.ps1` and re-stamped + gated against the published exe by `pack-mcpb.ps1` (a mismatch fails the pack).

Tools (`tools/`):
- `bump-version.ps1 <major|minor|patch|set X.Y.Z>`, the **deliberate** bump: rewrites `VERSION`, re-stamps the manifest. Run it when shipping, not on every commit.
- `check-version.ps1`, drift check: probes each artifact's real self-report and prints OK/DRIFT per artifact. `-NoBuild` to skip the build.
- `build-release-artifacts.ps1`, builds the three downloadable assets (`clipmeta.mcpb`, `clipmeta-unpacked.zip`, `clipmeta-cli-win-x64.zip`) into `dist/`. Used locally and by CI.

**Cutting a release:** `bump-version.ps1` → commit → `git tag vX.Y.Z` (the tag must match `VERSION`) → `git push origin vX.Y.Z`. The **`Release` workflow** (`.github/workflows/release.yml`) then builds the assets on a Windows runner and publishes the GitHub Release automatically, it fails the run if the tag and `VERSION` disagree. Use the Actions tab "Run workflow" (workflow_dispatch) to build + upload the assets to the run for inspection **without** publishing, to test the build before tagging. (Releases are Windows-only, self-contained, unsigned.) Note: v1.0.0 predates this workflow and was hand-published; v1.0.1 is the first release actually cut by CI.

**The rule that trips people up:** a bump is **not live in a built/installed artifact until that artifact is rebuilt, repacked, and reinstalled.** Editing `VERSION` makes the repo say the new number instantly, but a running binary / the `.mcpb` installed in Claude Desktop still reports the old one until its own deploy step runs. `check-version.ps1` sees the *repo-built* exe, not what Desktop is running, the installed bundle is verified by reinstalling. (Reset to **1.0.0** for the first public release; pass-N remains the stable feature id, and 1.0.0 supersedes the internal 1.0–1.6 bundle versions.)

---

## Metadata model

Custom fields use the reverse-domain namespace `com.peckworkslab.clipmeta`, stored in MP4 `----` freeform atoms, multi-values pipe-delimited. Well-known fields: `game`, `players`, `tags`, `timecode`, `rating`, `notes` (plus arbitrary custom names). Two **internal** stamps (excluded from curated read surfaces via `ClipMetaSchema.IsInternal`, but written to the file and shown in raw `--list`/tree): `schema` (version) and `tagged_by` (provenance, auto-stamped `Peckworks ClipMeta` on every user-field write; opt out with MCP `stamp_provenance:false` / CLI `--no-provenance`). Full schema and write semantics are in the write-engine design spec.

## Definition of Done (every change)

1. `dotnet build`, 0 warnings, 0 errors, all projects.
2. `dotnet test`, all pass, including real-clip integration and media-integrity tests.
3. Zero NuGet packages added to production projects.
4. Public types documented; new gotchas recorded in `docs/PITFALLS.md`.
