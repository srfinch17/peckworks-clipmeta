# Watched-Clip Resolution + Deferred-Tag Queue, Design Spec
**Date:** 2026-06-21
**Status:** Approved for planning (brainstorm complete)
**Author:** Peckworks Lab

---

## Problem Statement

The whole point of ClipMeta is to tag clips *while you watch them* (or right after you capture them) so a large library becomes searchable later. The metadata engine, search, index, copy, and batch all exist. The missing piece is the bridge between "a human is watching a clip in a media player" and "ClipMeta knows *which file* that is, so it can tag it."

The target experience: you open a clip in MPC-HC or VLC, watch it, and say to Claude "tag this, rocket jump, market garden kill." Claude asks ClipMeta *which clip is playing*, gets a confident answer, and applies the tags, without you ever typing or pasting a path. The same resolution also serves the capture-time flow ("Claude, tag that last clip 'uber chain'").

This spec covers **resolution** (which clip, with what confidence) and the **deferred-tag queue** that lets you tag clips faster than the filesystem lock will allow a write to land. It does **not** cover the higher tagging-session/payload workflow (see §13).

---

## Why this is hard (the two intrinsic constraints)

1. **No single signal is certain.** A window title tells us a filename; last-access-time tells us recency; a file lock tells us *something* is open. Each is individually fallible (titles can show metadata instead of a filename; access time is bumped by indexers/AV/our own reads; locks are released by some players mid-playback). Certainty comes from **corroboration** across signals, not from any one.
2. **A playing file is locked against our write.** `File.Replace` deletes-and-swaps the target, which fails with a sharing violation unless every open handle was opened with `FILE_SHARE_DELETE`, which media players do not grant. So we generally **cannot write to the clip while it is playing**; the player must advance to the next clip or close. This is not a bug to fix; it is a constraint to design around (and the basis for the deferred-tag queue).

Both constraints point at the same architecture: **a pluggable set of confidence signals feeding a resolver, and a write step that defers when the target is busy.**

---

## Scope

**In scope, Pass 1 (resolution):**
- `IWatchSignal` seam + `WatchContext` + corroboration-based confidence scoring, in `clipmeta.core`.
- Two producing signals: **player window title** (Windows) and **last access time** (cross-platform).
- **Lock-probe enrichment** (`inUse`) used as a tiebreaker and a pre-write warning, never as a sole basis.
- Cross-platform process seam (`IProcessWindowSource` + Windows implementation + OS-guarded factory) so Core builds and tests everywhere.
- **Access-time hardening:** stop ClipMeta's own reads from polluting the access-time signal, at the single parse choke point.
- Surfaces: MCP tool `library_watching`; CLI `clipmetascribe "<libraryDir>" --watching`.

**In scope, Pass 2 (deferred-tag queue):**
- Durable, library-root-resident tag queue keyed by clip path.
- Opportunistic drain on each watched-clip call + explicit flush (MCP tool + CLI `--flush-queue`).
- Surfaces for enqueue/flush/status.

**Out of scope (recorded, not built):**
- The **tagging-session / predefined-payload** workflow ("I'm on a TF2 run → auto-add `game=Team Fortress 2` plus spoken tags") and a ClipMeta self-mark for "already touched this clip." Separate future spec (§13); rides on top of resolution and does not constrain it.
- Future signals beyond pass 1: MPC-HC web interface, VLC/MPC recent-file lists, metadata-title match, open-handle enumeration. The seam is built for them now; the signals are documented in §11 and added later with zero edits to existing code.
- Mac/Linux player probes (the seam is ready; no platform implementation in this spec).
- Resolve-and-tag in a single call. Resolution is always separate from the existing write path.

---

## 1. Solution Structure

```
clipmeta.core/
├── Abstractions/
│   ├── IProcessWindowSource.cs      ← NEW: snapshot of (process name, window title)
│   └── IWatchSignal.cs              ← NEW: one confidence signal → evidence
├── Watching/                        ← NEW concern (parallel to Read/ and Write/)
│   ├── ProcessWindow.cs             ← record: ProcessName, WindowTitle
│   ├── WindowsProcessWindowSource.cs ← [SupportedOSPlatform("windows")]
│   ├── EmptyProcessWindowSource.cs  ← non-Windows / not-wired default
│   ├── ProcessWindowSource.cs       ← static ForCurrentPlatform() factory (OS guard)
│   ├── MediaPlayers.cs              ← the extensible known-player name list
│   ├── PlayerTitleParser.cs         ← PURE: title → .mp4 reference (full path | bare name)
│   ├── WatchContext.cs              ← inputs shared by all signals (enumerated library, etc.)
│   ├── SignalHit.cs                 ← one piece of evidence from one signal
│   ├── WatchSignals.cs              ← PlayerTitleSignal, AccessTimeSignal (pass 1)
│   ├── WatchingResolver.cs          ← runs signals, aggregates, scores confidence
│   ├── WatchingCandidate.cs         ← one ranked result row
│   └── TagQueue.cs                  ← PASS 2: durable deferred-write queue
├── Mp4/
│   ├── AccessTimeGuard.cs           ← NEW: capture/restore LastAccessTimeUtc (best-effort)
│   └── Mp4Parser.cs                 ← MODIFIED: wrap the open in AccessTimeGuard
clipmetamcp/Tools/
│   └── ReadTools.cs                 ← MODIFIED: register library_watching (+ queue tools, pass 2)
clipmetascribe/Commands/
│   ├── WatchingCommand.cs           ← NEW: --watching
│   └── FlushQueueCommand.cs         ← NEW (pass 2): --flush-queue
```

**Thin-shell rule holds:** all logic is in `clipmeta.core/Watching/`. The MCP handler and the CLI command parse arguments and call Core. Neither contains resolution logic.

---

## 2. The Cross-Platform Process Seam (the only thing that must be faked)

Resolution touches the filesystem directly (enumeration, access time, lock probe), which tests exercise against real temp files exactly as the existing integration tests do. The **only** dependency that cannot run in CI is reading live process window titles, so that is the one seam.

```csharp
/// <summary>One process's main-window title, as seen at a moment in time.</summary>
public readonly record struct ProcessWindow(string ProcessName, string WindowTitle);

/// <summary>Supplies the window titles of currently-running media players.</summary>
public interface IProcessWindowSource
{
    /// <summary>
    /// Returns (process name, main-window title) for every running process whose name matches
    /// one of <paramref name="processNames"/> (case-insensitive) and has a non-empty title.
    /// Implementations must never throw for a single inaccessible process, skip and continue.
    /// </summary>
    IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> processNames);
}
```

- `WindowsProcessWindowSource`, `[SupportedOSPlatform("windows")]`; enumerates `Process.GetProcesses()`, matches names, reads `MainWindowTitle` inside a per-process `try/catch` (access-denied/exited processes are skipped). Reading titles only for *matched* processes keeps it cheap.
- `EmptyProcessWindowSource`, returns an empty list; the default on non-Windows and whenever a real source isn't wired.
- `ProcessWindowSource.ForCurrentPlatform()`, returns the Windows source when `OperatingSystem.IsWindows()`, else `EmptyProcessWindowSource`. Centralizes the OS guard so neither surface repeats it, and CI on Linux cleanly falls through to the access-time signal. CA1416 stays satisfied via the platform annotation + guard (preserves the zero-warning rule).

The **player-name list lives in `MediaPlayers.cs`** (resolver-owned constant, not in the platform source), so it is unit-testable and trivially extensible:
`mpc-hc, mpc-hc64, mpc-be, vlc, mpv, wmplayer, PotPlayer` (documented as "append here to support a new player").

---

## 3. The Signal Seam (corroboration, open for extension)

Resolution is not "primary signal, then fallback." It is **N signals contributing evidence; the resolver aggregates per clip and scores confidence from how many independent signals agree.** Adding a player or a detection method later = register a new `IWatchSignal`, with zero edits to the resolver, the same open/closed discipline as `IMediaParser`.

```csharp
/// <summary>
/// Why a signal believes a particular clip is the one being watched. Several signals may emit a
/// hit for the same clip; the resolver groups hits by path and scores confidence from corroboration.
/// </summary>
public sealed record SignalHit(
    string ClipPath,        // MUST be a path enumerated from the library (never fabricated)
    string Source,          // "player_title" | "access_time" | (future sources)
    string? Player,         // process name when the evidence came from a player; else null
    bool Ambiguous);        // true when this signal alone could not disambiguate (e.g. a filename
                            // matching multiple clips, or multiple players open)

/// <summary>One pluggable confidence signal.</summary>
public interface IWatchSignal
{
    /// <summary>Stable identifier, also used as SignalHit.Source.</summary>
    string Name { get; }

    /// <summary>
    /// Emits zero or more evidence hits for the current moment. MUST only reference clips present
    /// in <see cref="WatchContext.LibraryClips"/>, a signal selects among already-enumerated clips,
    /// it never constructs a path from external input. MUST NOT throw for ordinary failures
    /// (player closed, file vanished, registry/file unreadable): emit nothing instead.
    /// </summary>
    IEnumerable<SignalHit> Detect(WatchContext context);
}
```

### `WatchContext`, enumerate the library once, share it

```csharp
/// <summary>Shared inputs for one resolution pass, so signals don't each re-scan the library.</summary>
public sealed class WatchContext
{
    /// <summary>Every clip under the library root, enumerated once (path + filename + FileInfo).</summary>
    public IReadOnlyList<LibraryClip> LibraryClips { get; init; }

    /// <summary>Filename → clip(s) lookup, for resolving a bare title filename.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LibraryClip>> ByFileName { get; init; }

    /// <summary>Full-path → clip lookup (ordinal-ignore-case), for validating a full-path title.</summary>
    public IReadOnlyDictionary<string, LibraryClip> ByFullPath { get; init; }

    /// <summary>Window titles of running players (empty on non-Windows / when no player runs).</summary>
    public IReadOnlyList<ProcessWindow> PlayerWindows { get; init; }
}
```

**Security property (no fabrication):** because every `SignalHit.ClipPath` must come from `LibraryClips`, files actually enumerated under the library root, a malicious or accidental window title like `C:\Windows\evil.mp4` simply matches nothing and is dropped. The title *selects*; it never *constructs*. This is why Core does not need `LibrarySandbox`: candidates are inherently library-contained. (The MCP surface still calls `sandbox.RequireRoot()` to obtain the root and refuse when unconfigured.)

---

## 4. Pass-1 Signals

### 4a. `PlayerTitleParser` (pure, the most-tested unit)

Given a window title, extract one `.mp4` reference, full-path pattern first:

| Order | Pattern | Meaning |
|---|---|---|
| a | `([A-Za-z]:\\[^"\|*?<>]+?\.mp4)` | Full path (MPC-HC style). Looked up in `ByFullPath`. |
| b | `([^\\/:*?"<>\|]+?\.mp4)` | Bare filename (VLC `name.mp4 - VLC media player`). Looked up in `ByFileName`. |

Returns a small `TitleExtraction?` (`Kind = FullPath \| BareName`, `Value`). A title with no `.mp4` (VLC showing an embedded metadata title, a stopped player, a custom title format) yields `null`.

### 4b. `PlayerTitleSignal : IWatchSignal` (`Name = "player_title"`)

For each `ProcessWindow` in the context, parse the title and resolve against the enumerated library:
- **Full path** → present in `ByFullPath`? Hit (`Ambiguous=false`). Absent → **drop** (don't fabricate).
- **Bare name** → in `ByFileName`: exactly one clip → hit (`Ambiguous=false`); more than one → a hit per clip (`Ambiguous=true`); none → drop.
- `Ambiguous=true` is also set on *every* player-title hit when **more than one** recognized player has a resolvable title (we can't tell which window you're looking at).

### 4c. `AccessTimeSignal : IWatchSignal` (`Name = "access_time"`)

Emits the library clips ordered by `LastAccessTimeUtc` descending, each `Ambiguous=true` (recency alone is never certain). The resolver only *surfaces* these when there is no player-title hit, or when the caller asked to include the access fallback.

### 4d. Lock-probe enrichment (not a producing signal)

A lock probe cannot *name* a clip, so it is not an `IWatchSignal`; it **enriches** candidates during aggregation. For each surviving candidate: `new FileStream(path, Open, Read, FileShare.None)` → `IOException` ⇒ `inUse=true`; dispose immediately; any other exception ⇒ `inUse=false`, continue. Never fails the call. Used only to break ties and to warn before a write (a busy file will reject `File.Replace`).

---

## 5. `WatchingResolver`, aggregation & confidence

```csharp
public sealed record WatchingCandidate(
    string Path,
    string Name,
    string Source,              // dominant source: "player_title" | "access_time"
    string? Player,
    DateTime LastAccessTimeUtc,
    double SecondsSinceAccess,
    bool InUse,
    string Confidence);         // "high" | "low"

public sealed class WatchingResolver
{
    public WatchingResolver(IReadOnlyList<IWatchSignal> signals);

    public IReadOnlyList<WatchingCandidate> Resolve(
        string libraryRoot, int limit, bool includeAccessFallback);
}
```

**Algorithm:**
1. Enumerate the library once → build `WatchContext` (incl. `PlayerWindows` from the wired `IProcessWindowSource`).
2. Run every registered signal; collect hits.
3. Group hits by clip path. A clip's evidence = the set of distinct sources that named it (+ ambiguity flags).
4. **Confidence = corroboration:**
   - **high** ⇔ named by `player_title` with `Ambiguous=false` (single player, unambiguous file). Corroboration by additional signals keeps it high; it never *lowers* a clean player hit.
   - **low** ⇔ everything else: ambiguous player hits (multiple players, or a filename hitting multiple clips), access-time-only candidates, or any single weak signal.
5. **Surface the access-time fallback** only if there are no player-title hits, *or* `includeAccessFallback` is true (then as additional `low` rows after the player hits).
6. Enrich each candidate with the lock probe (`inUse`).
7. **Rank:** high before low; within a tier, `inUse=true` first (tiebreak), then most-recent access first. Truncate to `limit`.

The corroboration model is deliberately conservative: **a write is only auto-safe on a single `high` candidate.** Anything `low` is for the agent/CLI to confirm with the human before mutating a file.

---

## 6. Access-Time Hardening (bounded sub-workstream)

ClipMeta's own reads must not bump the very signal §4c depends on. Every read funnels through **one** choke point, `Mp4Parser.ParseFile` (`clipmeta.core/Mp4/Mp4Parser.cs`, the only place a clip is opened for reading), so the fix is one site, not an N-call-site audit.

```csharp
/// <summary>
/// Captures a file's LastAccessTimeUtc on construction and restores it on Dispose, best-effort.
/// Restoring is itself a metadata write that can fail (file locked by a player, read-only,
/// removed): such failures are swallowed, preserving the signal must never break a read.
/// </summary>
public readonly struct AccessTimeGuard : IDisposable { /* GetLastAccessTimeUtc / SetLastAccessTimeUtc */ }
```

`Mp4Parser.ParseFile` wraps its `FileStream` open in an `AccessTimeGuard`. Every CLI and MCP read inherits the preservation. The write engine's verify-reparse (on a temp file) and backup-reparse are harmless to guard.

---

## 7. The Write-While-Open Constraint (test + document)

Confirmed during design: `File.Replace` against a file another process holds open (without `FILE_SHARE_DELETE`, which players don't grant) fails with a sharing violation. Therefore:
- A clip cannot generally be written **while it is the one playing**. It frees when the player advances ("next") or closes.
- The existing write path already detects this (it probes with `FileShare.None` and surfaces a friendly "open by another process" message). Resolution surfaces `inUse` so a caller can warn *before* attempting the write.
- **Empirical test item (per player), recorded in PITFALLS:** does MPC-HC / VLC release the lock on *stop*, on *next*, or only on *close*? The answer shapes the queue-drain timing in pass 2 and the guidance we give the user.

This constraint is the justification for pass 2.

---

## 8. Pass 1 Surfaces

### 8a. MCP tool `library_watching` (in `ReadTools`)

- **Params:** `limit` (optional int, default 5), `include_access_fallback` (optional bool, default true).
- **Root:** `sandbox.RequireRoot()` (refuses cleanly when no library configured).
- **Source:** `ProcessWindowSource.ForCurrentPlatform()`.
- **Returns:** ranked array; each entry `{ path, name, source, player, lastAccessTimeUtc, secondsSinceAccess, inUse, confidence }`.
- **Description (to the model):** "Resolve 'the clip I'm watching / just watched.' A `player_title` hit resolved to a library path is **high** confidence, prefer it. If only access-time candidates exist, or multiple players are open, confidence is **low**, confirm with the user before tagging. To tag, call the existing write tool with the chosen `path`."
- **`ExampleArguments`** ignores the clip path (returns `{}` or `{ "limit": 5 }`); the stdout-purity harness drives it cross-platform (Linux → access-time only, still pure to stdout).

### 8b. CLI `clipmetascribe "<libraryDir>" --watching [--limit N] [--no-access-fallback]`

- First positional = library directory (matches `--find`/`--vocab`/`--index`).
- New `WatchingCommand.cs`; prints the ranked candidates (path, source, confidence, player, seconds-since-access, inUse).
- **Resolve-only.** To tag, run a normal write on the printed path. New flags added to `KnownFlags` and `PrintUsage`.

---

## 9. Pass 2, Deferred-Tag Queue

The lock constraint creates a natural pump: when you advance to the next clip, the previous clip's handle frees. Your *next* spoken command is an MCP call arriving at the server, so the server **drains the queue opportunistically on each watched-clip call** (write entries whose locks have cleared), then resolves/enqueues the current clip. No background daemon, no polling.

- **Durable store:** a small JSON queue in the **library root** (same pattern as the index), so spoken tags survive an MCP-host restart or crash and a write that can't land yet simply stays queued.
- **Entry:** `{ clipPath, mutation (MetadataMutation), enqueuedAtUtc, confidence }`. Keyed by clip full-path.
- **Merge semantics:** re-tagging an already-queued clip layers onto its pending payload using the existing mutation rules (set last-wins, append accumulates), never a duplicate entry.
- **Drain (opportunistic):** on each watched-clip call, before resolving the current clip, attempt each queued entry whose `inUse` probe now reads false, via the normal temp-file → `File.Replace` engine. Still-locked → leave queued, retry next tick. Vanished/moved clip → drop with a note.
- **Drain (explicit):** an MCP **flush** tool and CLI `clipmetascribe "<libraryDir>" --flush-queue`, for the **last** clip, when you stop and there is no "next" command to pump the drain. Optionally a flush attempt on MCP-server shutdown.
- **Safety:** drain is single-threaded (no two writes race the same file; the write engine is already per-file safe, we simply never drain concurrently). Low-confidence resolutions are *held for confirmation*, not silently enqueued.

**UX consequence (honest):** "tag this clip" almost always lands the moment you advance; you only ever wait on the final clip until a flush.

Pass-2 surfaces (sketch, finalized in the pass-2 plan): MCP `library_queue_tag` (enqueue for a resolved path), `library_flush_queue`, `library_queue_status`; CLI `--flush-queue` (+ status in `--watching` output).

---

## 10. Test Strategy

Core logic is tested **through** `clipmetascribe.Tests` (its CLI exposes the engine), MCP shape through `clipmetamcp.Tests`, matching the existing convention (no standalone Core test project).

**Pure parser (`PlayerTitleParser`)**
- MPC full-path title → exact path.
- `name.mp4 - VLC media player` → filename extracted, resolved to the library path.
- Bare filename **not** in the library → dropped, never fabricated.
- Filename matching **multiple** clips → multiple `low` candidates.
- Title with no `.mp4` (embedded metadata title / stopped) → no player candidate.

**Resolver (temp dirs + fake `IProcessWindowSource`)**
- Access fallback returns most-recently-accessed when no player candidate.
- Multiple players open → all candidates `low`.
- Single unambiguous player hit → `high`; corroborating access-time keeps it `high`.
- Empty library → empty list (not an error).
- Lock probe: `inUse=true` for a file held with `FileShare.Read`; `false` when free.
- A clip that exists only in the title's full-path form but is outside the library → dropped.

**Access-time hardening**
- Reading a clip (`--list`/`--find`/`--export`/`clip_get_metadata`) leaves `LastAccessTimeUtc` unchanged (within tolerance), proving the guard at the choke point.

**MCP (`clipmetamcp.Tests`)**
- `library_watching` registered; result shape matches §8a; rides the stdout-purity harness on Windows and Linux.
- Refuses cleanly (model-readable message) when no library is configured.

**Pass-2 queue (when built)**
- Enqueue while locked → entry persisted; advance (unlock) → next call drains it; file gains the tags.
- Re-tag a queued clip → payloads merge (append accumulates, set last-wins), one entry.
- Explicit `--flush-queue` writes the last clip after the player closes.
- Queue survives a simulated restart (re-read from disk).
- Vanished queued clip → dropped, no crash.

**Cross-platform**
- All of the above run on CI (Linux) via `EmptyProcessWindowSource`; no test launches a real player.

---

## 11. Signal Roadmap (seam-ready, not in this spec)

| Signal | Evidence | Confidence | Mechanism (pure BCL) | Notes |
|---|---|---|---|---|
| MPC-HC web interface | Exact path of the *currently* playing file, live | very high | `HttpClient` GET `http://localhost:13579/variables.html` | Needs the web UI enabled (off by default) |
| VLC recent MRL | Recently-played list w/ recency, **survives close** | high | Read `%APPDATA%\vlc\vlc-qt-interface.ini` `[RecentsMRL]` (`.conf` Linux, `.plist` macOS) | Timestamps in ms (Win/Linux) |
| MPC recent list | Last-played full path, survives close | high | Registry `HKCU\Software\MPC-HC\...\Recent File List` (or `.ini` portable) | Windows |
| Metadata-title match | When a title has no filename, match it against indexed clip title/name metadata | low alone (good corroborator) | index lookup | Realizes the "VLC embedded-title" idea as corroboration |
| Open-handle enumeration | Exact file a process holds open | very high | `NtQuerySystemInformation` P/Invoke | **Rejected for now**, undocumented, version-fragile; below this codebase's robustness bar |

Each lands as a new `IWatchSignal` registered alongside the pass-1 two, strengthening corroboration with no edits to the resolver.

---

## 12. Risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | `MainWindowTitle` throws for some processes | Per-process try/catch in the Windows source; skip and continue |
| 2 | Restoring access time fails (clip locked by the player) | `AccessTimeGuard` is best-effort; swallow, a read never fails over it |
| 3 | VLC shows an embedded metadata title, not the filename | No `.mp4` → no player candidate → fall through to access-time (and, later, the metadata-title signal) |
| 4 | A window title names a real `.mp4` outside the library | Candidates only come from enumerated library clips; out-of-library paths match nothing → dropped |
| 5 | Cross-platform build / CI | OS-guarded factory + `[SupportedOSPlatform]`; non-Windows uses `EmptyProcessWindowSource` |
| 6 | Tagging faster than writes can land (locked files) | Pass-2 durable queue + opportunistic/explicit drain |
| 7 | Queue write races a direct write on the same file | Single-threaded drain; per-file-safe write engine; queue keyed by path |
| 8 | Acting on a wrong clip | Only a single `high` candidate is auto-safe; `low` requires confirmation before any mutation |
| 9 | Player doesn't release the lock on "next" (only on close) | Empirical PITFALLS test (§7); queue retries until the lock clears regardless |

---

## 13. Future (recorded for continuity)

- **Tagging sessions / predefined payloads**, a "run profile" (e.g. `game=Team Fortress 2` auto-applied) plus spoken-tag composition, so "rocket jump market garden kill" becomes a full mutation. Mostly an agent/conversation + small profile-store concern; sits *above* resolution and the queue. Its own brainstorm/spec.
- **ClipMeta self-mark**, an internal "ClipMeta last touched this clip" marker to help the session layer distinguish already-processed clips from genuinely-untagged ones during a run.
- **Roadmap signals (§11)**, MPC web interface, VLC/MPC recents, metadata-title match.
- **Mac/Linux player probes**, implement `IProcessWindowSource` (and platform recents signals) for non-Windows.

---

## Definition of Done

**Pass 1:**
1. `dotnet build`, zero warnings, zero errors, all projects (incl. CA1416 platform correctness).
2. `dotnet test`, all pass, including the resolver/parser tests and the access-time-preservation test, on Windows **and** clip-less CI.
3. `library_watching` returns correctly-shaped, correctly-ranked candidates; `--watching` prints them; both resolve-only.
4. A `high` candidate corresponds to an unambiguous player-title hit resolved inside the library; nothing out-of-library is ever returned.
5. Reading a clip does not change its `LastAccessTimeUtc`.
6. Zero NuGet packages added to production projects; new public types documented; new gotchas (esp. the write-while-open finding) recorded in `docs/PITFALLS.md`.

**Pass 2:**
7. Tags spoken faster than writes can land are queued durably and drained as locks clear (opportunistic) and on explicit flush; the last clip lands after a flush.
8. Queue survives a restart; merges re-tags; drops vanished clips; never races a write.
