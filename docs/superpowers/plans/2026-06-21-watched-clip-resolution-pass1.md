# Watched-Clip Resolution (Pass 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve "which library clip is a media player showing right now" into a ranked, confidence-scored candidate list, exposed via an MCP tool and a CLI command — without ever touching the existing write path.

**Architecture:** A new Core concern `clipmeta.core/Watching/` runs a pluggable set of `IWatchSignal` providers (window-title + access-time in pass 1) over a once-enumerated library, aggregates their evidence per clip, and scores confidence by corroboration. A lock probe enriches candidates as a tiebreaker. The only un-CI-able dependency — reading live process window titles — sits behind `IProcessWindowSource`, faked in tests. A separate, single-site hardening stops ClipMeta's own reads from polluting the access-time signal.

**Tech Stack:** C# / .NET 10, `clipmeta.core` (zero NuGet), MSTest. `System.Diagnostics.Process` (Windows-guarded), `System.Text.RegularExpressions` source-generated regex.

## Global Constraints

- **.NET 10**, solution `peckworks-clipmeta.slnx`.
- **Zero external NuGet packages** in production projects (`clipmeta.core`, `clipmetascribe`, `clipmetamcp`). BCL/SDK only. MSTest is the test-project-only exception.
- **CLIs/MCP are thin shells** — all resolution logic lives in `clipmeta.core`. `Program.cs` / tool handlers parse arguments and call Core.
- **Open for extension** — a new player or detection method is a new `IWatchSignal` / `IProcessWindowSource`, never an edit to the resolver.
- **No fabricated paths** — every returned path MUST come from clips actually enumerated under the library root. A window title selects among enumerated clips; it never constructs a path.
- **Cross-platform build** — `clipmeta.core` and all tests MUST build and pass on clip-less CI (Linux). Windows-only APIs guarded by `OperatingSystem.IsWindows()` + `[SupportedOSPlatform("windows")]`; CA1416 must stay clean.
- **Build gate:** `dotnet build --nologo -v q` → 0 warnings, 0 errors. **Test gate:** `dotnet test --nologo --no-build -v q` → all pass.
- XML doc comments on all public types/methods; named constants, no magic numbers.

## File Structure (namespace `ClipMetaCore.Watching` unless noted)

> **Note (refinement of the spec's file tree):** the two new interfaces (`IProcessWindowSource`, `IWatchSignal`) live in `Watching/` rather than `Abstractions/`. They are watching-specific contracts (unlike the cross-cutting `IMediaParser`), and `IWatchSignal` depends on `WatchContext` which lives in `Watching/` — co-locating them keeps the concern cohesive and avoids `Abstractions/` depending on `Watching/`.

- `clipmeta.core/Mp4/AccessTimeGuard.cs` — capture/restore `LastAccessTimeUtc`, best-effort (Mp4 namespace).
- `clipmeta.core/Mp4/Mp4Parser.cs` — **modify** `ParseFile` to wrap the open in the guard.
- `clipmeta.core/Watching/ProcessWindow.cs` — `(ProcessName, WindowTitle)` record struct.
- `clipmeta.core/Watching/IProcessWindowSource.cs` — the one fakeable seam.
- `clipmeta.core/Watching/EmptyProcessWindowSource.cs` — non-Windows default.
- `clipmeta.core/Watching/WindowsProcessWindowSource.cs` — Windows implementation.
- `clipmeta.core/Watching/ProcessWindowSource.cs` — `ForCurrentPlatform()` factory.
- `clipmeta.core/Watching/MediaPlayers.cs` — extensible known-player name list.
- `clipmeta.core/Watching/PlayerTitleParser.cs` — pure title → `.mp4` reference.
- `clipmeta.core/Watching/LibraryClip.cs` — enumerated clip record.
- `clipmeta.core/Watching/SignalHit.cs` — one piece of evidence.
- `clipmeta.core/Watching/IWatchSignal.cs` — one confidence signal.
- `clipmeta.core/Watching/WatchContext.cs` — shared inputs + `Build`.
- `clipmeta.core/Watching/PlayerTitleSignal.cs` — pass-1 signal.
- `clipmeta.core/Watching/AccessTimeSignal.cs` — pass-1 signal.
- `clipmeta.core/Watching/WatchingCandidate.cs` — one result row.
- `clipmeta.core/Watching/WatchingResolver.cs` — aggregate + score + rank.
- `clipmetamcp/Tools/ReadTools.cs` — **modify** to register `library_watching`.
- `clipmetascribe/Commands/WatchingCommand.cs` — `--watching` command.
- `clipmetascribe/Program.cs` — **modify** to wire `--watching` / `--limit` / `--no-access-fallback`.
- Tests: `clipmetascribe.Tests/{AccessTimeGuardTests,PlayerTitleParserTests,ProcessWindowSourceTests,WatchContextTests,WatchSignalsTests,WatchingResolverTests,WatchingCommandTests}.cs`, `clipmetascribe.Tests/Helpers/FakeProcessWindowSource.cs`, `clipmetamcp.Tests/LibraryWatchingToolTests.cs`.

---

### Task 1: Access-time hardening (`AccessTimeGuard` + parser choke point)

**Files:**
- Create: `clipmeta.core/Mp4/AccessTimeGuard.cs`
- Modify: `clipmeta.core/Mp4/Mp4Parser.cs:59-63`
- Test: `clipmetascribe.Tests/AccessTimeGuardTests.cs`

**Interfaces:**
- Produces: `public readonly struct AccessTimeGuard : IDisposable` with ctor `AccessTimeGuard(string path)`; captures `File.GetLastAccessTimeUtc(path)` on construct, restores on `Dispose()`, best-effort.

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/AccessTimeGuardTests.cs`:

```csharp
using ClipMetaCore.Mp4;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class AccessTimeGuardTests
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
    public void Guard_RestoresLastAccessTime_AfterAnInterveningRead()
    {
        string path = Path.Combine(_tempDir, "f.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        DateTime original = DateTime.UtcNow.AddDays(-3);
        File.SetLastAccessTimeUtc(path, original);

        using (new AccessTimeGuard(path))
        {
            // Simulate a read bumping the access time.
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }

        Assert.AreEqual(
            original, File.GetLastAccessTimeUtc(path),
            "guard must restore the captured access time on dispose");
    }

    [TestMethod]
    public void Guard_MissingFile_DoesNotThrow()
    {
        string path = Path.Combine(_tempDir, "does-not-exist.bin");
        // Construction captures (best-effort) and disposal restores (best-effort); neither throws.
        using (new AccessTimeGuard(path)) { }
    }

    [TestMethod]
    public void ParseFile_DoesNotChangeLastAccessTime()
    {
        if (!TestClipsLocator.PristineClipsPresent())
        {
            Assert.Inconclusive("No test clips in testclips/pristine — skipped (e.g. CI).");
            return;
        }

        string clip = Path.Combine(_tempDir, "clip.mp4");
        File.Copy(TestClipsLocator.SmallestPristine(), clip);
        DateTime original = DateTime.UtcNow.AddDays(-2);
        File.SetLastAccessTimeUtc(clip, original);

        _ = ClipMetaCore.Mp4.Mp4Parser.ParseFile(clip);

        // Tolerance: filesystem access-time granularity can be coarse; assert within 5 seconds.
        TimeSpan drift = (File.GetLastAccessTimeUtc(clip) - original).Duration();
        Assert.IsTrue(drift < TimeSpan.FromSeconds(5),
            $"reading a clip changed its last-access time by {drift}");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AccessTimeGuardTests" --nologo`
Expected: FAIL — `AccessTimeGuard` does not exist (compile error).

- [ ] **Step 3: Create `AccessTimeGuard`**

Create `clipmeta.core/Mp4/AccessTimeGuard.cs`:

```csharp
namespace ClipMetaCore.Mp4;

/// <summary>
/// Captures a file's <see cref="File.GetLastAccessTimeUtc(string)"/> on construction and restores
/// it on <see cref="Dispose"/>, best-effort. ClipMeta's own reads would otherwise bump the access
/// time and pollute the watched-clip access-time signal. Restoring is itself a metadata write that
/// can fail (file locked by a player, read-only, removed); such failures are swallowed — preserving
/// the signal must never break a read.
/// </summary>
public readonly struct AccessTimeGuard : IDisposable
{
    private readonly string _path;
    private readonly DateTime _original;
    private readonly bool _captured;

    /// <summary>Captures the current last-access time of <paramref name="path"/>, best-effort.</summary>
    public AccessTimeGuard(string path)
    {
        _path = path;
        try
        {
            _original = File.GetLastAccessTimeUtc(path);
            _captured = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _original = default;
            _captured = false;
        }
    }

    /// <summary>Restores the captured last-access time, best-effort (failures swallowed).</summary>
    public void Dispose()
    {
        if (!_captured)
            return;
        try
        {
            File.SetLastAccessTimeUtc(_path, _original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // best-effort: restoring is a write that can lose to a lock or a vanished file.
        }
    }
}
```

- [ ] **Step 4: Wire the guard into the parse choke point**

Modify `clipmeta.core/Mp4/Mp4Parser.cs` `ParseFile` (lines 59-63). Declare the guard **before** the stream so it disposes (restores) **after** the stream closes:

```csharp
    public static BoxNode ParseFile(string path)
    {
        // Capture/restore last-access time around the read so ClipMeta's own reads don't pollute
        // the watched-clip access-time signal (best-effort; see AccessTimeGuard).
        using var accessTimeGuard = new AccessTimeGuard(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Parse(fs);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~AccessTimeGuardTests" --nologo`
Expected: PASS (the `ParseFile` test reports Inconclusive on a clip-less machine, which counts as not-failed).

- [ ] **Step 6: Commit**

```bash
git add clipmeta.core/Mp4/AccessTimeGuard.cs clipmeta.core/Mp4/Mp4Parser.cs clipmetascribe.Tests/AccessTimeGuardTests.cs
git commit -m "feat(core): preserve last-access time across reads (AccessTimeGuard at parse choke point)"
```

---

### Task 2: `PlayerTitleParser` (pure title parsing)

**Files:**
- Create: `clipmeta.core/Watching/PlayerTitleParser.cs`
- Test: `clipmetascribe.Tests/PlayerTitleParserTests.cs`

**Interfaces:**
- Produces:
  - `public enum TitleExtractionKind { FullPath, BareName }`
  - `public readonly record struct TitleExtraction(TitleExtractionKind Kind, string Value)`
  - `public static partial class PlayerTitleParser { static TitleExtraction? Extract(string? title); }`

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/PlayerTitleParserTests.cs`:

```csharp
using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class PlayerTitleParserTests
{
    [TestMethod]
    public void Extract_MpcFullPathTitle_ReturnsFullPath()
    {
        TitleExtraction? r = PlayerTitleParser.Extract(@"C:\clips\2026.06.20\clip001.mp4 - MPC-HC");
        Assert.IsNotNull(r);
        Assert.AreEqual(TitleExtractionKind.FullPath, r.Value.Kind);
        Assert.AreEqual(@"C:\clips\2026.06.20\clip001.mp4", r.Value.Value);
    }

    [TestMethod]
    public void Extract_VlcBareNameTitle_ReturnsBareName()
    {
        TitleExtraction? r = PlayerTitleParser.Extract("clip001.mp4 - VLC media player");
        Assert.IsNotNull(r);
        Assert.AreEqual(TitleExtractionKind.BareName, r.Value.Kind);
        Assert.AreEqual("clip001.mp4", r.Value.Value);
    }

    [TestMethod]
    public void Extract_TitleWithoutMp4_ReturnsNull()
    {
        // VLC showing an embedded metadata title, or a stopped player.
        Assert.IsNull(PlayerTitleParser.Extract("My Awesome Montage - VLC media player"));
    }

    [TestMethod]
    public void Extract_NullOrEmpty_ReturnsNull()
    {
        Assert.IsNull(PlayerTitleParser.Extract(null));
        Assert.IsNull(PlayerTitleParser.Extract("   "));
    }

    [TestMethod]
    public void Extract_FullPathPreferredOverBareName()
    {
        // A title containing a full path must yield the full path, not just the filename tail.
        TitleExtraction? r = PlayerTitleParser.Extract(@"Now playing C:\a\b\clip.mp4");
        Assert.IsNotNull(r);
        Assert.AreEqual(TitleExtractionKind.FullPath, r.Value.Kind);
        Assert.AreEqual(@"C:\a\b\clip.mp4", r.Value.Value);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PlayerTitleParserTests" --nologo`
Expected: FAIL — `PlayerTitleParser` does not exist.

- [ ] **Step 3: Create `PlayerTitleParser`**

Create `clipmeta.core/Watching/PlayerTitleParser.cs`:

```csharp
using System.Text.RegularExpressions;

namespace ClipMetaCore.Watching;

/// <summary>Whether a parsed title reference is a full path or a bare filename.</summary>
public enum TitleExtractionKind
{
    /// <summary>A drive-rooted absolute path, e.g. MPC-HC's title format.</summary>
    FullPath,

    /// <summary>A bare file name, e.g. VLC's "<c>name.mp4 - VLC media player</c>" format.</summary>
    BareName,
}

/// <summary>One <c>.mp4</c> reference extracted from a player window title.</summary>
public readonly record struct TitleExtraction(TitleExtractionKind Kind, string Value);

/// <summary>
/// Pure extraction of an <c>.mp4</c> reference from a media-player window title. Tries a
/// drive-rooted full path first (MPC-HC style), then a bare file name (VLC style). A title with no
/// <c>.mp4</c> (an embedded metadata title, a stopped player, a custom format) yields null. This
/// type only parses text; resolving a reference to a real library clip is the signal's job.
/// </summary>
public static partial class PlayerTitleParser
{
    // Drive-rooted absolute path ending in .mp4. Excludes characters illegal in Windows paths
    // (and the pipe/quote a title might use as a separator) so the match stops at the path's edge.
    [GeneratedRegex(@"([A-Za-z]:\\[^""|*?<>]+?\.mp4)", RegexOptions.IgnoreCase)]
    private static partial Regex FullPathRegex();

    // Bare file name ending in .mp4: no path separators, drive colon, or wildcard/quote chars.
    [GeneratedRegex(@"([^\\/:*?""<>|]+?\.mp4)", RegexOptions.IgnoreCase)]
    private static partial Regex BareNameRegex();

    /// <summary>Extracts the first <c>.mp4</c> reference from <paramref name="title"/>, or null.</summary>
    public static TitleExtraction? Extract(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        Match full = FullPathRegex().Match(title);
        if (full.Success)
            return new TitleExtraction(TitleExtractionKind.FullPath, full.Groups[1].Value);

        Match bare = BareNameRegex().Match(title);
        if (bare.Success)
            return new TitleExtraction(TitleExtractionKind.BareName, bare.Groups[1].Value);

        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~PlayerTitleParserTests" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/PlayerTitleParser.cs clipmetascribe.Tests/PlayerTitleParserTests.cs
git commit -m "feat(core): PlayerTitleParser extracts .mp4 references from player window titles"
```

---

### Task 3: Process-window seam (`IProcessWindowSource`, implementations, factory, player list)

**Files:**
- Create: `clipmeta.core/Watching/ProcessWindow.cs`, `IProcessWindowSource.cs`, `EmptyProcessWindowSource.cs`, `WindowsProcessWindowSource.cs`, `ProcessWindowSource.cs`, `MediaPlayers.cs`
- Test: `clipmetascribe.Tests/ProcessWindowSourceTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct ProcessWindow(string ProcessName, string WindowTitle)`
  - `public interface IProcessWindowSource { IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> processNames); }`
  - `public sealed class EmptyProcessWindowSource : IProcessWindowSource { static EmptyProcessWindowSource Instance { get; } }`
  - `[SupportedOSPlatform("windows")] public sealed class WindowsProcessWindowSource : IProcessWindowSource`
  - `public static class ProcessWindowSource { static IProcessWindowSource ForCurrentPlatform(); }`
  - `public static class MediaPlayers { static IReadOnlyList<string> KnownProcessNames { get; } }`

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/ProcessWindowSourceTests.cs`:

```csharp
using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ProcessWindowSourceTests
{
    [TestMethod]
    public void Empty_ReturnsNoWindows()
    {
        IReadOnlyList<ProcessWindow> windows =
            EmptyProcessWindowSource.Instance.GetPlayerWindows(MediaPlayers.KnownProcessNames);
        Assert.AreEqual(0, windows.Count);
    }

    [TestMethod]
    public void ForCurrentPlatform_ReturnsUsableSource_ThatDoesNotThrow()
    {
        IProcessWindowSource source = ProcessWindowSource.ForCurrentPlatform();
        // On Linux CI this is the empty source; on Windows it enumerates real processes.
        // Either way it must return a list without throwing.
        IReadOnlyList<ProcessWindow> windows = source.GetPlayerWindows(MediaPlayers.KnownProcessNames);
        Assert.IsNotNull(windows);
    }

    [TestMethod]
    public void KnownProcessNames_IncludeSeededPlayers()
    {
        CollectionAssert.Contains((System.Collections.ICollection)MediaPlayers.KnownProcessNames, "vlc");
        CollectionAssert.Contains((System.Collections.ICollection)MediaPlayers.KnownProcessNames, "mpc-hc64");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ProcessWindowSourceTests" --nologo`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Create the seam types**

Create `clipmeta.core/Watching/ProcessWindow.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>One running process's main-window title, captured at a moment in time.</summary>
public readonly record struct ProcessWindow(string ProcessName, string WindowTitle);
```

Create `clipmeta.core/Watching/IProcessWindowSource.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// Supplies the window titles of currently-running media players. The one dependency that cannot
/// run on clip-less CI (live process inspection is Windows-only), so it is isolated behind this
/// interface and faked in tests.
/// </summary>
public interface IProcessWindowSource
{
    /// <summary>
    /// Returns one <see cref="ProcessWindow"/> per running process whose name matches one of
    /// <paramref name="processNames"/> (case-insensitive) and has a non-empty main-window title.
    /// Implementations MUST NOT throw for a single inaccessible or exited process — skip and
    /// continue.
    /// </summary>
    IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> processNames);
}
```

Create `clipmeta.core/Watching/EmptyProcessWindowSource.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// A source that reports no players — the default on non-Windows platforms and anywhere a real
/// source is not wired. Resolution then relies on the access-time signal alone.
/// </summary>
public sealed class EmptyProcessWindowSource : IProcessWindowSource
{
    /// <summary>The shared instance.</summary>
    public static EmptyProcessWindowSource Instance { get; } = new();

    private EmptyProcessWindowSource() { }

    /// <inheritdoc/>
    public IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> processNames) =>
        Array.Empty<ProcessWindow>();
}
```

Create `clipmeta.core/Watching/WindowsProcessWindowSource.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.Versioning;

namespace ClipMetaCore.Watching;

/// <summary>
/// Reads media-player window titles via <see cref="Process.MainWindowTitle"/> (Windows-only).
/// Title reads are wrapped per-process: a process can exit between enumeration and access, or deny
/// access to its window, and that must skip the process, never fail the whole snapshot.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessWindowSource : IProcessWindowSource
{
    /// <inheritdoc/>
    public IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> processNames)
    {
        var names = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        var results = new List<ProcessWindow>();

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (!names.Contains(process.ProcessName))
                    continue;
                string title = process.MainWindowTitle;
                if (!string.IsNullOrEmpty(title))
                    results.Add(new ProcessWindow(process.ProcessName, title));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Process exited mid-enumeration, or its window is inaccessible — skip it.
            }
            finally
            {
                process.Dispose();
            }
        }

        return results;
    }
}
```

Create `clipmeta.core/Watching/ProcessWindowSource.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>Selects the right <see cref="IProcessWindowSource"/> for the running platform.</summary>
public static class ProcessWindowSource
{
    /// <summary>
    /// Returns a Windows process source when running on Windows, otherwise the empty source. The
    /// <see cref="OperatingSystem.IsWindows"/> guard is what makes constructing the Windows source
    /// CA1416-safe.
    /// </summary>
    public static IProcessWindowSource ForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsProcessWindowSource() : EmptyProcessWindowSource.Instance;
}
```

Create `clipmeta.core/Watching/MediaPlayers.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>The media players ClipMeta recognizes by process name.</summary>
public static class MediaPlayers
{
    /// <summary>
    /// Process names (without the <c>.exe</c> suffix, as <see cref="System.Diagnostics.Process.ProcessName"/>
    /// reports them) of recognized players. Matched case-insensitively. <b>Append here to support a
    /// new player</b> — no other code changes are required.
    /// </summary>
    public static IReadOnlyList<string> KnownProcessNames { get; } = new[]
    {
        "mpc-hc", "mpc-hc64", "mpc-be", "vlc", "mpv", "wmplayer", "PotPlayer",
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~ProcessWindowSourceTests" --nologo`
Expected: PASS. Confirm the build reports **0 warnings** (CA1416 platform correctness).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/ProcessWindow.cs clipmeta.core/Watching/IProcessWindowSource.cs clipmeta.core/Watching/EmptyProcessWindowSource.cs clipmeta.core/Watching/WindowsProcessWindowSource.cs clipmeta.core/Watching/ProcessWindowSource.cs clipmeta.core/Watching/MediaPlayers.cs clipmetascribe.Tests/ProcessWindowSourceTests.cs
git commit -m "feat(core): cross-platform process-window seam + extensible player list"
```

---

### Task 4: Signal model (`LibraryClip`, `SignalHit`, `IWatchSignal`, `WatchContext`)

**Files:**
- Create: `clipmeta.core/Watching/LibraryClip.cs`, `SignalHit.cs`, `IWatchSignal.cs`, `WatchContext.cs`
- Create: `clipmetascribe.Tests/Helpers/FakeProcessWindowSource.cs`
- Test: `clipmetascribe.Tests/WatchContextTests.cs`

**Interfaces:**
- Consumes: `IProcessWindowSource`, `ProcessWindow` (Task 3).
- Produces:
  - `public sealed record LibraryClip(string FullPath, string FileName, DateTime LastAccessTimeUtc)`
  - `public sealed record SignalHit(string ClipPath, string Source, string? Player, bool Ambiguous)`
  - `public interface IWatchSignal { string Name { get; } IEnumerable<SignalHit> Detect(WatchContext context); }`
  - `public sealed class WatchContext` with init-only `LibraryClips` (`IReadOnlyList<LibraryClip>`), `ByFileName` (`IReadOnlyDictionary<string, IReadOnlyList<LibraryClip>>`), `ByFullPath` (`IReadOnlyDictionary<string, LibraryClip>`), `PlayerWindows` (`IReadOnlyList<ProcessWindow>`); and `static WatchContext Build(string libraryRoot, IProcessWindowSource source, IReadOnlyCollection<string> playerNames)`.
  - Test helper: `internal sealed class FakeProcessWindowSource : IProcessWindowSource` constructed from `params ProcessWindow[]`.

- [ ] **Step 1: Write the failing tests + the fake source helper**

Create `clipmetascribe.Tests/Helpers/FakeProcessWindowSource.cs`:

```csharp
using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests.Helpers;

/// <summary>Returns a fixed set of player windows, ignoring the name filter — for resolver tests.</summary>
internal sealed class FakeProcessWindowSource : IProcessWindowSource
{
    private readonly IReadOnlyList<ProcessWindow> _windows;

    public FakeProcessWindowSource(params ProcessWindow[] windows) => _windows = windows;

    public IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> processNames) => _windows;
}
```

Create `clipmetascribe.Tests/WatchContextTests.cs`:

```csharp
using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class WatchContextTests
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

    private string Touch(string relativePath)
    {
        string path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    [TestMethod]
    public void Build_EnumeratesClipsRecursively_AndIndexesByNameAndPath()
    {
        string a = Touch("a.mp4");
        string b = Touch(Path.Combine("sub", "b.mp4"));
        Touch("notes.txt"); // must be ignored

        WatchContext ctx = WatchContext.Build(_tempDir, EmptyProcessWindowSource.Instance, MediaPlayers.KnownProcessNames);

        Assert.AreEqual(2, ctx.LibraryClips.Count);
        Assert.IsTrue(ctx.ByFullPath.ContainsKey(a));
        Assert.IsTrue(ctx.ByFullPath.ContainsKey(b));
        Assert.AreEqual(1, ctx.ByFileName["a.mp4"].Count);
        Assert.AreEqual(b, ctx.ByFileName["b.mp4"].Single().FullPath);
    }

    [TestMethod]
    public void Build_DuplicateFileNames_GroupedUnderOneNameKey()
    {
        Touch("dup.mp4");
        Touch(Path.Combine("sub", "dup.mp4"));

        WatchContext ctx = WatchContext.Build(_tempDir, EmptyProcessWindowSource.Instance, MediaPlayers.KnownProcessNames);

        Assert.AreEqual(2, ctx.ByFileName["dup.mp4"].Count);
    }

    [TestMethod]
    public void Build_PopulatesPlayerWindowsFromSource()
    {
        var source = new FakeProcessWindowSource(new ProcessWindow("vlc", "x.mp4 - VLC media player"));

        WatchContext ctx = WatchContext.Build(_tempDir, source, MediaPlayers.KnownProcessNames);

        Assert.AreEqual(1, ctx.PlayerWindows.Count);
        Assert.AreEqual("vlc", ctx.PlayerWindows[0].ProcessName);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WatchContextTests" --nologo`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Create the model types**

Create `clipmeta.core/Watching/LibraryClip.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>A clip enumerated from the library, with the facts resolution needs.</summary>
/// <param name="FullPath">Absolute path to the .mp4 file.</param>
/// <param name="FileName">File name only (for bare-title matching).</param>
/// <param name="LastAccessTimeUtc">Last-access time at enumeration.</param>
public sealed record LibraryClip(string FullPath, string FileName, DateTime LastAccessTimeUtc);
```

Create `clipmeta.core/Watching/SignalHit.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// One signal's evidence that a particular clip is the one being watched. Several signals may emit
/// a hit for the same clip; the resolver groups hits by path and scores confidence by corroboration.
/// </summary>
/// <param name="ClipPath">Path of an enumerated library clip — never a fabricated path.</param>
/// <param name="Source">The emitting signal's name (also used as the candidate source).</param>
/// <param name="Player">Process name when the evidence came from a player; otherwise null.</param>
/// <param name="Ambiguous">True when this signal alone could not disambiguate the clip.</param>
public sealed record SignalHit(string ClipPath, string Source, string? Player, bool Ambiguous);
```

Create `clipmeta.core/Watching/IWatchSignal.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// One pluggable confidence signal. Adding a new player or detection method means adding a new
/// implementation and registering it — never editing the resolver.
/// </summary>
public interface IWatchSignal
{
    /// <summary>Stable identifier, also used as <see cref="SignalHit.Source"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Emits zero or more evidence hits for the current moment. MUST only reference clips present
    /// in <see cref="WatchContext.LibraryClips"/> — a signal selects among already-enumerated clips,
    /// it never constructs a path. MUST NOT throw for ordinary failures (player closed, file gone,
    /// source unreadable): emit nothing instead.
    /// </summary>
    IEnumerable<SignalHit> Detect(WatchContext context);
}
```

Create `clipmeta.core/Watching/WatchContext.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// Shared inputs for one resolution pass. The library is enumerated exactly once here so signals
/// don't each re-scan it, and the lookups make title→clip resolution O(1).
/// </summary>
public sealed class WatchContext
{
    /// <summary>Every clip under the library root, enumerated once.</summary>
    public required IReadOnlyList<LibraryClip> LibraryClips { get; init; }

    /// <summary>File name → clip(s), for resolving a bare title filename (case-insensitive).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<LibraryClip>> ByFileName { get; init; }

    /// <summary>Full path → clip, for validating a full-path title (case-insensitive).</summary>
    public required IReadOnlyDictionary<string, LibraryClip> ByFullPath { get; init; }

    /// <summary>Window titles of running players (empty on non-Windows / when none run).</summary>
    public required IReadOnlyList<ProcessWindow> PlayerWindows { get; init; }

    /// <summary>
    /// Enumerates <paramref name="libraryRoot"/> for .mp4 files (recursive), builds the lookups,
    /// and captures the player-window snapshot from <paramref name="source"/>. Files whose access
    /// time cannot be read are skipped (a vanished/locked file must not abort the whole pass).
    /// </summary>
    public static WatchContext Build(
        string libraryRoot, IProcessWindowSource source, IReadOnlyCollection<string> playerNames)
    {
        var clips = new List<LibraryClip>();
        foreach (string path in Directory.EnumerateFiles(libraryRoot, "*.mp4", SearchOption.AllDirectories))
        {
            DateTime accessTime;
            try
            {
                accessTime = File.GetLastAccessTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            clips.Add(new LibraryClip(path, Path.GetFileName(path), accessTime));
        }

        var byName = new Dictionary<string, List<LibraryClip>>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, LibraryClip>(StringComparer.OrdinalIgnoreCase);
        foreach (LibraryClip clip in clips)
        {
            if (!byName.TryGetValue(clip.FileName, out List<LibraryClip>? list))
                byName[clip.FileName] = list = new List<LibraryClip>();
            list.Add(clip);
            byPath[clip.FullPath] = clip;
        }

        return new WatchContext
        {
            LibraryClips = clips,
            ByFileName = byName.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<LibraryClip>)kv.Value, StringComparer.OrdinalIgnoreCase),
            ByFullPath = byPath,
            PlayerWindows = source.GetPlayerWindows(playerNames),
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~WatchContextTests" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/LibraryClip.cs clipmeta.core/Watching/SignalHit.cs clipmeta.core/Watching/IWatchSignal.cs clipmeta.core/Watching/WatchContext.cs clipmetascribe.Tests/Helpers/FakeProcessWindowSource.cs clipmetascribe.Tests/WatchContextTests.cs
git commit -m "feat(core): IWatchSignal model + WatchContext (single library enumeration)"
```

---

### Task 5: Pass-1 signals (`PlayerTitleSignal`, `AccessTimeSignal`)

**Files:**
- Create: `clipmeta.core/Watching/PlayerTitleSignal.cs`, `AccessTimeSignal.cs`
- Test: `clipmetascribe.Tests/WatchSignalsTests.cs`

**Interfaces:**
- Consumes: `IWatchSignal`, `WatchContext`, `SignalHit`, `PlayerTitleParser`, `LibraryClip` (Tasks 2, 4).
- Produces:
  - `public sealed class PlayerTitleSignal : IWatchSignal` with `public const string SourceName = "player_title";`
  - `public sealed class AccessTimeSignal : IWatchSignal` with `public const string SourceName = "access_time";`

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/WatchSignalsTests.cs`:

```csharp
using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class WatchSignalsTests
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

    private string Touch(string relativePath)
    {
        string path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private WatchContext Build(params ProcessWindow[] windows) =>
        WatchContext.Build(_tempDir, new FakeProcessWindowSource(windows), MediaPlayers.KnownProcessNames);

    [TestMethod]
    public void PlayerTitle_BareNameInLibrary_UnambiguousHit()
    {
        string clip = Touch("clip.mp4");
        WatchContext ctx = Build(new ProcessWindow("vlc", "clip.mp4 - VLC media player"));

        List<SignalHit> hits = new PlayerTitleSignal().Detect(ctx).ToList();

        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(clip, hits[0].ClipPath);
        Assert.AreEqual(PlayerTitleSignal.SourceName, hits[0].Source);
        Assert.AreEqual("vlc", hits[0].Player);
        Assert.IsFalse(hits[0].Ambiguous);
    }

    [TestMethod]
    public void PlayerTitle_BareNameNotInLibrary_Dropped()
    {
        Touch("present.mp4");
        WatchContext ctx = Build(new ProcessWindow("vlc", "absent.mp4 - VLC media player"));

        Assert.AreEqual(0, new PlayerTitleSignal().Detect(ctx).Count());
    }

    [TestMethod]
    public void PlayerTitle_NameMatchesMultipleClips_AmbiguousHits()
    {
        Touch("dup.mp4");
        Touch(Path.Combine("sub", "dup.mp4"));
        WatchContext ctx = Build(new ProcessWindow("vlc", "dup.mp4 - VLC media player"));

        List<SignalHit> hits = new PlayerTitleSignal().Detect(ctx).ToList();

        Assert.AreEqual(2, hits.Count);
        Assert.IsTrue(hits.All(h => h.Ambiguous));
    }

    [TestMethod]
    public void PlayerTitle_MultiplePlayers_AllAmbiguous()
    {
        Touch("a.mp4");
        Touch("b.mp4");
        WatchContext ctx = Build(
            new ProcessWindow("vlc", "a.mp4 - VLC media player"),
            new ProcessWindow("mpc-hc64", "b.mp4"));

        List<SignalHit> hits = new PlayerTitleSignal().Detect(ctx).ToList();

        Assert.AreEqual(2, hits.Count);
        Assert.IsTrue(hits.All(h => h.Ambiguous));
    }

    [TestMethod]
    public void AccessTime_OrdersMostRecentFirst_AllAmbiguous()
    {
        string older = Touch("older.mp4");
        string newer = Touch("newer.mp4");
        File.SetLastAccessTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.SetLastAccessTimeUtc(newer, DateTime.UtcNow);
        WatchContext ctx = Build();

        List<SignalHit> hits = new AccessTimeSignal().Detect(ctx).ToList();

        Assert.AreEqual(newer, hits[0].ClipPath);
        Assert.AreEqual(older, hits[1].ClipPath);
        Assert.IsTrue(hits.All(h => h.Ambiguous && h.Player is null));
        Assert.AreEqual(AccessTimeSignal.SourceName, hits[0].Source);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WatchSignalsTests" --nologo`
Expected: FAIL — signal types do not exist.

- [ ] **Step 3: Create the signals**

Create `clipmeta.core/Watching/PlayerTitleSignal.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// Resolves a clip from a player's window title. Drops anything that does not resolve to a clip
/// inside the enumerated library (no fabrication). A hit is ambiguous when more than one recognized
/// player has a resolvable title, or when a bare filename matches more than one library clip.
/// </summary>
public sealed class PlayerTitleSignal : IWatchSignal
{
    /// <summary>The signal name and the source tag on its hits.</summary>
    public const string SourceName = "player_title";

    /// <inheritdoc/>
    public string Name => SourceName;

    /// <inheritdoc/>
    public IEnumerable<SignalHit> Detect(WatchContext context)
    {
        var perPlayer = new List<(ProcessWindow Window, IReadOnlyList<LibraryClip> Clips)>();
        foreach (ProcessWindow window in context.PlayerWindows)
        {
            TitleExtraction? extraction = PlayerTitleParser.Extract(window.WindowTitle);
            if (extraction is null)
                continue;
            IReadOnlyList<LibraryClip> matches = Resolve(extraction.Value, context);
            if (matches.Count > 0)
                perPlayer.Add((window, matches));
        }

        bool multiplePlayers = perPlayer.Count > 1;
        foreach ((ProcessWindow window, IReadOnlyList<LibraryClip> clips) in perPlayer)
        {
            bool ambiguousFile = clips.Count > 1;
            foreach (LibraryClip clip in clips)
                yield return new SignalHit(clip.FullPath, SourceName, window.ProcessName,
                    Ambiguous: multiplePlayers || ambiguousFile);
        }
    }

    private static IReadOnlyList<LibraryClip> Resolve(TitleExtraction extraction, WatchContext context)
    {
        if (extraction.Kind == TitleExtractionKind.FullPath)
        {
            return context.ByFullPath.TryGetValue(extraction.Value, out LibraryClip? clip)
                ? new[] { clip }
                : Array.Empty<LibraryClip>();
        }

        return context.ByFileName.TryGetValue(extraction.Value, out IReadOnlyList<LibraryClip>? list)
            ? list
            : Array.Empty<LibraryClip>();
    }
}
```

Create `clipmeta.core/Watching/AccessTimeSignal.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// Emits library clips ordered by most-recently-accessed first. Recency alone is never certain
/// (indexers, AV, other apps bump access time), so every hit is ambiguous; the resolver only
/// surfaces these as the fallback / corroborating signal.
/// </summary>
public sealed class AccessTimeSignal : IWatchSignal
{
    /// <summary>The signal name and the source tag on its hits.</summary>
    public const string SourceName = "access_time";

    /// <inheritdoc/>
    public string Name => SourceName;

    /// <inheritdoc/>
    public IEnumerable<SignalHit> Detect(WatchContext context)
    {
        foreach (LibraryClip clip in context.LibraryClips.OrderByDescending(c => c.LastAccessTimeUtc))
            yield return new SignalHit(clip.FullPath, SourceName, Player: null, Ambiguous: true);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~WatchSignalsTests" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/PlayerTitleSignal.cs clipmeta.core/Watching/AccessTimeSignal.cs clipmetascribe.Tests/WatchSignalsTests.cs
git commit -m "feat(core): player-title and access-time watch signals"
```

---

### Task 6: `WatchingResolver` + `WatchingCandidate` (aggregate, score, rank, lock probe)

**Files:**
- Create: `clipmeta.core/Watching/WatchingCandidate.cs`, `WatchingResolver.cs`
- Test: `clipmetascribe.Tests/WatchingResolverTests.cs`

**Interfaces:**
- Consumes: `IWatchSignal`, `IProcessWindowSource`, `WatchContext`, `SignalHit`, `PlayerTitleSignal`, `AccessTimeSignal`, `MediaPlayers` (Tasks 3-5).
- Produces:
  - `public sealed record WatchingCandidate(string Path, string Name, string Source, string? Player, DateTime LastAccessTimeUtc, double SecondsSinceAccess, bool InUse, string Confidence)`
  - `public sealed class WatchingResolver` with ctor `(IReadOnlyList<IWatchSignal> signals, IProcessWindowSource windowSource, IReadOnlyCollection<string>? playerNames = null)`, `static WatchingResolver CreateDefault(IProcessWindowSource windowSource)`, and `IReadOnlyList<WatchingCandidate> Resolve(string libraryRoot, int limit, bool includeAccessFallback)`.
  - Confidence values are the exact strings `"high"` and `"low"`.

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/WatchingResolverTests.cs`:

```csharp
using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class WatchingResolverTests
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

    private WatchingResolver Resolver(params ProcessWindow[] windows) =>
        WatchingResolver.CreateDefault(new FakeProcessWindowSource(windows));

    [TestMethod]
    public void Resolve_SingleUnambiguousPlayerHit_IsHighAndFirst()
    {
        string clip = Touch("clip.mp4");
        Touch("other.mp4");

        IReadOnlyList<WatchingCandidate> result =
            Resolver(new ProcessWindow("vlc", "clip.mp4 - VLC media player"))
                .Resolve(_tempDir, limit: 5, includeAccessFallback: true);

        Assert.AreEqual(clip, result[0].Path);
        Assert.AreEqual("high", result[0].Confidence);
        Assert.AreEqual(PlayerTitleSignal.SourceName, result[0].Source);
        Assert.AreEqual("vlc", result[0].Player);
    }

    [TestMethod]
    public void Resolve_NoPlayer_FallsBackToMostRecentAccessAsLow()
    {
        string older = Touch("older.mp4");
        string newer = Touch("newer.mp4");
        File.SetLastAccessTimeUtc(older, DateTime.UtcNow.AddHours(-3));
        File.SetLastAccessTimeUtc(newer, DateTime.UtcNow);

        IReadOnlyList<WatchingCandidate> result =
            Resolver().Resolve(_tempDir, limit: 5, includeAccessFallback: true);

        Assert.AreEqual(newer, result[0].Path);
        Assert.IsTrue(result.All(c => c.Confidence == "low"));
        Assert.AreEqual(AccessTimeSignal.SourceName, result[0].Source);
    }

    [TestMethod]
    public void Resolve_NoPlayer_AndFallbackDisabled_ReturnsEmpty()
    {
        Touch("a.mp4");

        IReadOnlyList<WatchingCandidate> result =
            Resolver().Resolve(_tempDir, limit: 5, includeAccessFallback: false);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Resolve_MultiplePlayers_AllLow()
    {
        Touch("a.mp4");
        Touch("b.mp4");

        IReadOnlyList<WatchingCandidate> result = Resolver(
                new ProcessWindow("vlc", "a.mp4 - VLC media player"),
                new ProcessWindow("mpc-hc64", "b.mp4"))
            .Resolve(_tempDir, limit: 5, includeAccessFallback: false);

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.All(c => c.Confidence == "low"));
    }

    [TestMethod]
    public void Resolve_EmptyLibrary_ReturnsEmpty()
    {
        Assert.AreEqual(0, Resolver().Resolve(_tempDir, limit: 5, includeAccessFallback: true).Count);
    }

    [TestMethod]
    public void Resolve_LockedFile_ReportsInUseTrue()
    {
        string busy = Touch("busy.mp4");
        using var hold = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read);

        WatchingCandidate candidate = Resolver(new ProcessWindow("vlc", "busy.mp4 - VLC media player"))
            .Resolve(_tempDir, limit: 5, includeAccessFallback: true)
            .Single(c => c.Path == busy);

        Assert.IsTrue(candidate.InUse);
    }

    [TestMethod]
    public void Resolve_FreeFile_ReportsInUseFalse()
    {
        string free = Touch("free.mp4");

        WatchingCandidate candidate = Resolver(new ProcessWindow("vlc", "free.mp4 - VLC media player"))
            .Resolve(_tempDir, limit: 5, includeAccessFallback: true)
            .Single(c => c.Path == free);

        Assert.IsFalse(candidate.InUse);
    }

    [TestMethod]
    public void Resolve_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
            Touch($"clip{i}.mp4");

        IReadOnlyList<WatchingCandidate> result =
            Resolver().Resolve(_tempDir, limit: 3, includeAccessFallback: true);

        Assert.AreEqual(3, result.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WatchingResolverTests" --nologo`
Expected: FAIL — resolver types do not exist.

- [ ] **Step 3: Create `WatchingCandidate`**

Create `clipmeta.core/Watching/WatchingCandidate.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>One ranked watched-clip candidate.</summary>
/// <param name="Path">Absolute path to the candidate clip (always a library clip).</param>
/// <param name="Name">File name only.</param>
/// <param name="Source">Dominant evidence source: "player_title" or "access_time".</param>
/// <param name="Player">Process name when a player named it; otherwise null.</param>
/// <param name="LastAccessTimeUtc">Last-access time at enumeration.</param>
/// <param name="SecondsSinceAccess">Seconds between enumeration and the last access (≥ 0).</param>
/// <param name="InUse">True when the file currently has an exclusive-denying open handle.</param>
/// <param name="Confidence">"high" only for a single unambiguous player hit; otherwise "low".</param>
public sealed record WatchingCandidate(
    string Path,
    string Name,
    string Source,
    string? Player,
    DateTime LastAccessTimeUtc,
    double SecondsSinceAccess,
    bool InUse,
    string Confidence);
```

- [ ] **Step 4: Create `WatchingResolver`**

Create `clipmeta.core/Watching/WatchingResolver.cs`:

```csharp
namespace ClipMetaCore.Watching;

/// <summary>
/// Runs the registered <see cref="IWatchSignal"/>s over a once-enumerated library, groups their
/// evidence per clip, and scores confidence by corroboration: a single unambiguous player-title hit
/// is "high" (auto-safe to write); everything else is "low" (confirm before mutating). The lock
/// probe enriches the few returned candidates as a tiebreaker and a pre-write warning.
/// </summary>
public sealed class WatchingResolver
{
    /// <summary>Confidence value for an auto-safe candidate.</summary>
    public const string HighConfidence = "high";

    /// <summary>Confidence value for a candidate that needs confirmation before a write.</summary>
    public const string LowConfidence = "low";

    private readonly IReadOnlyList<IWatchSignal> _signals;
    private readonly IProcessWindowSource _windowSource;
    private readonly IReadOnlyCollection<string> _playerNames;

    /// <summary>Creates a resolver over the given signals and process source.</summary>
    public WatchingResolver(
        IReadOnlyList<IWatchSignal> signals,
        IProcessWindowSource windowSource,
        IReadOnlyCollection<string>? playerNames = null)
    {
        _signals = signals;
        _windowSource = windowSource;
        _playerNames = playerNames ?? MediaPlayers.KnownProcessNames;
    }

    /// <summary>The pass-1 resolver: player-title then access-time signals.</summary>
    public static WatchingResolver CreateDefault(IProcessWindowSource windowSource) =>
        new(new IWatchSignal[] { new PlayerTitleSignal(), new AccessTimeSignal() }, windowSource);

    /// <summary>
    /// Resolves the watched-clip candidates under <paramref name="libraryRoot"/>, best first, capped
    /// at <paramref name="limit"/>. When <paramref name="includeAccessFallback"/> is false, only
    /// player-title candidates are returned (empty when no player resolves a clip).
    /// </summary>
    public IReadOnlyList<WatchingCandidate> Resolve(string libraryRoot, int limit, bool includeAccessFallback)
    {
        WatchContext context = WatchContext.Build(libraryRoot, _windowSource, _playerNames);

        var hitsByPath = new Dictionary<string, List<SignalHit>>(StringComparer.OrdinalIgnoreCase);
        foreach (IWatchSignal signal in _signals)
            foreach (SignalHit hit in signal.Detect(context))
            {
                if (!hitsByPath.TryGetValue(hit.ClipPath, out List<SignalHit>? list))
                    hitsByPath[hit.ClipPath] = list = new List<SignalHit>();
                list.Add(hit);
            }

        DateTime now = DateTime.UtcNow;
        var candidates = new List<WatchingCandidate>();
        foreach ((string path, List<SignalHit> hits) in hitsByPath)
        {
            bool hasPlayer = hits.Any(h => h.Source == PlayerTitleSignal.SourceName);

            // include_access_fallback governs whether access-only candidates appear at all.
            if (!hasPlayer && !includeAccessFallback)
                continue;

            // Safety: only ever surface clips that were enumerated from the library.
            if (!context.ByFullPath.TryGetValue(path, out LibraryClip? clip))
                continue;

            bool playerUnambiguous = hits.Any(h => h.Source == PlayerTitleSignal.SourceName && !h.Ambiguous);
            string source = hasPlayer ? PlayerTitleSignal.SourceName : AccessTimeSignal.SourceName;
            string? player = hits.FirstOrDefault(h => h.Player is not null)?.Player;

            candidates.Add(new WatchingCandidate(
                Path: clip.FullPath,
                Name: clip.FileName,
                Source: source,
                Player: player,
                LastAccessTimeUtc: clip.LastAccessTimeUtc,
                SecondsSinceAccess: Math.Max(0, (now - clip.LastAccessTimeUtc).TotalSeconds),
                InUse: false, // enriched below, only for the returned set
                Confidence: playerUnambiguous ? HighConfidence : LowConfidence));
        }

        // Rank (high first, then most-recent access) and cap BEFORE probing, so the lock probe only
        // opens the handful of files we actually return — never the whole library on a fallback pass.
        List<WatchingCandidate> ranked = candidates
            .OrderByDescending(c => c.Confidence == HighConfidence)
            .ThenByDescending(c => c.LastAccessTimeUtc)
            .Take(limit)
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
            ranked[i] = ranked[i] with { InUse = ProbeInUse(ranked[i].Path) };

        // Final ordering applies the lock probe as a tiebreaker within equal confidence.
        return ranked
            .OrderByDescending(c => c.Confidence == HighConfidence)
            .ThenByDescending(c => c.InUse)
            .ThenByDescending(c => c.LastAccessTimeUtc)
            .ToList();
    }

    /// <summary>
    /// True when the file has an open handle that denies exclusive access. Best-effort and never
    /// fatal: an unexpected failure reports not-in-use and the resolution continues.
    /// </summary>
    private static bool ProbeInUse(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~WatchingResolverTests" --nologo`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add clipmeta.core/Watching/WatchingCandidate.cs clipmeta.core/Watching/WatchingResolver.cs clipmetascribe.Tests/WatchingResolverTests.cs
git commit -m "feat(core): WatchingResolver aggregates signals into ranked, confidence-scored candidates"
```

---

### Task 7: MCP tool `library_watching`

**Files:**
- Modify: `clipmetamcp/Tools/ReadTools.cs` (add registration + handler + schema constants)
- Test: `clipmetamcp.Tests/LibraryWatchingToolTests.cs`

**Interfaces:**
- Consumes: `WatchingResolver`, `ProcessWindowSource`, `WatchingCandidate` (Tasks 3, 6); `LibrarySandbox`, `ToolRegistry`, `ToolDefinition`, the `GetOptional*` helpers (existing).
- Produces: a registered tool named exactly `library_watching` returning `{ libraryRoot, candidateCount, candidates: [{ path, name, source, player, lastAccessTimeUtc, secondsSinceAccess, inUse, confidence }] }`.

- [ ] **Step 1: Write the failing tests**

Create `clipmetamcp.Tests/LibraryWatchingToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using ClipMetaMcp.Tests.Helpers;

namespace ClipMetaMcp.Tests;

/// <summary>
/// library_watching works on filenames + access times, so these tests need only empty .mp4 files
/// (no real clips, no CI skip). They assert result shape and the unconfigured-library refusal.
/// </summary>
[TestClass]
public class LibraryWatchingToolTests
{
    private string _lib = null!;

    [TestInitialize]
    public void SetUp()
    {
        _lib = Path.Combine(Path.GetTempPath(), "clipmeta-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_lib);
        File.WriteAllBytes(Path.Combine(_lib, "a.mp4"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(_lib, "b.mp4"), Array.Empty<byte>());
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_lib))
            Directory.Delete(_lib, recursive: true);
    }

    private JsonObject Call(JsonObject args, string? root)
    {
        var responses = McpHarness.Run(root,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, "library_watching", args));
        return (JsonObject)responses[1]["result"]!;
    }

    [TestMethod]
    public void Watching_WithAccessFallback_ReturnsShapedCandidates()
    {
        JsonObject result = Call(new JsonObject { ["include_access_fallback"] = true }, _lib);

        Assert.IsNull(result["isError"]);
        var candidates = (JsonArray)result["candidates"]!;
        Assert.IsTrue(candidates.Count >= 1, "access fallback should surface the temp clips");

        JsonObject first = (JsonObject)candidates[0]!;
        foreach (string key in new[]
                 { "path", "name", "source", "lastAccessTimeUtc", "secondsSinceAccess", "inUse", "confidence" })
            Assert.IsTrue(first.ContainsKey(key), $"candidate missing '{key}'");

        string confidence = first["confidence"]!.GetValue<string>();
        Assert.IsTrue(confidence is "high" or "low");
    }

    [TestMethod]
    public void Watching_RespectsLimit()
    {
        JsonObject result = Call(new JsonObject { ["limit"] = 1 }, _lib);
        Assert.AreEqual(1, ((JsonArray)result["candidates"]!).Count);
    }

    [TestMethod]
    public void Watching_NoLibraryConfigured_Refuses()
    {
        JsonObject result = Call(new JsonObject(), root: null);
        Assert.IsTrue(result["isError"]?.GetValue<bool>() == true, "must refuse with no library configured");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test clipmetamcp.Tests --filter "FullyQualifiedName~LibraryWatchingToolTests" --nologo`
Expected: FAIL — `library_watching` is not registered (the harness returns an error/unknown-tool result, so the shape assertions fail).

- [ ] **Step 3: Add the using, schema constants, registration, and handler**

In `clipmetamcp/Tools/ReadTools.cs`, add to the using block at the top:

```csharp
using ClipMetaCore.Watching;
```

Add these constants next to `MaxListLimit` (after line 26):

```csharp
    /// <summary>Default number of watched-clip candidates returned by library_watching.</summary>
    private const int DefaultWatchingLimit = 5;

    /// <summary>Hard ceiling for the caller-supplied watched-clip limit.</summary>
    private const int MaxWatchingLimit = 50;
```

Register the tool at the end of `RegisterAll` (after the `library_search_index` registration, before the method's closing brace):

```csharp
        registry.Register(new ToolDefinition(
            "library_watching",
            "Resolves 'the clip I'm watching / just watched' by inspecting open media players. " +
            "Returns ranked candidates, best first. A 'player_title' candidate resolved to a library " +
            "path with confidence 'high' is the file an open player is showing — prefer it and you " +
            "may tag it. If only 'access_time' candidates exist, or confidence is 'low' (multiple " +
            "players open, or an ambiguous file name), confirm with the user before tagging. To tag, " +
            "call the write tool with the chosen 'path'. Note: a clip cannot be written while a " +
            "player still holds it open ('inUse' true) — it frees when the player advances or closes. " +
            "Optional 'limit' (default " + DefaultWatchingLimit + ") and 'include_access_fallback' " +
            "(default true). Requires a configured clips library.",
            WatchingSchema(),
            args => Watching(args, sandbox),
            _ => new JsonObject { ["limit"] = DefaultWatchingLimit }));
```

Add the schema builder next to the other `*Schema()` methods:

```csharp
    private static JsonObject WatchingSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = $"Maximum candidates to return (default {DefaultWatchingLimit}, max {MaxWatchingLimit}).",
            },
            ["include_access_fallback"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "When true (default), include most-recently-accessed clips as " +
                                  "low-confidence candidates. When false, only open-player candidates " +
                                  "are returned.",
            },
        },
    };
```

Add the handler next to the other handlers:

```csharp
    private static JsonObject Watching(JsonObject? args, LibrarySandbox sandbox)
    {
        string root = sandbox.RequireRoot();
        int limit = Math.Clamp(GetOptionalInt(args, "limit", DefaultWatchingLimit), 1, MaxWatchingLimit);
        bool includeAccessFallback = GetOptionalBool(args, "include_access_fallback", defaultValue: true);

        var resolver = WatchingResolver.CreateDefault(ProcessWindowSource.ForCurrentPlatform());
        IReadOnlyList<WatchingCandidate> candidates = resolver.Resolve(root, limit, includeAccessFallback);

        var array = new JsonArray();
        foreach (WatchingCandidate c in candidates)
        {
            array.Add(new JsonObject
            {
                ["path"] = c.Path,
                ["name"] = c.Name,
                ["source"] = c.Source,
                ["player"] = c.Player,
                ["lastAccessTimeUtc"] = c.LastAccessTimeUtc.ToString("O"),
                ["secondsSinceAccess"] = Math.Round(c.SecondsSinceAccess, 1),
                ["inUse"] = c.InUse,
                ["confidence"] = c.Confidence,
            });
        }

        return new JsonObject
        {
            ["libraryRoot"] = root,
            ["candidateCount"] = candidates.Count,
            ["candidates"] = array,
        };
    }
```

- [ ] **Step 4: Run tests to verify they pass (incl. the stdout-purity suite that now auto-covers the new tool)**

Run: `dotnet build --nologo -v q && dotnet test clipmetamcp.Tests --filter "FullyQualifiedName~LibraryWatchingToolTests|FullyQualifiedName~StdoutPurityTests" --nologo`
Expected: PASS — including `StdoutPurityTests`, which drives `library_watching` via its `ExampleArguments` and asserts zero stdout bytes + no tool error.

- [ ] **Step 5: Commit**

```bash
git add clipmetamcp/Tools/ReadTools.cs clipmetamcp.Tests/LibraryWatchingToolTests.cs
git commit -m "feat(mcp): library_watching tool resolves the currently/just-watched clip"
```

---

### Task 8: CLI `--watching` command

**Files:**
- Create: `clipmetascribe/Commands/WatchingCommand.cs`
- Modify: `clipmetascribe/Program.cs` (dispatch + `KnownFlags` + usage)
- Test: `clipmetascribe.Tests/WatchingCommandTests.cs`

**Interfaces:**
- Consumes: `WatchingResolver`, `ProcessWindowSource`, `WatchingCandidate` (Tasks 3, 6).
- Produces: `internal static class WatchingCommand { static int Run(string libraryDir, int limit, bool includeAccessFallback, TextWriter? output = null); }`.

- [ ] **Step 1: Write the failing tests**

Create `clipmetascribe.Tests/WatchingCommandTests.cs`:

```csharp
using ClipMetaScribe.Commands;

namespace ClipMetaScribe.Tests;

[TestClass]
public class WatchingCommandTests
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
    public void Run_WithClipsAndFallback_ListsCandidatePaths()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "clip.mp4"), Array.Empty<byte>());
        using var sw = new StringWriter();

        int code = WatchingCommand.Run(_tempDir, limit: 5, includeAccessFallback: true, output: sw);

        Assert.AreEqual(0, code);
        StringAssert.Contains(sw.ToString(), "clip.mp4");
    }

    [TestMethod]
    public void Run_EmptyLibrary_ReportsNoCandidates()
    {
        using var sw = new StringWriter();

        int code = WatchingCommand.Run(_tempDir, limit: 5, includeAccessFallback: true, output: sw);

        Assert.AreEqual(0, code);
        StringAssert.Contains(sw.ToString(), "No watched-clip candidates");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~WatchingCommandTests" --nologo`
Expected: FAIL — `WatchingCommand` does not exist.

- [ ] **Step 3: Create `WatchingCommand`**

Create `clipmetascribe/Commands/WatchingCommand.cs`:

```csharp
using ClipMetaCore.Watching;

namespace ClipMetaScribe.Commands;

/// <summary>
/// Resolves which clip an open media player is showing and prints the ranked candidates.
/// Resolve-only: to tag a candidate, run a normal write on its path.
/// </summary>
internal static class WatchingCommand
{
    /// <summary>
    /// Prints watched-clip candidates under <paramref name="libraryDir"/> to
    /// <paramref name="output"/> (default <see cref="Console.Out"/>).
    /// </summary>
    /// <returns>Exit code 0.</returns>
    internal static int Run(string libraryDir, int limit, bool includeAccessFallback, TextWriter? output = null)
    {
        output ??= Console.Out;

        var resolver = WatchingResolver.CreateDefault(ProcessWindowSource.ForCurrentPlatform());
        IReadOnlyList<WatchingCandidate> candidates = resolver.Resolve(libraryDir, limit, includeAccessFallback);

        if (candidates.Count == 0)
        {
            output.WriteLine("No watched-clip candidates found.");
            return 0;
        }

        output.WriteLine("Watched-clip candidates (most likely first):");
        foreach (WatchingCandidate c in candidates)
        {
            string via = c.Player is null ? "" : $" via {c.Player}";
            string locked = c.InUse ? "  [in use — close/advance the player before tagging]" : "";
            output.WriteLine($"  [{c.Confidence}] {c.Path}");
            output.WriteLine($"        source={c.Source}{via}  {c.SecondsSinceAccess:F0}s since access{locked}");
        }
        return 0;
    }
}
```

- [ ] **Step 4: Wire `--watching` into `Program.cs`**

In `clipmetascribe/Program.cs`, add the dispatch block immediately after the `--index-search` block (after its closing brace, before the `--export` block at line 148):

```csharp
        if (ContainsFlag(args, "--watching"))
        {
            if (filePath == null || !Directory.Exists(filePath))
            {
                Console.Error.WriteLine("Error: --watching requires a valid clips directory as the first argument.");
                return 1;
            }
            int watchLimit = 5;
            string? limitArg = GetFlag(args, "--limit");
            if (limitArg != null && (!int.TryParse(limitArg, out watchLimit) || watchLimit < 1))
            {
                Console.Error.WriteLine("Error: --limit requires a positive integer.");
                return 1;
            }
            bool includeAccessFallback = !ContainsFlag(args, "--no-access-fallback");
            try
            {
                return WatchingCommand.Run(filePath, watchLimit, includeAccessFallback);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }
```

Add the new flags to the `KnownFlags` set (the collection initializer near line 329-335):

```csharp
        "--watching", "--limit", "--no-access-fallback",
```

In `PrintUsage`, add under the directory-command usage lines (after the `--index-search` line ~550):

```csharp
              clipmetascribe "C:\clips\" --watching [--limit <n>] [--no-access-fallback]
```

and add to the Options section:

```csharp
              --limit <n>             Max watched-clip candidates (use with --watching; default 5).
              --no-access-fallback    Only open-player candidates (use with --watching).
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet build --nologo -v q && dotnet test --filter "FullyQualifiedName~WatchingCommandTests" --nologo`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add clipmetascribe/Commands/WatchingCommand.cs clipmetascribe/Program.cs clipmetascribe.Tests/WatchingCommandTests.cs
git commit -m "feat(scribe): --watching command prints ranked watched-clip candidates"
```

---

### Task 9: Full build/test gate + documentation

**Files:**
- Modify: `CLAUDE.md` (architecture note + test counts), `docs/PITFALLS.md` (new entries), `README.md` (tool/command docs), `tools/mcpb-manifest.json` (if it enumerates tools)

**Interfaces:** none (documentation + verification only).

- [ ] **Step 1: Run the full build and test gate**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors, all projects.

Run: `dotnet test --nologo --no-build -v q`
Expected: all pass (this includes the multi-minute `clipmetascribe.Tests` integration suite — use a long timeout). Record the new per-project test totals printed by the runner; you will need them for the CLAUDE.md edit.

- [ ] **Step 2: Update `CLAUDE.md`**

In the project table, update the `clipmeta.core` row to mention the new concern and the two CLI/MCP surfaces, and bump the `clipmetascribe.Tests` and `clipmetamcp.Tests` counts to the totals from Step 1. Update the `clipmeta.core` layout line to include `Watching/`:

```markdown
`clipmeta.core` layout: `Abstractions/`, `Mp4/`, `Write/`, `Read/`, `Watching/` (watched-clip resolution: signals, process seam, resolver), `Schema/`, `Logging/`, `Exceptions/`.
```

Add a one-line note under the scribe and MCP rows that `--watching` (scribe) and `library_watching` (MCP) resolve the currently/just-watched clip, resolve-only.

- [ ] **Step 3: Append to `docs/PITFALLS.md`**

Add a dated section capturing the design findings (so the parser/writer guardrails travel with the code):

```markdown
## 2026-06-21 — Watched-clip resolution

- **Writing to a clip a player still holds open fails.** `File.Replace` deletes-and-swaps the
  target; that throws a sharing violation unless every open handle used `FILE_SHARE_DELETE`, which
  MPC-HC/VLC do not. A clip is writable only after the player advances ("next") or closes. The
  watched-clip resolver surfaces `inUse` so callers warn before attempting a write. **TODO when
  dogfooding:** confirm per player whether the lock releases on *stop*, on *next*, or only on
  *close* — this sets the deferred-tag queue's drain timing (pass 2).
- **ClipMeta's own reads bump last-access time.** That pollutes the access-time resolution signal.
  Fixed at the single parse choke point (`Mp4Parser.ParseFile`) with `AccessTimeGuard`
  (capture-then-restore, best-effort — restoring is itself a write that can lose to a lock).
- **Window titles only *select*, never *construct*.** A resolver candidate must come from a clip
  enumerated under the library root; a title naming a path outside the library matches nothing and
  is dropped. This is the containment guarantee — do not "resolve" a title path by trusting it.
- **Player title formats:** MPC-HC emits the full path; VLC emits `name.mp4 - VLC media player`
  (bare name). A VLC title with no `.mp4` is an embedded metadata title — expected, yields no player
  candidate. The recognized-player list lives in `MediaPlayers.KnownProcessNames` (extensible).
```

- [ ] **Step 4: Update `README.md` and the .mcpb manifest**

In `README.md`, add `library_watching` to the MCP tools list and `--watching` to the clipmetascribe command list, noting the player list is extensible and the per-player title-format assumptions (MPC full path, VLC bare name). Open `tools/mcpb-manifest.json`; if it enumerates tools for display, add `library_watching` with a one-line description matching the tool's.

- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md docs/PITFALLS.md README.md tools/mcpb-manifest.json
git commit -m "docs: document watched-clip resolution (CLAUDE.md, PITFALLS, README, mcpb manifest)"
```

---

## Self-Review

**Spec coverage (pass 1 sections):**
- §2 cross-platform seam → Task 3. §3 `IWatchSignal`/`WatchContext`/no-fabrication → Tasks 4, 6 (`ByFullPath` guard). §4a parser → Task 2; §4b/§4c signals → Task 5; §4d lock probe → Task 6 (`ProbeInUse`). §5 resolver + confidence + ranking → Task 6. §6 access-time hardening → Task 1. §7 write-while-open → Task 9 PITFALLS. §8a MCP tool → Task 7; §8b CLI → Task 8. §10 tests → every task's test step + Task 9 gate. §12 risks → covered by per-process try/catch (Task 3), best-effort guard (Task 1), enumerated-only candidates (Tasks 4/6), OS guard (Task 3). Pass-2 queue and §13 future items are intentionally not in this plan.

**Placeholder scan:** the only "TODO" is inside the PITFALLS prose (a real, intended dogfooding action item), not a plan gap. All code steps contain complete code; all run steps have exact commands and expected results.

**Type consistency:** `WatchingCandidate` fields match between Task 6, Task 7 (JSON keys), and Task 8 (printing). `PlayerTitleSignal.SourceName` / `AccessTimeSignal.SourceName` / `WatchingResolver.HighConfidence`/`LowConfidence` are defined once and reused across tasks 5-8. `WatchContext.Build` / `WatchingResolver.Resolve` / `WatchingResolver.CreateDefault` signatures are identical wherever referenced. `IProcessWindowSource.GetPlayerWindows` signature matches across the real source, empty source, fake, and `WatchContext.Build`.

**Note on test runs:** `--filter` keeps the TDD loop fast; the final `dotnet test` (Task 9) runs the whole suite including the multi-minute scribe integration tests — budget a long timeout, it is not a hang.
