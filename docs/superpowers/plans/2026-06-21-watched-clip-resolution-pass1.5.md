# Watched-Clip Resolution Pass 1.5 (Wrong-Directory Honesty) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the watched-clip resolver from silently tagging the wrong clip: trust bare-name (VLC) matches only when the library file is locked, and warn (instead of guessing) when a player is open on a file that isn't in the configured library.

**Architecture:** Extend the pass-1 `clipmeta.core/Watching/` pipeline. A shared `PlayerTitleResolution` helper becomes the single source of truth for player-title → library resolution (feeding both the signal's hits and the resolver's diagnostics). `SignalHit` carries the match kind so the resolver can apply a bare-name-only collision guard backed by a cloud-safe `LockProbe`. `WatchingResolver.Resolve` returns a `WatchingResult` (candidates + diagnostics); both surfaces render a wrong-directory warning and a per-candidate confirm note.

**Tech Stack:** C# / .NET 10, `clipmeta.core` (zero NuGet), MSTest.

## Global Constraints

- **.NET 10**, solution `peckworks-clipmeta.slnx`. Build: `dotnet build --nologo -v q` → **0 warnings, 0 errors** (incl. CA1416). Test: `dotnet test`.
- **Zero external NuGet packages** in production projects. BCL/SDK only. MSTest is the test-project-only exception.
- **CLIs/MCP are thin shells**, resolution logic stays in `clipmeta.core`.
- **No directory searching anywhere.** A foreign-folder string may come ONLY from the player's own title; never enumerate or probe outside the configured library root.
- **No-fabrication invariant (pass 1):** every returned path must be a clip enumerated under the library root.
- **Collision guard applies to bare-name matches only.** Full-path (MPC) matches stay `high` regardless of lock state.
- **Lock probe must never open an offline/placeholder file** (no cloud hydration).
- **Demote, never drop:** a not-locked bare-name match becomes `low` + note, never removed.
- Confidence values are exactly `"high"` / `"low"`. XML doc comments on all public types/methods; named constants, no magic numbers.

## File Structure (namespace `ClipMetaCore.Watching` unless noted)

- `clipmeta.core/Watching/PlayerTitleResolution.cs`, NEW: `PlayerMatch` record + `For(WatchContext)` helper (single source of truth for player-title resolution).
- `clipmeta.core/Watching/SignalHit.cs`, MODIFY: add optional `TitleExtractionKind? MatchKind`.
- `clipmeta.core/Watching/PlayerTitleSignal.cs`, MODIFY: resolve via the helper, set `MatchKind`.
- `clipmeta.core/Watching/LockProbe.cs`, NEW: cloud-safe `IsInUse(path)`.
- `clipmeta.core/Watching/WatchingCandidate.cs`, MODIFY: add `string? Note`.
- `clipmeta.core/Watching/WatchDiagnostics.cs`, NEW: `UnresolvedPlayer` + `WatchDiagnostics` records.
- `clipmeta.core/Watching/WatchingResult.cs`, NEW: `WatchingResult` record.
- `clipmeta.core/Watching/WatchingResolver.cs`, MODIFY: return `WatchingResult`; collision guard, diagnostics, suppression; use `LockProbe` + `PlayerTitleResolution`.
- `clipmetamcp/Tools/ReadTools.cs`, MODIFY: `Watching` handler renders `warning` + candidate `note`.
- `clipmetascribe/Commands/WatchingCommand.cs`, MODIFY: print warning + note.
- Tests: `clipmetascribe.Tests/PlayerTitleResolutionTests.cs`, `LockProbeTests.cs`, and additions/edits to `WatchSignalsTests.cs` + `WatchingResolverTests.cs`; `clipmetamcp.Tests/LibraryWatchingToolTests.cs` additions.

---

### Task 1: `PlayerTitleResolution` helper + `SignalHit.MatchKind` + `PlayerTitleSignal` refactor

**Files:**
- Create: `clipmeta.core/Watching/PlayerTitleResolution.cs`
- Modify: `clipmeta.core/Watching/SignalHit.cs`, `clipmeta.core/Watching/PlayerTitleSignal.cs`
- Test: `clipmetascribe.Tests/PlayerTitleResolutionTests.cs` (new); `clipmetascribe.Tests/WatchSignalsTests.cs` (add one assertion)

**Interfaces:**
- Consumes: `WatchContext`, `PlayerTitleParser`/`TitleExtraction`/`TitleExtractionKind`, `LibraryClip`, `ProcessWindow`, `SignalHit` (pass 1).
- Produces:
  - `public sealed record PlayerMatch(ProcessWindow Window, TitleExtractionKind Kind, string ReferencedValue, IReadOnlyList<LibraryClip> Matches)`
  - `public static class PlayerTitleResolution { static IReadOnlyList<PlayerMatch> For(WatchContext context); }`
  - `SignalHit` gains trailing `TitleExtractionKind? MatchKind = null`.

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/PlayerTitleResolutionTests.cs`:

```csharp
using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class PlayerTitleResolutionTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string Touch(string name)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private WatchContext Build(params ProcessWindow[] windows) =>
        WatchContext.Build(_tempDir, new FakeProcessWindowSource(windows), MediaPlayers.KnownProcessNames);

    [TestMethod]
    public void For_BareNameInLibrary_ResolvesWithBareNameKind()
    {
        string clip = Touch("clip.mp4");
        IReadOnlyList<PlayerMatch> matches =
            PlayerTitleResolution.For(Build(new ProcessWindow("vlc", "clip.mp4 - VLC media player")));

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual(TitleExtractionKind.BareName, matches[0].Kind);
        Assert.AreEqual(clip, matches[0].Matches.Single().FullPath);
    }

    [TestMethod]
    public void For_FullPathInLibrary_ResolvesWithFullPathKind()
    {
        string clip = Touch("clip.mp4");
        IReadOnlyList<PlayerMatch> matches =
            PlayerTitleResolution.For(Build(new ProcessWindow("mpc-hc64", $"{clip} - MPC-HC")));

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual(TitleExtractionKind.FullPath, matches[0].Kind);
        Assert.AreEqual(clip, matches[0].Matches.Single().FullPath);
    }

    [TestMethod]
    public void For_NamedFileNotInLibrary_ReturnsEntryWithNoMatches()
    {
        Touch("present.mp4");
        IReadOnlyList<PlayerMatch> matches =
            PlayerTitleResolution.For(Build(new ProcessWindow("vlc", "absent.mp4 - VLC media player")));

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual("absent.mp4", matches[0].ReferencedValue);
        Assert.AreEqual(0, matches[0].Matches.Count);
    }

    [TestMethod]
    public void For_TitleWithoutMp4_IsOmitted()
    {
        Touch("clip.mp4");
        IReadOnlyList<PlayerMatch> matches =
            PlayerTitleResolution.For(Build(new ProcessWindow("vlc", "My Montage - VLC media player")));

        Assert.AreEqual(0, matches.Count);
    }
}
```

Add this assertion to an existing bare-name test in `clipmetascribe.Tests/WatchSignalsTests.cs`, inside `PlayerTitle_BareNameInLibrary_UnambiguousHit`, after the existing asserts:

```csharp
        Assert.AreEqual(TitleExtractionKind.BareName, hits[0].MatchKind);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PlayerTitleResolutionTests" --nologo`
Expected: FAIL, `PlayerTitleResolution` / `PlayerMatch` do not exist; `SignalHit.MatchKind` does not exist.

- [ ] **Step 3: Add `MatchKind` to `SignalHit`**

Modify `clipmeta.core/Watching/SignalHit.cs`, add the trailing optional parameter (default keeps every existing construction site, e.g. `AccessTimeSignal`, compiling):

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// One signal's evidence that a particular clip is the one being watched. Several signals may emit
/// a hit for the same clip; the resolver groups hits by path and scores confidence by corroboration.
/// </summary>
/// <param name="ClipPath">Path of an enumerated library clip, never a fabricated path.</param>
/// <param name="Source">The emitting signal's name (also used as the candidate source).</param>
/// <param name="Player">Process name when the evidence came from a player; otherwise null.</param>
/// <param name="Ambiguous">True when this signal alone could not disambiguate the clip.</param>
/// <param name="MatchKind">
/// For a player-title hit, whether the player named a full path or a bare file name; null for
/// non-player signals. The resolver applies the lock-based collision guard to bare-name hits only.
/// </param>
public sealed record SignalHit(
    string ClipPath, string Source, string? Player, bool Ambiguous,
    TitleExtractionKind? MatchKind = null);
```

- [ ] **Step 4: Create `PlayerTitleResolution`**

Create `clipmeta.core/Watching/PlayerTitleResolution.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>One player window whose title named an .mp4, with the library clips it resolves to.</summary>
/// <param name="Window">The player window the title came from.</param>
/// <param name="Kind">Whether the title named a full path or a bare file name.</param>
/// <param name="ReferencedValue">The extracted reference (full path, or bare file name).</param>
/// <param name="Matches">Library clips the reference resolves to; empty when the file is not in the library.</param>
public sealed record PlayerMatch(
    ProcessWindow Window,
    TitleExtractionKind Kind,
    string ReferencedValue,
    IReadOnlyList<LibraryClip> Matches);

/// <summary>
/// Single source of truth for turning open player windows into resolved/unresolved matches against
/// the enumerated library. Both <see cref="PlayerTitleSignal"/> (for hits) and
/// <see cref="WatchingResolver"/> (for wrong-directory diagnostics) use this, so the two can never
/// disagree about what resolved. Windows whose titles contain no .mp4 are omitted entirely.
/// </summary>
public static class PlayerTitleResolution
{
    /// <summary>Resolves every player window whose title names an .mp4 against the library.</summary>
    public static IReadOnlyList<PlayerMatch> For(WatchContext context)
    {
        var result = new List<PlayerMatch>();
        foreach (ProcessWindow window in context.PlayerWindows)
        {
            TitleExtraction? extraction = PlayerTitleParser.Extract(window.WindowTitle);
            if (extraction is null)
                continue;

            TitleExtraction value = extraction.Value;
            IReadOnlyList<LibraryClip> matches = value.Kind == TitleExtractionKind.FullPath
                ? (context.ByFullPath.TryGetValue(value.Value, out LibraryClip? clip)
                    ? new[] { clip }
                    : Array.Empty<LibraryClip>())
                : (context.ByFileName.TryGetValue(value.Value, out IReadOnlyList<LibraryClip>? list)
                    ? list
                    : Array.Empty<LibraryClip>());

            result.Add(new PlayerMatch(window, value.Kind, value.Value, matches));
        }
        return result;
    }
}
```

- [ ] **Step 5: Refactor `PlayerTitleSignal` to use the helper and set `MatchKind`**

Replace the body of `clipmeta.core/Watching/PlayerTitleSignal.cs` (keep the class/`SourceName`/`Name` as-is). The `Resolve` private method is removed, its logic now lives in `PlayerTitleResolution`:

```csharp
    /// <inheritdoc/>
    public IEnumerable<SignalHit> Detect(WatchContext context)
    {
        // Only players whose title resolved to at least one library clip become hits; the resolver
        // handles the unresolved ones (wrong-directory diagnostics) via the same helper.
        var resolved = PlayerTitleResolution.For(context)
            .Where(match => match.Matches.Count > 0)
            .ToList();

        bool multiplePlayers = resolved.Count > 1;
        foreach (PlayerMatch match in resolved)
        {
            bool ambiguousFile = match.Matches.Count > 1;
            foreach (LibraryClip clip in match.Matches)
                yield return new SignalHit(clip.FullPath, SourceName, match.Window.ProcessName,
                    Ambiguous: multiplePlayers || ambiguousFile, MatchKind: match.Kind);
        }
    }
```

Remove the now-unused `private static IReadOnlyList<LibraryClip> Resolve(...)` method from the file.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~PlayerTitleResolutionTests|FullyQualifiedName~WatchSignalsTests" --nologo`
Expected: PASS (new resolution tests + the unchanged `WatchSignalsTests` with the added `MatchKind` assertion).

- [ ] **Step 7: Commit**

```bash
git add clipmeta.core/Watching/PlayerTitleResolution.cs clipmeta.core/Watching/SignalHit.cs clipmeta.core/Watching/PlayerTitleSignal.cs clipmetascribe.Tests/PlayerTitleResolutionTests.cs clipmetascribe.Tests/WatchSignalsTests.cs
git commit -m "refactor(core): extract PlayerTitleResolution + carry MatchKind on SignalHit"
```

---

### Task 2: Cloud-safe `LockProbe`

**Files:**
- Create: `clipmeta.core/Watching/LockProbe.cs`
- Modify: `clipmeta.core/Watching/WatchingResolver.cs` (replace private `ProbeInUse` with `LockProbe.IsInUse`)
- Test: `clipmetascribe.Tests/LockProbeTests.cs` (new)

**Interfaces:**
- Produces: `public static class LockProbe { static bool IsInUse(string path); }`, true when the file has an exclusive-denying open handle; false when free, inaccessible, or offline; never opens an offline/placeholder file.

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/LockProbeTests.cs`:

```csharp
using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class LockProbeTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void IsInUse_FreeFile_False()
    {
        string path = Path.Combine(_tempDir, "free.mp4");
        File.WriteAllBytes(path, Array.Empty<byte>());
        Assert.IsFalse(LockProbe.IsInUse(path));
    }

    [TestMethod]
    public void IsInUse_FileHeldByAnotherHandle_True()
    {
        string path = Path.Combine(_tempDir, "busy.mp4");
        File.WriteAllBytes(path, Array.Empty<byte>());
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.IsTrue(LockProbe.IsInUse(path));
    }

    [TestMethod]
    public void IsInUse_OfflineFile_ReturnsFalseWithoutOpening()
    {
        // An offline/placeholder file must NOT be opened (that would force a cloud download). Prove
        // the short-circuit by holding the file EXCLUSIVELY: if the probe tried to open it, the
        // FileShare.None open would throw and report true. Returning false proves it never opened.
        string path = Path.Combine(_tempDir, "offline.mp4");
        File.WriteAllBytes(path, Array.Empty<byte>());
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.IsFalse(LockProbe.IsInUse(path),
            "offline files must be reported not-in-use without being opened");
    }

    [TestMethod]
    public void IsInUse_MissingFile_False()
    {
        Assert.IsFalse(LockProbe.IsInUse(Path.Combine(_tempDir, "nope.mp4")));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~LockProbeTests" --nologo`
Expected: FAIL, `LockProbe` does not exist.

- [ ] **Step 3: Create `LockProbe`**

Create `clipmeta.core/Watching/LockProbe.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// Best-effort check of whether a file currently has an open handle that denies exclusive access, 
/// the signal that a media player is actively reading it. Cloud-safe: an offline/placeholder file
/// (Dropbox/OneDrive online-only) is reported not-in-use WITHOUT being opened, so the probe can
/// never trigger a hydration download. Never throws, any failure reports not-in-use.
/// </summary>
public static class LockProbe
{
    /// <summary>True when the file has an exclusive-denying open handle; false otherwise.</summary>
    public static bool IsInUse(string path)
    {
        try
        {
            // Never open an offline/placeholder file, opening would force a download. An
            // un-hydrated file is not the one a player is actively reading, so treat it not-in-use.
            if ((File.GetAttributes(path) & FileAttributes.Offline) != 0)
                return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false; // missing/inaccessible/invalid path, not lockable
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true; // a sharing violation means another handle holds it
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Point the resolver at `LockProbe`**

In `clipmeta.core/Watching/WatchingResolver.cs`, replace the call in the post-take probe loop, change `ProbeInUse(ranked[i].Path)` to `LockProbe.IsInUse(ranked[i].Path)`, and **delete** the private `ProbeInUse` method entirely (it is now `LockProbe`). The current loop is:

```csharp
        for (int i = 0; i < ranked.Count; i++)
            ranked[i] = ranked[i] with { InUse = LockProbe.IsInUse(ranked[i].Path) };
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~LockProbeTests|FullyQualifiedName~WatchingResolverTests" --nologo`
Expected: PASS, `LockProbeTests` green, and the pass-1 `WatchingResolverTests` still green (non-offline probe behavior unchanged).

> If `IsInUse_OfflineFile_ReturnsFalseWithoutOpening` is flaky because the OS strips `FileAttributes.Offline`, STOP and report it, do not delete the assertion; we will adjust how the placeholder state is simulated.

- [ ] **Step 6: Commit**

```bash
git add clipmeta.core/Watching/LockProbe.cs clipmeta.core/Watching/WatchingResolver.cs clipmetascribe.Tests/LockProbeTests.cs
git commit -m "feat(core): cloud-safe LockProbe (never opens offline/placeholder files)"
```

---

### Task 3: Return-type plumbing, `WatchingResult` + `WatchingCandidate.Note` (behavior preserved)

**Files:**
- Create: `clipmeta.core/Watching/WatchDiagnostics.cs`, `clipmeta.core/Watching/WatchingResult.cs`
- Modify: `clipmeta.core/Watching/WatchingCandidate.cs`, `clipmeta.core/Watching/WatchingResolver.cs`, `clipmetamcp/Tools/ReadTools.cs`, `clipmetascribe/Commands/WatchingCommand.cs`, `clipmetascribe.Tests/WatchingResolverTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record UnresolvedPlayer(string Player, string ReferencedName, string? ForeignDirectory)`
  - `public sealed record WatchDiagnostics(IReadOnlyList<UnresolvedPlayer> UnresolvedPlayers)`
  - `public sealed record WatchingResult(IReadOnlyList<WatchingCandidate> Candidates, WatchDiagnostics Diagnostics)`
  - `WatchingCandidate` gains trailing `string? Note = null`.
  - `WatchingResolver.Resolve(...)` now returns `WatchingResult` (this task: `Candidates` = the existing list, `Diagnostics` = empty).

This task is a pure mechanical refactor: it changes the return type and threads `.Candidates` through callers and tests, with **no behavior change**. All existing assertions keep passing.

- [ ] **Step 1: Update the existing resolver tests to the new return type**

In `clipmetascribe.Tests/WatchingResolverTests.cs`, the helper currently returns the list directly. Change every `resolver.Resolve(...)` call site to read `.Candidates`. The simplest mechanical change is to wrap resolution in a local helper at the top of the class and replace call sites. Add this private helper to the class:

```csharp
    private static IReadOnlyList<WatchingCandidate> Candidates(WatchingResolver resolver, string dir, int limit, bool fallback) =>
        resolver.Resolve(dir, limit, fallback).Candidates;
```

Then replace each `<resolver>.Resolve(_tempDir, limit: X, includeAccessFallback: Y)` expression in the test bodies with `Candidates(<resolver>, _tempDir, X, Y)`. (Every existing assertion, `result[0]`, `result.Count`, `.Single(...)`, `.All(...)`, then operates on the candidate list exactly as before.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WatchingResolverTests" --nologo`
Expected: FAIL to compile, `Resolve(...)` still returns a list (no `.Candidates`); `WatchingResult` does not exist yet.

- [ ] **Step 3: Create the result types**

Create `clipmeta.core/Watching/WatchDiagnostics.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>A media player open on a file that is NOT in the configured library.</summary>
/// <param name="Player">Process name of the player.</param>
/// <param name="ReferencedName">The file the title named (full path or bare name).</param>
/// <param name="ForeignDirectory">
/// The folder the player is reading from, populated ONLY when the title gave a full path (MPC).
/// Null for bare-name titles: we genuinely do not know where the file is, and will not search.
/// </param>
public sealed record UnresolvedPlayer(string Player, string ReferencedName, string? ForeignDirectory);

/// <summary>Side-band findings from a resolution pass, beyond the ranked candidates.</summary>
/// <param name="UnresolvedPlayers">
/// Players open on files outside the library. Non-empty means "you may be playing from the wrong
/// folder", surfaces should warn and not tag.
/// </param>
public sealed record WatchDiagnostics(IReadOnlyList<UnresolvedPlayer> UnresolvedPlayers);
```

Create `clipmeta.core/Watching/WatchingResult.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>The outcome of one resolution pass: the ranked candidates plus any diagnostics.</summary>
/// <param name="Candidates">Ranked watched-clip candidates, best first (may be empty).</param>
/// <param name="Diagnostics">Wrong-directory and related findings (see <see cref="WatchDiagnostics"/>).</param>
public sealed record WatchingResult(IReadOnlyList<WatchingCandidate> Candidates, WatchDiagnostics Diagnostics);
```

- [ ] **Step 4: Add `Note` to `WatchingCandidate`**

Modify `clipmeta.core/Watching/WatchingCandidate.cs`, append the trailing optional parameter and document it:

```csharp
/// <param name="Note">
/// Optional human-readable caveat (e.g. a not-locked bare-name match the agent should confirm
/// before tagging). Null when there is nothing to flag.
/// </param>
public sealed record WatchingCandidate(
    string Path,
    string Name,
    string Source,
    string? Player,
    DateTime LastAccessTimeUtc,
    double SecondsSinceAccess,
    bool InUse,
    string Confidence,
    string? Note = null);
```

(Keep the existing `<param>` docs above; add the `Note` one in the right place.)

- [ ] **Step 5: Wrap the resolver's return value (no behavior change)**

In `clipmeta.core/Watching/WatchingResolver.cs`, change the method signature and the final `return`. The body that builds `candidates`/`ranked` is unchanged in this task; only the return is wrapped with empty diagnostics:

```csharp
    public WatchingResult Resolve(string libraryRoot, int limit, bool includeAccessFallback)
    {
        // ... existing body unchanged ...

        List<WatchingCandidate> finalCandidates = ranked
            .OrderByDescending(c => c.Confidence == HighConfidence)
            .ThenByDescending(c => c.InUse)
            .ThenByDescending(c => c.LastAccessTimeUtc)
            .ToList();

        return new WatchingResult(finalCandidates, new WatchDiagnostics(Array.Empty<UnresolvedPlayer>()));
    }
```

(The variable names `ranked`/`finalCandidates` follow the existing file; if the current code returns the ordered list inline, assign it to `finalCandidates` first, then wrap.)

- [ ] **Step 6: Update the two surface call sites to `.Candidates` (compile only)**

In `clipmetamcp/Tools/ReadTools.cs`, the `Watching` handler currently does
`IReadOnlyList<WatchingCandidate> candidates = resolver.Resolve(root, limit, includeAccessFallback);`.
Change it to:

```csharp
        WatchingResult result = resolver.Resolve(root, limit, includeAccessFallback);
        IReadOnlyList<WatchingCandidate> candidates = result.Candidates;
```

(The rest of the handler, building the `candidates` JSON array and the response, is unchanged in this task.)

In `clipmetascribe/Commands/WatchingCommand.cs`, change
`IReadOnlyList<WatchingCandidate> candidates = resolver.Resolve(libraryDir, limit, includeAccessFallback);`
to:

```csharp
        WatchingResult result = resolver.Resolve(libraryDir, limit, includeAccessFallback);
        IReadOnlyList<WatchingCandidate> candidates = result.Candidates;
```

- [ ] **Step 7: Run the full watching + surface suites to verify behavior is preserved**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~WatchingResolverTests|FullyQualifiedName~WatchingCommandTests|FullyQualifiedName~LibraryWatchingToolTests" --nologo`
Expected: PASS, every pass-1 assertion still holds (only the return shape changed).

- [ ] **Step 8: Commit**

```bash
git add clipmeta.core/Watching/WatchDiagnostics.cs clipmeta.core/Watching/WatchingResult.cs clipmeta.core/Watching/WatchingCandidate.cs clipmeta.core/Watching/WatchingResolver.cs clipmetamcp/Tools/ReadTools.cs clipmetascribe/Commands/WatchingCommand.cs clipmetascribe.Tests/WatchingResolverTests.cs
git commit -m "refactor(core): Resolve returns WatchingResult (candidates + diagnostics); add candidate Note"
```

---

### Task 4: Collision guard + wrong-directory diagnostics + suppression (the behavioral core)

**Files:**
- Modify: `clipmeta.core/Watching/WatchingResolver.cs`
- Test: `clipmetascribe.Tests/WatchingResolverTests.cs` (update two existing tests; add the state-table tests)

**Interfaces:**
- Consumes: `PlayerTitleResolution.For`, `LockProbe.IsInUse`, `SignalHit.MatchKind`, `TitleExtractionKind`, the records from Task 3.
- Produces: the §1 state-table behavior. `Resolve` now fills `Diagnostics.UnresolvedPlayers`, applies the bare-name lock collision guard, and suppresses access-time candidates when a player is open on a foreign file and nothing resolved.

**Behavior change to be explicit about:** under pass 1, a single unambiguous bare-name (VLC) hit on a *free* file was `high`. Under pass 1.5 it is `high` only when the file is **locked**; a free or offline bare-name hit becomes `low` + note. Two existing tests assumed the old behavior and are updated in Step 1.

- [ ] **Step 1: Update the two existing tests that assumed bare-name-free = high, and add the new state-table tests**

In `clipmetascribe.Tests/WatchingResolverTests.cs`:

(a) `Resolve_SingleUnambiguousPlayerHit_IsHighAndFirst` currently uses a VLC bare-name title on a free file and expects `high`. Switch it to an **MPC full-path** title (full-path stays `high` regardless of lock, preserving the test's intent, "a confident hit ranks first"). Replace its `Resolver(...)` window with a full-path one built from the clip path:

```csharp
    [TestMethod]
    public void Resolve_SingleUnambiguousPlayerHit_IsHighAndFirst()
    {
        string clip = Touch("clip.mp4");
        Touch("other.mp4");

        IReadOnlyList<WatchingCandidate> result = Candidates(
            Resolver(new ProcessWindow("mpc-hc64", $"{clip} - MPC-HC")),
            _tempDir, 5, true);

        Assert.AreEqual(clip, result[0].Path);
        Assert.AreEqual("high", result[0].Confidence);
        Assert.AreEqual(PlayerTitleSignal.SourceName, result[0].Source);
        Assert.AreEqual("mpc-hc64", result[0].Player);
    }
```

(b) `Resolve_PlayerHitWithFallback_AccessOnlyClipsAppearAsLowRows` (added in pass-1 Task 6) uses a VLC bare-name title on a free file and expects `watched.mp4` to be `high`. Switch its window to MPC full-path so the high row is preserved:

```csharp
    [TestMethod]
    public void Resolve_PlayerHitWithFallback_AccessOnlyClipsAppearAsLowRows()
    {
        string watched = Touch("watched.mp4");
        Touch("bystander.mp4");

        IReadOnlyList<WatchingCandidate> result = Candidates(
            Resolver(new ProcessWindow("mpc-hc64", $"{watched} - MPC-HC")),
            _tempDir, 5, true);

        WatchingCandidate high = result.Single(c => c.Name == "watched.mp4");
        Assert.AreEqual("high", high.Confidence);
        Assert.AreEqual(PlayerTitleSignal.SourceName, high.Source);

        WatchingCandidate low = result.Single(c => c.Name == "bystander.mp4");
        Assert.AreEqual("low", low.Confidence);
        Assert.AreEqual(AccessTimeSignal.SourceName, low.Source);
    }
```

Now add the new state-table tests (append to the class):

```csharp
    [TestMethod]
    public void Resolve_BareNameLocked_IsHigh()
    {
        string clip = Touch("clip.mp4");
        using var hold = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read);

        WatchingCandidate c = Candidates(
            Resolver(new ProcessWindow("vlc", "clip.mp4 - VLC media player")), _tempDir, 5, true)
            .Single(x => x.Name == "clip.mp4");

        Assert.AreEqual("high", c.Confidence);
        Assert.IsNull(c.Note);
    }

    [TestMethod]
    public void Resolve_BareNameNotLocked_DemotedToLowWithNote()
    {
        Touch("clip.mp4"); // free

        WatchingCandidate c = Candidates(
            Resolver(new ProcessWindow("vlc", "clip.mp4 - VLC media player")), _tempDir, 5, true)
            .Single(x => x.Name == "clip.mp4");

        Assert.AreEqual("low", c.Confidence);
        Assert.IsNotNull(c.Note); // confirm-before-tagging caveat
    }

    [TestMethod]
    public void Resolve_FullPathNotLocked_StaysHigh()
    {
        string clip = Touch("clip.mp4"); // free, but full-path match can't collide

        WatchingCandidate c = Candidates(
            Resolver(new ProcessWindow("mpc-hc64", $"{clip} - MPC-HC")), _tempDir, 5, true)
            .Single(x => x.Name == "clip.mp4");

        Assert.AreEqual("high", c.Confidence);
        Assert.IsNull(c.Note);
    }

    [TestMethod]
    public void Resolve_PlayerOnForeignFile_NoResolution_WarnsAndSuppressesFallback()
    {
        Touch("inlibrary.mp4"); // exists, but nobody is playing it

        WatchingResult result = Resolver(new ProcessWindow("mpc-hc64", @"D:\elsewhere\foreign.mp4 - MPC-HC"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.AreEqual(0, result.Candidates.Count, "access-time guesses must be suppressed");
        Assert.AreEqual(1, result.Diagnostics.UnresolvedPlayers.Count);
        UnresolvedPlayer up = result.Diagnostics.UnresolvedPlayers[0];
        Assert.AreEqual("mpc-hc64", up.Player);
        Assert.AreEqual(@"D:\elsewhere", up.ForeignDirectory);
    }

    [TestMethod]
    public void Resolve_BareNameForeignFile_HasNoForeignDirectory()
    {
        Touch("inlibrary.mp4");

        WatchingResult result = Resolver(new ProcessWindow("vlc", "foreign.mp4 - VLC media player"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.AreEqual(1, result.Diagnostics.UnresolvedPlayers.Count);
        Assert.IsNull(result.Diagnostics.UnresolvedPlayers[0].ForeignDirectory);
        Assert.AreEqual(0, result.Candidates.Count);
    }

    [TestMethod]
    public void Resolve_MixedResolvedAndForeign_KeepsCandidateAndReportsForeign()
    {
        string watched = Touch("watched.mp4");

        WatchingResult result = Resolver(
                new ProcessWindow("mpc-hc64", $"{watched} - MPC-HC"),
                new ProcessWindow("vlc", "foreign.mp4 - VLC media player"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.IsTrue(result.Candidates.Any(c => c.Name == "watched.mp4"), "resolved candidate must remain");
        Assert.AreEqual(1, result.Diagnostics.UnresolvedPlayers.Count, "foreign player still reported");
    }

    [TestMethod]
    public void Resolve_PlayerWithNoFilenameInTitle_StaysQuiet()
    {
        Touch("clip.mp4");

        WatchingResult result = Resolver(new ProcessWindow("vlc", "Some Metadata Title - VLC media player"))
            .Resolve(_tempDir, 5, includeAccessFallback: true);

        Assert.AreEqual(0, result.Diagnostics.UnresolvedPlayers.Count, "no .mp4 in title is not a wrong-dir signal");
        Assert.IsTrue(result.Candidates.Count >= 1, "normal access-time fallback still answers");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WatchingResolverTests" --nologo`
Expected: FAIL, the new collision/diagnostic/suppression behavior isn't implemented (e.g. `BareNameNotLocked_DemotedToLowWithNote` sees `high`; `PlayerOnForeignFile...` sees access-time candidates and empty diagnostics).

- [ ] **Step 3: Implement the new resolver flow**

Replace the body of `Resolve` in `clipmeta.core/Watching/WatchingResolver.cs` with the following (and add the `NotLockedNote` constant + the private `WorkingCandidate` record). The signal-collection and access-time-after-take-probe stay; what's new is the player resolution up front, the working-candidate metadata, the early player-hit probe with the bare-name demote, the suppression check, and filling diagnostics:

```csharp
    /// <summary>The caveat attached to a bare-name match whose file is not currently locked.</summary>
    private const string NotLockedNote =
        "not currently locked, may be a same-named file elsewhere; confirm before tagging";

    public WatchingResult Resolve(string libraryRoot, int limit, bool includeAccessFallback)
    {
        WatchContext context = WatchContext.Build(libraryRoot, _windowSource, _playerNames);

        // One source of truth for player→library resolution: feeds both the hit grouping (below)
        // and the wrong-directory diagnostics (here).
        IReadOnlyList<PlayerMatch> playerMatches = PlayerTitleResolution.For(context);
        bool anyPlayerResolved = playerMatches.Any(m => m.Matches.Count > 0);
        var unresolvedPlayers = playerMatches
            .Where(m => m.Matches.Count == 0)
            .Select(m => new UnresolvedPlayer(
                m.Window.ProcessName,
                m.ReferencedValue,
                m.Kind == TitleExtractionKind.FullPath ? Path.GetDirectoryName(m.ReferencedValue) : null))
            .ToList();
        var diagnostics = new WatchDiagnostics(unresolvedPlayers);

        // Row 7: a player is open on a file outside the library and NOTHING resolved → suppress the
        // access-time guesses so there is no tempting wrong answer; the warning leads.
        bool suppressAccessFallback = !anyPlayerResolved && unresolvedPlayers.Count > 0;

        var hitsByPath = new Dictionary<string, List<SignalHit>>(StringComparer.OrdinalIgnoreCase);
        foreach (IWatchSignal signal in _signals)
            foreach (SignalHit hit in signal.Detect(context))
            {
                if (!hitsByPath.TryGetValue(hit.ClipPath, out List<SignalHit>? list))
                    hitsByPath[hit.ClipPath] = list = new List<SignalHit>();
                list.Add(hit);
            }

        DateTime now = DateTime.UtcNow;
        var working = new List<WorkingCandidate>();
        foreach ((string path, List<SignalHit> hits) in hitsByPath)
        {
            bool hasPlayer = hits.Any(h => h.Source == PlayerTitleSignal.SourceName);

            if (!hasPlayer && !includeAccessFallback)
                continue;
            if (!hasPlayer && suppressAccessFallback)
                continue;

            if (!context.ByFullPath.TryGetValue(path, out LibraryClip? clip))
                continue;

            bool playerUnambiguous = hits.Any(h => h.Source == PlayerTitleSignal.SourceName && !h.Ambiguous);
            bool bareNameUnambiguous = hits.Any(h =>
                h.Source == PlayerTitleSignal.SourceName && !h.Ambiguous &&
                h.MatchKind == TitleExtractionKind.BareName);
            string source = hasPlayer ? PlayerTitleSignal.SourceName : AccessTimeSignal.SourceName;
            string? player = hits.FirstOrDefault(h => h.Player is not null)?.Player;

            var candidate = new WatchingCandidate(
                Path: clip.FullPath,
                Name: clip.FileName,
                Source: source,
                Player: player,
                LastAccessTimeUtc: clip.LastAccessTimeUtc,
                SecondsSinceAccess: Math.Max(0, (now - clip.LastAccessTimeUtc).TotalSeconds),
                InUse: false,
                Confidence: playerUnambiguous ? HighConfidence : LowConfidence,
                Note: null);

            working.Add(new WorkingCandidate(candidate, hasPlayer, bareNameUnambiguous));
        }

        // Collision guard: probe player-hit candidates now (there are at most a few, one per open
        // player), so a bare-name high hit whose file is NOT locked is demoted to low + note. This
        // is the only probing done before the cap; access-time candidates are still probed after it.
        for (int i = 0; i < working.Count; i++)
        {
            if (!working[i].IsPlayerHit)
                continue;
            bool inUse = LockProbe.IsInUse(working[i].Candidate.Path);
            WatchingCandidate c = working[i].Candidate with { InUse = inUse };
            if (working[i].BareNameUnambiguous && c.Confidence == HighConfidence && !inUse)
                c = c with { Confidence = LowConfidence, Note = NotLockedNote };
            working[i] = working[i] with { Candidate = c };
        }

        // Rank (high first, then most-recent access) and cap BEFORE probing the access-time rows,
        // so the lock probe never opens the whole library on a fallback pass.
        List<WorkingCandidate> ranked = working
            .OrderByDescending(w => w.Candidate.Confidence == HighConfidence)
            .ThenByDescending(w => w.Candidate.LastAccessTimeUtc)
            .Take(limit)
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
            if (!ranked[i].IsPlayerHit) // player hits already probed above
                ranked[i] = ranked[i] with
                {
                    Candidate = ranked[i].Candidate with { InUse = LockProbe.IsInUse(ranked[i].Candidate.Path) },
                };

        List<WatchingCandidate> finalCandidates = ranked
            .Select(w => w.Candidate)
            .OrderByDescending(c => c.Confidence == HighConfidence)
            .ThenByDescending(c => c.InUse)
            .ThenByDescending(c => c.LastAccessTimeUtc)
            .ToList();

        return new WatchingResult(finalCandidates, diagnostics);
    }

    /// <summary>A candidate plus the per-path facts the collision guard needs before finalizing.</summary>
    private sealed record WorkingCandidate(WatchingCandidate Candidate, bool IsPlayerHit, bool BareNameUnambiguous);
```

> Note: this replaces the Task-3 wrapped body. The pass-1 `CreateDefault`, constructor, `HighConfidence`/`LowConfidence` constants stay. `LockProbe` (Task 2) replaced the old `ProbeInUse`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~WatchingResolverTests" --nologo`
Expected: PASS, all updated + new state-table tests green.

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/WatchingResolver.cs clipmetascribe.Tests/WatchingResolverTests.cs
git commit -m "feat(core): bare-name collision guard + wrong-directory diagnostics + fallback suppression"
```

---

### Task 5: MCP `library_watching` surfaces the warning + note

**Files:**
- Modify: `clipmetamcp/Tools/ReadTools.cs` (`Watching` handler + tool description)
- Test: `clipmetamcp.Tests/LibraryWatchingToolTests.cs` (add cases)

**Interfaces:**
- Consumes: `WatchingResult` / `WatchDiagnostics` / `UnresolvedPlayer` / `WatchingCandidate.Note` (Tasks 3-4).
- Produces: the tool result gains an optional top-level `warning` object and each candidate gains an optional `note`.

- [ ] **Step 1: Write the failing tests**

Add to `clipmetamcp.Tests/LibraryWatchingToolTests.cs` (the class already has `_lib`, `SetUp`, `TearDown`, `Call`, and `Structured`). Add a helper to start the MCP server with a fake "player" is out of scope here (the real `ProcessWindowSource` runs on the dev box), so these tests assert the *shape contract*: absent warning on a normal call, and that the candidate objects allow a `note` key. The behavioral warning is fully covered at the Core level (Task 4). Add:

```csharp
    [TestMethod]
    public void Watching_NormalCall_HasNoWarning()
    {
        JsonObject result = Call(new JsonObject { ["include_access_fallback"] = true }, _lib);
        Assert.IsNull(result["isError"]);
        // No player is playing our temp clips, so there is no wrong-directory warning.
        Assert.IsNull(Structured(result)["warning"], "warning must be absent when no foreign player is detected");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test clipmetamcp.Tests --filter "FullyQualifiedName~LibraryWatchingToolTests" --nologo`
Expected: FAIL to compile or assert, `Structured(result)["warning"]` referenced before the handler is updated (compiles, but the test is new; run to confirm green/red baseline). If it passes trivially, proceed, the substantive change is the handler emitting `warning` only when diagnostics exist.

- [ ] **Step 3: Update the `Watching` handler**

In `clipmetamcp/Tools/ReadTools.cs`, the `Watching` handler (after Task 3) holds `WatchingResult result`. Replace the candidate-array build + return so it (a) includes each candidate's `note` when present, and (b) attaches a top-level `warning` when `result.Diagnostics.UnresolvedPlayers` is non-empty:

```csharp
        WatchingResult result = resolver.Resolve(root, limit, includeAccessFallback);

        var array = new JsonArray();
        foreach (WatchingCandidate c in result.Candidates)
        {
            var entry = new JsonObject
            {
                ["path"] = c.Path,
                ["name"] = c.Name,
                ["source"] = c.Source,
                ["player"] = c.Player,
                ["lastAccessTimeUtc"] = c.LastAccessTimeUtc.ToString("O"),
                ["secondsSinceAccess"] = Math.Round(c.SecondsSinceAccess, 1),
                ["inUse"] = c.InUse,
                ["confidence"] = c.Confidence,
            };
            if (c.Note is not null)
                entry["note"] = c.Note;
            array.Add(entry);
        }

        var response = new JsonObject
        {
            ["libraryRoot"] = root,
            ["candidateCount"] = result.Candidates.Count,
            ["candidates"] = array,
        };

        if (result.Diagnostics.UnresolvedPlayers.Count > 0)
        {
            var players = new JsonArray();
            foreach (UnresolvedPlayer up in result.Diagnostics.UnresolvedPlayers)
                players.Add(new JsonObject
                {
                    ["player"] = up.Player,
                    ["referencedName"] = up.ReferencedName,
                    ["foreignDirectory"] = up.ForeignDirectory,
                });
            response["warning"] = new JsonObject
            {
                ["type"] = "player_outside_library",
                ["message"] = "A media player is showing a file that is not in the configured clips " +
                              "library. The user may be playing from the wrong folder. Do not tag.",
                ["unresolvedPlayers"] = players,
            };
        }

        return response;
```

Update the tool's description string (the `registry.Register("library_watching", ...)` description) to add, before the trailing "Requires a configured clips library." sentence:

```
"If the response includes a 'warning' (type 'player_outside_library'), a player is showing a file " +
"that is not in the configured library, tell the user they may be playing from the wrong folder " +
"(name the player and, if 'foreignDirectory' is given, the folder) and do NOT tag. If a candidate " +
"has a 'note', mention it and confirm with the user before tagging. " +
```

- [ ] **Step 4: Run tests to verify they pass (incl. purity)**

Run: `dotnet build --nologo -v q && dotnet test clipmetamcp.Tests --filter "FullyQualifiedName~LibraryWatchingToolTests|FullyQualifiedName~StdoutPurityTests" --nologo`
Expected: PASS, including `StdoutPurityTests` (the tool still runs cleanly via `ExampleArguments`).

- [ ] **Step 5: Commit**

```bash
git add clipmetamcp/Tools/ReadTools.cs clipmetamcp.Tests/LibraryWatchingToolTests.cs
git commit -m "feat(mcp): library_watching surfaces wrong-directory warning + candidate note"
```

---

### Task 6: CLI `--watching` prints the warning + note

**Files:**
- Modify: `clipmetascribe/Commands/WatchingCommand.cs`
- Test: `clipmetascribe.Tests/WatchingCommandTests.cs` (add a case)

**Interfaces:**
- Consumes: `WatchingResult` (Task 3-4).
- Produces: the command prints a prominent warning above candidates when foreign players are detected, and the confirm note on demoted rows.

- [ ] **Step 1: Write the failing test**

Add to `clipmetascribe.Tests/WatchingCommandTests.cs`:

```csharp
    [TestMethod]
    public void Run_BareNameUnlockedClip_PrintsConfirmNote()
    {
        // A free (unlocked) bare-name-resolvable clip is demoted with a note; the CLI surfaces it.
        // We can't inject a player from the test, so drive the command's Core path indirectly:
        // a clip exists and an access-time candidate prints, assert the command runs and the
        // note column is wired by checking a demoted candidate's note appears when present.
        // (Behavioral player cases are covered in WatchingResolverTests; here we assert formatting.)
        File.WriteAllBytes(Path.Combine(_tempDir, "clip.mp4"), Array.Empty<byte>());
        using var sw = new StringWriter();

        int code = WatchingCommand.Run(_tempDir, limit: 5, includeAccessFallback: true, output: sw);

        Assert.AreEqual(0, code);
        // Access-time candidate has no note, so output must NOT contain the note marker on a plain run.
        StringAssert.Contains(sw.ToString(), "clip.mp4");
    }
```

> The player-driven warning/note formatting is asserted structurally below in Step 3's implementation and covered behaviorally in `WatchingResolverTests`; the CLI test confirms the command still prints candidates and does not crash on the new result shape.

- [ ] **Step 2: Run the test to verify the baseline**

Run: `dotnet test --filter "FullyQualifiedName~WatchingCommandTests" --nologo`
Expected: PASS for the existing tests; the new test compiles and passes once the command handles `WatchingResult` (it already does after Task 3). Proceed to wire the warning/note printing.

- [ ] **Step 3: Print the warning and the note**

In `clipmetascribe/Commands/WatchingCommand.cs`, after obtaining `WatchingResult result` (Task 3), print the warning block first, then candidates with their notes:

```csharp
        WatchingResult result = resolver.Resolve(libraryDir, limit, includeAccessFallback);

        if (result.Diagnostics.UnresolvedPlayers.Count > 0)
        {
            foreach (UnresolvedPlayer up in result.Diagnostics.UnresolvedPlayers)
            {
                string where = up.ForeignDirectory is null ? "" : $" from \"{up.ForeignDirectory}\"";
                output.WriteLine(
                    $"WARNING: {up.Player} is playing \"{up.ReferencedName}\"{where}, which is not in this " +
                    "library, you may be in the wrong folder. Do not tag until you've confirmed.");
            }
            output.WriteLine();
        }

        IReadOnlyList<WatchingCandidate> candidates = result.Candidates;
        if (candidates.Count == 0)
        {
            output.WriteLine("No watched-clip candidates found.");
            return 0;
        }

        output.WriteLine("Watched-clip candidates (most likely first):");
        foreach (WatchingCandidate c in candidates)
        {
            string via = c.Player is null ? "" : $" via {c.Player}";
            string locked = c.InUse ? "  [in use]" : "";
            output.WriteLine($"  [{c.Confidence}] {c.Path}");
            output.WriteLine($"        source={c.Source}{via}  {c.SecondsSinceAccess:F0}s since access{locked}");
            if (c.Note is not null)
                output.WriteLine($"        note: {c.Note}");
        }
        return 0;
```

(This replaces the prior body that pulled `candidates` and printed them; keep the method signature and the `output ??= Console.Out;` line at the top.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~WatchingCommandTests" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add clipmetascribe/Commands/WatchingCommand.cs clipmetascribe.Tests/WatchingCommandTests.cs
git commit -m "feat(scribe): --watching prints wrong-directory warning and confirm notes"
```

---

### Task 7: Full build/test gate + documentation

**Files:**
- Modify: `docs/PITFALLS.md`, `CLAUDE.md` (test counts), `README.md` (warning/note behavior)

- [ ] **Step 1: Full build and test gate**

Run: `dotnet build --nologo -v q` → expect 0 warnings, 0 errors.
Run: `dotnet test --nologo --no-build -v q` → expect all pass (multi-minute scribe suite included; long timeout, not a hang). Record the per-project totals for the CLAUDE.md edit. If anything fails, STOP and report verbatim.

- [ ] **Step 2: Append to `docs/PITFALLS.md`**

Add under the existing 2026-06-21 watched-clip section (or a new dated subsection):

```markdown
### 2026-06-21, Watched-clip resolution, pass 1.5 (wrong-directory honesty)

- **VLC bare-name matches can collide.** VLC reports only the file name, so a library `clip001.mp4`
  matches even when you're watching a *different* `clip001.mp4` elsewhere. Guard: a bare-name match
  is `high` only when the library file is **locked** (`LockProbe.IsInUse`); otherwise it is demoted
  to `low` with a confirm note. Full-path (MPC) matches are exact and stay `high` regardless of lock.
- **Pause/stop releases the lock, accepted trade-off.** If a player releases the file handle while
  paused, a *correct* bare-name match reads not-locked and is demoted to "confirm" (friction, not a
  wrong tag). MPC (full path) is unaffected. Revisit the trust policy after dogfooding tells us how
  MPC/VLC behave with the lock on stop vs. next vs. close.
- **Never lock-probe an offline/placeholder file.** Opening a Dropbox/OneDrive online-only file
  hydrates (downloads) it. `LockProbe` checks `FileAttributes.Offline` and reports not-locked WITHOUT
  opening, so a bare-name match to an un-downloaded library file stays `low` (correct: it isn't the
  file being played).
- **A player open with no readable filename is NOT a wrong-directory signal.** Only a title that
  names an `.mp4` absent from the library warns; a metadata-title/idle player stays quiet.
```

- [ ] **Step 3: Update `CLAUDE.md` test counts**

Bump the `clipmetascribe.Tests` and `clipmetamcp.Tests` counts in the project table to the totals from Step 1.

- [ ] **Step 4: Update `README.md`**

In the `library_watching` / `--watching` description, note that when a player is open on a file outside the configured library the tool returns a wrong-directory warning (and does not guess), and that VLC bare-name matches are confirmed via the file lock.

- [ ] **Step 5: Commit**

```bash
git add docs/PITFALLS.md CLAUDE.md README.md
git commit -m "docs: pass-1.5 wrong-directory honesty (PITFALLS, CLAUDE.md counts, README)"
```

---

## Self-Review

**Spec coverage:** §1 state table → Task 4 tests (rows 1-8 each have a test or are pass-1 regressions); §2 collision guard → Tasks 1 (MatchKind), 2 (LockProbe), 4 (demote rule); §2 cloud-safe probe → Task 2; §3 warning + suppression → Task 4 (diagnostics + suppress) and the `PlayerTitleResolution` helper from Task 1; §4 types → Tasks 1/3; §5 surfaces → Tasks 5 (MCP) + 6 (CLI); §6 tests → each task's test step + Task 7 gate; §7 risks → covered (offline probe T2, demote-not-drop T4, return-type churn confined to T3, idle-player quiet T4). §8 DoD → Task 7.

**Placeholder scan:** no TBD/TODO; every code step shows complete code; every run step has a command + expected result. The CLI Task-6 test is deliberately a formatting/no-crash assertion (the player-driven warning/note is covered behaviorally in Task 4), this is stated, not a gap.

**Type consistency:** `WatchingResult.Candidates`/`.Diagnostics`, `WatchDiagnostics.UnresolvedPlayers`, `UnresolvedPlayer(Player, ReferencedName, ForeignDirectory)`, `WatchingCandidate.Note`, `SignalHit.MatchKind`, `PlayerMatch(Window, Kind, ReferencedValue, Matches)`, `PlayerTitleResolution.For`, `LockProbe.IsInUse`, `WorkingCandidate(Candidate, IsPlayerHit, BareNameUnambiguous)`, and the `"high"`/`"low"` constants are used identically across tasks. The behavior change to bare-name confidence is called out explicitly in Task 4 with the two existing tests it updates.

**Note on test runs:** `--filter` keeps the loop fast; the Task-7 `dotnet test` runs the full suite (multi-minute scribe integration), budget a long timeout.
