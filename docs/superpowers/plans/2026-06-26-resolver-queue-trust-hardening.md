# Resolver & Queue Trust Hardening, Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make gaming-mode resolution and the deferred-tag queue trustworthy, a just-saved clip is found, ClipMeta's own file touches stop polluting the signals, every auto-flushed tag is reported, and an unrecognized player name is flagged.

**Architecture:** One shared substrate (`SelfActionLedger` + NTFS creation-time + index baseline) feeds the two no-player signals; a `DrainJournal` surfaces the silent background pump's writes; a write-path roster check rides an advisory back. All Core logic is pure and injectable; the MCP layer wires singletons in `Program.cs`.

**Tech Stack:** C# / .NET 10, BCL only (zero NuGet in production), MSTest.

## Global Constraints

- Zero external NuGet packages in `clipmeta.core`, `clipmetamcp` (BCL/SDK only). MSTest is the sole test-project exception.
- Big-endian MP4 IO only through `BigEndianReader`/`BigEndianWriter` (not touched here, but never regress it).
- CLIs/MCP are thin shells, no business logic in handlers; logic lives in Core.
- New formats/signals extend via interfaces, never edit-in-place; signals MUST NOT throw for ordinary failure (emit nothing).
- XML doc comments on all public types/methods; named constants, no magic numbers.
- No new MCP tools, tool count stays **17**. Changes are additive response fields + one optional `roster` arg. Still **run the full `clipmetamcp.Tests`** for any MCP-surface task (surface assertions live outside the diff, CLAUDE.md rule).
- Version → **1.4.0** in BOTH `clipmetamcp/clipmetamcp.csproj` and `tools/mcpb-manifest.json` (pack gate fails if they disagree).
- Build gate: `dotnet build --nologo -v q` = 0 warnings / 0 errors. Test gate: `dotnet test --nologo --no-build -v q` all pass (scribe suite takes minutes, long timeout, not a hang).

---

## File Structure

**Create (Core):**
- `clipmeta.core/Watching/SelfActionLedger.cs`, process-wide record of paths ClipMeta wrote/read this session.
- `clipmeta.core/Watching/DrainedTag.cs`, one auto-flushed tag (path, changed fields, when).
- `clipmeta.core/Watching/DrainJournal.cs`, report-once buffer of pump auto-flushes.
- `clipmeta.core/Schema/PlayerRosterGuard.cs`, pure "which committed player tokens are unknown" check.

**Create (tests):**
- `clipmetascribe.Tests/SelfActionLedgerTests.cs`, `DrainJournalTests.cs`, `PlayerRosterGuardTests.cs`.

**Modify (Core):**
- `clipmeta.core/Watching/LibraryClip.cs`, add `CreationTimeUtc`.
- `clipmeta.core/Watching/WatchContext.cs`, read creation time; add `KnownBaselinePaths` + `Ledger`; load baseline from the index.
- `clipmeta.core/Watching/RecentWriteSignal.cs`, creation-time + baseline + ledger predicate (P0-2).
- `clipmeta.core/Watching/AccessTimeSignal.cs`, self-read exclusion (P1-1).
- `clipmeta.core/Watching/WatchingResolver.cs`, hold + thread the ledger into `WatchContext.Build`.
- `clipmeta.core/Watching/TagQueue.cs`, `Drain` gains an `onWritten` callback; extract `ChangedFields`.
- `clipmeta.core/Watching/QueueDrainPump.cs`, record auto-flushes into the journal.

**Modify (MCP):**
- `clipmetamcp/Program.cs`, build one `SelfActionLedger` + one `DrainJournal`; inject both.
- `clipmetamcp/Tools/ReadTools.cs`, mark reads; thread ledger to resolver; surface `autoFlushed`; roster review on no tools here (reads only), see Task 8 for write/queue.
- `clipmetamcp/Tools/WriteTools.cs`, mark writes; roster advisory + `roster` arg.
- `clipmetamcp/Tools/QueueTools.cs`, roster advisory + `roster` arg on `library_queue_tag`; surface `autoFlushed` on flush/status.

**Modify (existing tests to reconcile):** `clipmetascribe.Tests/WatchingResolverTests.cs` (gaming cases + `TouchStale`/new `TouchCreated`), `RecentWriteSignalTests.cs`, any `AccessTimeSignal`/`WatchContext` test. Reconcile as correct new behavior, not blind edits.

---

## Task 1: `SelfActionLedger` (Core)

**Files:**
- Create: `clipmeta.core/Watching/SelfActionLedger.cs`
- Test: `clipmetascribe.Tests/SelfActionLedgerTests.cs`

**Interfaces:**
- Produces: `enum SelfTouchKind { Read, Written }`; `class SelfActionLedger` with ctor `(Func<DateTimeOffset>? clock = null)`, `void MarkWritten(string)`, `void MarkRead(string)`, `bool WasWrittenWithin(string path, TimeSpan window, DateTimeOffset now)`, `bool WasTouchedWithin(string path, TimeSpan window, DateTimeOffset now)`, `static readonly TimeSpan DefaultWindow`.

- [ ] **Step 1: Write the failing test**

```csharp
// clipmetascribe.Tests/SelfActionLedgerTests.cs
using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class SelfActionLedgerTests
{
    private static DateTimeOffset T0 => new(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void MarkWritten_IsWrittenWithinWindow()
    {
        var clock = T0;
        var ledger = new SelfActionLedger(() => clock);
        ledger.MarkWritten(@"C:\lib\a.mp4");
        Assert.IsTrue(ledger.WasWrittenWithin(@"C:\lib\a.mp4", TimeSpan.FromMinutes(5), T0));
        Assert.IsTrue(ledger.WasTouchedWithin(@"c:\LIB\A.MP4", TimeSpan.FromMinutes(5), T0)); // case-insensitive
    }

    [TestMethod]
    public void Read_DoesNotCountAsWritten()
    {
        var ledger = new SelfActionLedger(() => T0);
        ledger.MarkRead(@"C:\lib\a.mp4");
        Assert.IsFalse(ledger.WasWrittenWithin(@"C:\lib\a.mp4", TimeSpan.FromMinutes(5), T0));
        Assert.IsTrue(ledger.WasTouchedWithin(@"C:\lib\a.mp4", TimeSpan.FromMinutes(5), T0));
    }

    [TestMethod]
    public void WrittenThenRead_StaysWritten()
    {
        var ledger = new SelfActionLedger(() => T0);
        ledger.MarkWritten(@"C:\lib\a.mp4");
        ledger.MarkRead(@"C:\lib\a.mp4");
        Assert.IsTrue(ledger.WasWrittenWithin(@"C:\lib\a.mp4", TimeSpan.FromMinutes(5), T0));
    }

    [TestMethod]
    public void OutsideWindow_IsNotWithin()
    {
        var ledger = new SelfActionLedger(() => T0);
        ledger.MarkWritten(@"C:\lib\a.mp4");
        Assert.IsFalse(ledger.WasWrittenWithin(@"C:\lib\a.mp4", TimeSpan.FromMinutes(5), T0.AddMinutes(6)));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo --filter SelfActionLedgerTests`
Expected: FAIL, `SelfActionLedger` does not exist.

- [ ] **Step 3: Implement**

```csharp
// clipmeta.core/Watching/SelfActionLedger.cs
namespace ClipMetaCore.Watching;

/// <summary>Whether ClipMeta wrote a file or merely read it.</summary>
public enum SelfTouchKind
{
    /// <summary>ClipMeta opened the file's content (export / get-metadata).</summary>
    Read,
    /// <summary>ClipMeta wrote metadata into the file.</summary>
    Written,
}

/// <summary>
/// Process-wide record of the clips ClipMeta itself touched this session, so signals keyed on raw
/// filesystem timestamps can subtract self-actions: a clip we just wrote is not a fresh user "save",
/// and a clip we just read is not a clip the user just "watched". In-memory and session-scoped, a
/// restart is a new session. Thread-safe: the queue-drain pump thread and request threads share it.
/// </summary>
public sealed class SelfActionLedger
{
    /// <summary>How long a self-action masks a path (also the prune horizon).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, SelfTouchKind Kind)> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates a ledger. <paramref name="clock"/> is injected in tests; defaults to system UTC.</summary>
    public SelfActionLedger(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Records that ClipMeta wrote <paramref name="path"/> just now.</summary>
    public void MarkWritten(string path) => Mark(path, SelfTouchKind.Written);

    /// <summary>Records that ClipMeta read <paramref name="path"/>'s content just now.</summary>
    public void MarkRead(string path) => Mark(path, SelfTouchKind.Read);

    private void Mark(string path, SelfTouchKind kind)
    {
        lock (_gate)
        {
            // A fresh write outranks a later read: don't let a diagnostic read of a clip we just
            // tagged downgrade it to "merely read".
            if (kind == SelfTouchKind.Read &&
                _entries.TryGetValue(path, out var e) && e.Kind == SelfTouchKind.Written &&
                _clock() - e.At <= DefaultWindow)
                return;

            _entries[path] = (_clock(), kind);
            Prune();
        }
    }

    /// <summary>True if ClipMeta WROTE <paramref name="path"/> within <paramref name="window"/> of now.</summary>
    public bool WasWrittenWithin(string path, TimeSpan window, DateTimeOffset now)
    {
        lock (_gate)
            return _entries.TryGetValue(path, out var e) &&
                   e.Kind == SelfTouchKind.Written && now - e.At <= window;
    }

    /// <summary>True if ClipMeta touched (read OR wrote) <paramref name="path"/> within the window.</summary>
    public bool WasTouchedWithin(string path, TimeSpan window, DateTimeOffset now)
    {
        lock (_gate)
            return _entries.TryGetValue(path, out var e) && now - e.At <= window;
    }

    /// <summary>Drops entries older than <see cref="DefaultWindow"/> so the ledger never grows unbounded. Caller holds the lock.</summary>
    private void Prune()
    {
        DateTimeOffset cutoff = _clock() - DefaultWindow;
        foreach (string key in _entries.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
            _entries.Remove(key);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test clipmetascribe.Tests --nologo --filter SelfActionLedgerTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/SelfActionLedger.cs clipmetascribe.Tests/SelfActionLedgerTests.cs
git commit -m "feat(watching): SelfActionLedger, session record of self-written/read clips"
```

---

## Task 2: `DrainedTag` + `DrainJournal` (Core)

**Files:**
- Create: `clipmeta.core/Watching/DrainedTag.cs`, `clipmeta.core/Watching/DrainJournal.cs`
- Test: `clipmetascribe.Tests/DrainJournalTests.cs`

**Interfaces:**
- Produces: `record DrainedTag(string Path, IReadOnlyList<string> Fields, DateTimeOffset WhenUtc)`; `class DrainJournal` with `void Record(DrainedTag)`, `IReadOnlyList<DrainedTag> TakePending()`.

- [ ] **Step 1: Write the failing test**

```csharp
// clipmetascribe.Tests/DrainJournalTests.cs
using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class DrainJournalTests
{
    private static DrainedTag Tag(string p) =>
        new(p, new[] { "tags" }, DateTimeOffset.UtcNow);

    [TestMethod]
    public void TakePending_ReturnsRecorded_ThenClears()
    {
        var j = new DrainJournal();
        j.Record(Tag(@"C:\lib\a.mp4"));
        j.Record(Tag(@"C:\lib\b.mp4"));

        var first = j.TakePending();
        CollectionAssert.AreEqual(
            new[] { @"C:\lib\a.mp4", @"C:\lib\b.mp4" }, first.Select(t => t.Path).ToArray());

        Assert.AreEqual(0, j.TakePending().Count); // report-once: cleared
    }

    [TestMethod]
    public void Record_OverCap_DropsOldest()
    {
        var j = new DrainJournal();
        for (int i = 0; i < 60; i++) j.Record(Tag($@"C:\lib\{i}.mp4"));

        var pending = j.TakePending();
        Assert.AreEqual(50, pending.Count);
        Assert.AreEqual(@"C:\lib\10.mp4", pending[0].Path); // first 10 dropped
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo --filter DrainJournalTests`
Expected: FAIL, types do not exist.

- [ ] **Step 3: Implement**

```csharp
// clipmeta.core/Watching/DrainedTag.cs
namespace ClipMetaCore.Watching;

/// <summary>One tag the background pump auto-flushed: the clip, the fields it changed, and when.</summary>
/// <param name="Path">Clip whose queued tag was written.</param>
/// <param name="Fields">User-facing names of the fields the write changed.</param>
/// <param name="WhenUtc">When the auto-flush landed.</param>
public sealed record DrainedTag(string Path, IReadOnlyList<string> Fields, DateTimeOffset WhenUtc);
```

```csharp
// clipmeta.core/Watching/DrainJournal.cs
namespace ClipMetaCore.Watching;

/// <summary>
/// Report-once buffer of tags the background <see cref="QueueDrainPump"/> auto-flushed. The pump
/// writes the last clip the instant its player closes but reports to no caller; this lets the next
/// library_watching / library_flush_queue / library_queue_status surface "your queued tag landed".
/// <see cref="TakePending"/> returns and CLEARS, so each auto-flush is reported exactly once.
/// Thread-safe: the pump thread records, request threads take.
/// </summary>
public sealed class DrainJournal
{
    /// <summary>Most recent entries kept; older ones are dropped if never taken (no unbounded growth).</summary>
    private const int Cap = 50;

    private readonly object _gate = new();
    private readonly List<DrainedTag> _pending = new();

    /// <summary>Appends an auto-flushed tag, dropping the oldest beyond <see cref="Cap"/>.</summary>
    public void Record(DrainedTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        lock (_gate)
        {
            _pending.Add(tag);
            if (_pending.Count > Cap)
                _pending.RemoveRange(0, _pending.Count - Cap);
        }
    }

    /// <summary>Returns all pending auto-flushes (oldest first) and clears the buffer.</summary>
    public IReadOnlyList<DrainedTag> TakePending()
    {
        lock (_gate)
        {
            var copy = _pending.ToList();
            _pending.Clear();
            return copy;
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test clipmetascribe.Tests --nologo --filter DrainJournalTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/DrainedTag.cs clipmeta.core/Watching/DrainJournal.cs clipmetascribe.Tests/DrainJournalTests.cs
git commit -m "feat(watching): DrainJournal, report-once buffer for silent pump auto-flushes"
```

---

## Task 3: Creation-time + baseline + ledger on `WatchContext`

**Files:**
- Modify: `clipmeta.core/Watching/LibraryClip.cs`
- Modify: `clipmeta.core/Watching/WatchContext.cs`
- Test: `clipmetascribe.Tests/WatchContextBaselineTests.cs` (create)

**Interfaces:**
- Consumes: `SelfActionLedger` (Task 1); `ClipMetaIndex.ReadFromFile(string)` → `IndexData` with `.Entries[].FilePath`; `ClipMetaIndex.IndexFileName` = `".clipmeta-index"`.
- Produces: `LibraryClip` gains `DateTime CreationTimeUtc` (last positional param). `WatchContext` gains `IReadOnlySet<string> KnownBaselinePaths { get; init; }` (default empty) and `SelfActionLedger? Ledger { get; init; }`. Both `Build` overloads gain a trailing `SelfActionLedger? ledger = null` and populate `KnownBaselinePaths` from the library's index.

- [ ] **Step 1: Write the failing test**

```csharp
// clipmetascribe.Tests/WatchContextBaselineTests.cs
using ClipMetaCore.Read;
using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class WatchContextBaselineTests
{
    private static string NewLibrary()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cmwatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void Build_ReadsCreationTime_AndEmptyBaseline_WhenNoIndex()
    {
        string dir = NewLibrary();
        try
        {
            string clip = Path.Combine(dir, "a.mp4");
            File.WriteAllBytes(clip, new byte[] { 1, 2, 3 });

            WatchContext ctx = WatchContext.Build(dir, Array.Empty<ProcessWindow>());

            Assert.AreEqual(1, ctx.LibraryClips.Count);
            Assert.AreNotEqual(default, ctx.LibraryClips[0].CreationTimeUtc);
            Assert.AreEqual(0, ctx.KnownBaselinePaths.Count); // no index file
            Assert.IsNull(ctx.Ledger);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void Build_LoadsBaselinePaths_FromIndex()
    {
        string dir = NewLibrary();
        try
        {
            string clip = Path.Combine(dir, "a.mp4");
            File.WriteAllBytes(clip, new byte[] { 1, 2, 3 });
            // Build a real index file in the library so the baseline picks it up.
            ClipMetaIndex.WriteToFile(ClipMetaIndex.Build(dir), Path.Combine(dir, ClipMetaIndex.IndexFileName));

            WatchContext ctx = WatchContext.Build(dir, Array.Empty<ProcessWindow>());

            Assert.IsTrue(ctx.KnownBaselinePaths.Contains(clip)); // case-insensitive set
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

> Note: confirm `ClipMetaIndex.WriteToFile(IndexData, string)` exists (it does, used by `RebuildIndex`). If its signature differs, adjust the index-build line only.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo --filter WatchContextBaselineTests`
Expected: FAIL, `CreationTimeUtc` / `KnownBaselinePaths` / `Ledger` not defined.

- [ ] **Step 3: Implement, `LibraryClip`**

```csharp
// clipmeta.core/Watching/LibraryClip.cs
namespace ClipMetaCore.Watching;

/// <summary>A clip enumerated from the library, with the facts resolution needs.</summary>
/// <param name="FullPath">Absolute path to the .mp4 file.</param>
/// <param name="FileName">File name only (for bare-title matching).</param>
/// <param name="LastAccessTimeUtc">Last-access time at enumeration.</param>
/// <param name="LastWriteTimeUtc">
/// Last-write time at enumeration. NOT bumped by merely playing a clip.
/// </param>
/// <param name="CreationTimeUtc">
/// NTFS creation time at enumeration. Set fresh when a file appears in a directory even when a copy
/// preserves the source's write time, so it, not write time, identifies a genuinely new clip
/// (gaming mode; see <see cref="RecentWriteSignal"/>).
/// </param>
public sealed record LibraryClip(
    string FullPath, string FileName,
    DateTime LastAccessTimeUtc, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);
```

- [ ] **Step 4: Implement, `WatchContext`** (add props, read creation time, load baseline, thread ledger)

In `WatchContext.cs`, add the two properties after `PlayerWindows`:

```csharp
    /// <summary>Paths already known to the library (from the persisted index). Empty when no index.</summary>
    public IReadOnlySet<string> KnownBaselinePaths { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Session self-action ledger (paths ClipMeta wrote/read), or null when not tracked.</summary>
    public SelfActionLedger? Ledger { get; init; }
```

Change both `Build` overloads to accept a trailing `SelfActionLedger? ledger = null`, populate the new props, and pass `ledger` through:

```csharp
    public static WatchContext Build(
        string libraryRoot, IProcessWindowSource source,
        IReadOnlyCollection<string> playerNames, SelfActionLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Build(libraryRoot, source.GetPlayerWindows(playerNames), ledger);
    }

    public static WatchContext Build(
        string libraryRoot, IReadOnlyList<ProcessWindow> playerWindows, SelfActionLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(playerWindows);
        (List<LibraryClip> clips, var byName, var byPath) = EnumerateLibrary(libraryRoot);
        return new WatchContext
        {
            LibraryClips = clips,
            ByFileName = byName,
            ByFullPath = byPath,
            PlayerWindows = playerWindows,
            KnownBaselinePaths = LoadBaseline(libraryRoot),
            Ledger = ledger,
        };
    }
```

In `EnumerateLibrary`, read creation time alongside the others and pass it to `LibraryClip`:

```csharp
            DateTime accessTime, writeTime, creationTime;
            try
            {
                accessTime = File.GetLastAccessTimeUtc(path);
                writeTime = File.GetLastWriteTimeUtc(path);
                creationTime = File.GetCreationTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            clips.Add(new LibraryClip(path, Path.GetFileName(path), accessTime, writeTime, creationTime));
```

Add the baseline loader (and `using ClipMetaCore.Read;` at the top of the file):

```csharp
    /// <summary>
    /// Loads the set of paths the persisted index already knows. A missing/unreadable index yields an
    /// empty set (so a not-yet-indexed library degrades to creation-time + ledger novelty, never throws).
    /// </summary>
    private static IReadOnlySet<string> LoadBaseline(string libraryRoot)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string indexPath = Path.Combine(libraryRoot, ClipMetaIndex.IndexFileName);
            if (!File.Exists(indexPath))
                return known;
            foreach (IndexEntry entry in ClipMetaIndex.ReadFromFile(indexPath).Entries)
                known.Add(entry.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Treat a corrupt/locked index as "no baseline", never let it abort a resolution pass.
        }
        return known;
    }
```

- [ ] **Step 5: Run to verify the new tests pass**

Run: `dotnet test clipmetascribe.Tests --nologo --filter WatchContextBaselineTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Fix every `new LibraryClip(...)` call site** (the added param breaks construction)

Run: `dotnet build clipmeta.core --nologo -v q`, expect compile errors at each `new LibraryClip(...)` in tests/helpers. Add a creation-time argument to each. For test fixtures, pass the access or write time when creation isn't under test; where "old vs new" matters, pass an explicit value.

- [ ] **Step 7: Build all + run the watching tests**

Run: `dotnet build --nologo -v q` then `dotnet test clipmetascribe.Tests --nologo --filter Watch`
Expected: build 0/0; watching tests green (reconcile any that constructed `LibraryClip` positionally).

- [ ] **Step 8: Commit**

```bash
git add clipmeta.core/Watching/LibraryClip.cs clipmeta.core/Watching/WatchContext.cs clipmetascribe.Tests/WatchContextBaselineTests.cs
git add -u   # LibraryClip call-site fixes in tests
git commit -m "feat(watching): creation-time + index baseline + ledger on WatchContext"
```

---

## Task 4: `RecentWriteSignal` rework, gaming-mode novelty (P0-2)

**Files:**
- Modify: `clipmeta.core/Watching/RecentWriteSignal.cs`
- Test: `clipmetascribe.Tests/RecentWriteSignalTests.cs` (extend)

**Interfaces:**
- Consumes: `WatchContext.KnownBaselinePaths`, `WatchContext.Ledger`, `LibraryClip.CreationTimeUtc`, `SelfActionLedger.WasWrittenWithin`.
- Produces: unchanged `IWatchSignal` surface (`SourceName = "recent_write"`); new predicate.

- [ ] **Step 1: Write the failing tests**

```csharp
// clipmetascribe.Tests/RecentWriteSignalTests.cs, ADD these (keep existing file's usings/helpers)
[TestMethod]
public void NewClip_FreshCreation_OldWriteTime_IsDetected()
{
    DateTime now = new(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc);
    var clip = new LibraryClip(
        @"C:\lib\new.mp4", "new.mp4",
        LastAccessTimeUtc: now.AddDays(-10),
        LastWriteTimeUtc: now.AddDays(-10),   // copy preserved an OLD mtime
        CreationTimeUtc: now.AddMinutes(-1));  // but it just appeared in the folder
    var ctx = ContextWith(new[] { clip }, baseline: Array.Empty<string>(), ledger: null);

    var hits = new RecentWriteSignal(() => now).Detect(ctx).ToList();

    Assert.AreEqual(1, hits.Count);
    Assert.IsFalse(hits[0].Ambiguous);
}

[TestMethod]
public void KnownBaselinePath_IsExcluded()
{
    DateTime now = new(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc);
    var clip = new LibraryClip(@"C:\lib\old.mp4", "old.mp4", now, now, now.AddMinutes(-1));
    var ctx = ContextWith(new[] { clip }, baseline: new[] { @"C:\lib\old.mp4" }, ledger: null);

    Assert.AreEqual(0, new RecentWriteSignal(() => now).Detect(ctx).Count());
}

[TestMethod]
public void SelfWrittenPath_IsExcluded()
{
    DateTime now = new(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc);
    var ledger = new SelfActionLedger(() => new DateTimeOffset(now));
    ledger.MarkWritten(@"C:\lib\tagged.mp4");
    var clip = new LibraryClip(@"C:\lib\tagged.mp4", "tagged.mp4", now, now, now.AddMinutes(-1));
    var ctx = ContextWith(new[] { clip }, baseline: Array.Empty<string>(), ledger: ledger);

    Assert.AreEqual(0, new RecentWriteSignal(() => now).Detect(ctx).Count());
}

// Helper, add to the test class:
private static WatchContext ContextWith(
    IReadOnlyList<LibraryClip> clips, IEnumerable<string> baseline, SelfActionLedger? ledger)
{
    var byName = clips.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => (IReadOnlyList<LibraryClip>)g.ToList(), StringComparer.OrdinalIgnoreCase);
    var byPath = clips.ToDictionary(c => c.FullPath, c => c, StringComparer.OrdinalIgnoreCase);
    return new WatchContext
    {
        LibraryClips = clips,
        ByFileName = byName,
        ByFullPath = byPath,
        PlayerWindows = Array.Empty<ProcessWindow>(),
        KnownBaselinePaths = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase),
        Ledger = ledger,
    };
}
```

- [ ] **Step 2: Run to verify the new tests fail**

Run: `dotnet test clipmetascribe.Tests --nologo --filter RecentWriteSignalTests`
Expected: FAIL, old predicate keys on write time, so the new-creation/old-write case returns 0 and baseline/ledger are ignored.

- [ ] **Step 3: Implement the new predicate**

```csharp
// clipmeta.core/Watching/RecentWriteSignal.cs, replace Detect's body
    public IEnumerable<SignalHit> Detect(WatchContext context)
    {
        DateTime now = _clock();
        DateTimeOffset nowOffset = new(now, TimeSpan.Zero);

        List<LibraryClip> fresh = context.LibraryClips
            .Where(c =>
                // (a) genuinely new to the library
                !context.KnownBaselinePaths.Contains(c.FullPath) &&
                // (b) created within the freshness window (creation time, not write time)
                now - c.CreationTimeUtc <= _window && now - c.CreationTimeUtc >= TimeSpan.Zero &&
                // (c) not a clip ClipMeta itself just wrote
                context.Ledger?.WasWrittenWithin(c.FullPath, _window, nowOffset) != true)
            .OrderByDescending(c => c.CreationTimeUtc)
            .ToList();

        bool ambiguous = fresh.Count > 1;
        foreach (LibraryClip clip in fresh)
            yield return new SignalHit(clip.FullPath, SourceName, Player: null, Ambiguous: ambiguous);
    }
```

Update the class doc comment to say "identified by a fresh `CreationTimeUtc`, excluding indexed and self-written paths."

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test clipmetascribe.Tests --nologo --filter RecentWriteSignalTests`
Expected: PASS (new + any pre-existing reconciled, see Step 5).

- [ ] **Step 5: Reconcile pre-existing `RecentWriteSignalTests`**

Pre-existing tests built clips with a fresh `LastWriteTimeUtc` and expected hits. Now creation time + baseline drive detection. For each: if it builds via the new `LibraryClip` ctor, give it a fresh `CreationTimeUtc` (now) and empty baseline/null ledger to keep the same intent; a test for "outside window" must set creation time outside the window. Justify each edit in the commit as the new contract, not a masked regression.

- [ ] **Step 6: Commit**

```bash
git add clipmeta.core/Watching/RecentWriteSignal.cs clipmetascribe.Tests/RecentWriteSignalTests.cs
git commit -m "fix(watching): recent_write keys on creation-time + baseline + self-ledger (P0-2)"
```

---

## Task 5: `AccessTimeSignal` self-read exclusion (P1-1)

**Files:**
- Modify: `clipmeta.core/Watching/AccessTimeSignal.cs`
- Test: `clipmetascribe.Tests/AccessTimeSignalTests.cs` (create or extend)

**Interfaces:**
- Consumes: `WatchContext.Ledger`, `SelfActionLedger.WasTouchedWithin`, `SelfActionLedger.DefaultWindow`.

- [ ] **Step 1: Write the failing test**

```csharp
// clipmetascribe.Tests/AccessTimeSignalTests.cs
using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class AccessTimeSignalTests
{
    private static WatchContext Ctx(IReadOnlyList<LibraryClip> clips, SelfActionLedger? ledger) => new()
    {
        LibraryClips = clips,
        ByFileName = clips.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LibraryClip>)g.ToList(), StringComparer.OrdinalIgnoreCase),
        ByFullPath = clips.ToDictionary(c => c.FullPath, c => c, StringComparer.OrdinalIgnoreCase),
        PlayerWindows = Array.Empty<ProcessWindow>(),
        Ledger = ledger,
    };

    [TestMethod]
    public void SelfReadClip_IsExcluded()
    {
        DateTime now = new(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc);
        var ledger = new SelfActionLedger(() => new DateTimeOffset(now));
        ledger.MarkRead(@"C:\lib\read.mp4");
        var clips = new[]
        {
            new LibraryClip(@"C:\lib\read.mp4", "read.mp4", now, now, now),
            new LibraryClip(@"C:\lib\other.mp4", "other.mp4", now.AddMinutes(-1), now, now),
        };

        var hits = new AccessTimeSignal().Detect(Ctx(clips, ledger)).Select(h => h.ClipPath).ToList();

        CollectionAssert.DoesNotContain(hits, @"C:\lib\read.mp4");
        CollectionAssert.Contains(hits, @"C:\lib\other.mp4");
    }

    [TestMethod]
    public void NoLedger_EmitsAll()
    {
        DateTime now = DateTime.UtcNow;
        var clips = new[] { new LibraryClip(@"C:\lib\a.mp4", "a.mp4", now, now, now) };
        Assert.AreEqual(1, new AccessTimeSignal().Detect(Ctx(clips, ledger: null)).Count());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo --filter AccessTimeSignalTests`
Expected: FAIL, `read.mp4` is still emitted.

- [ ] **Step 3: Implement the exclusion**

```csharp
// clipmeta.core/Watching/AccessTimeSignal.cs, replace Detect's body
    public IEnumerable<SignalHit> Detect(WatchContext context)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (LibraryClip clip in context.LibraryClips.OrderByDescending(c => c.LastAccessTimeUtc))
        {
            // Skip clips ClipMeta itself just read (export / get-metadata bump access time): a
            // diagnostic read must not float a dead file to the top of the fallback ranking.
            if (context.Ledger?.WasTouchedWithin(clip.FullPath, SelfActionLedger.DefaultWindow, now) == true)
                continue;
            yield return new SignalHit(clip.FullPath, SourceName, Player: null, Ambiguous: true);
        }
    }
```

Update the class doc to note the self-read exclusion.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test clipmetascribe.Tests --nologo --filter AccessTimeSignalTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/AccessTimeSignal.cs clipmetascribe.Tests/AccessTimeSignalTests.cs
git commit -m "fix(watching): access_time excludes ClipMeta's own reads (P1-1)"
```

---

## Task 6: Thread the ledger through the resolver

**Files:**
- Modify: `clipmeta.core/Watching/WatchingResolver.cs`
- Test: `clipmetascribe.Tests/WatchingResolverTests.cs` (extend + reconcile)

**Interfaces:**
- Consumes: `SelfActionLedger`; `WatchContext.Build(root, source, names, ledger)` / `Build(root, windows, ledger)` (Task 3).
- Produces: `WatchingResolver` ctor gains a trailing `SelfActionLedger? ledger = null`; `CreateDefault(IProcessWindowSource, SelfActionLedger? ledger = null)`; `Resolve`/`ResolveReview` pass the held ledger into `WatchContext.Build`.

- [ ] **Step 1: Write the failing test**, a self-written fresh clip is not a gaming live target

```csharp
// clipmetascribe.Tests/WatchingResolverTests.cs, ADD
[TestMethod]
public void Resolve_SingleFreshClip_SelfWritten_IsNotLiveTarget()
{
    string dir = MakeLibrary();           // existing helper that returns a temp library dir
    try
    {
        string clip = Path.Combine(dir, "fresh.mp4");
        File.WriteAllBytes(clip, new byte[] { 0, 1, 2 });   // fresh creation time

        var ledger = new SelfActionLedger();
        ledger.MarkWritten(clip);          // ClipMeta wrote it -> not a user save

        var resolver = WatchingResolver.CreateDefault(new EmptyProcessWindowSource(), ledger);
        WatchingResult result = resolver.Resolve(dir, limit: 5, includeAccessFallback: true);

        Assert.IsFalse(result.Candidates.Any(c => c.Source == "recent_write"));
    }
    finally { Directory.Delete(dir, recursive: true); }
}
```

> Uses `EmptyProcessWindowSource` (exists in Core) so no player resolves. If `MakeLibrary` isn't the helper name in this file, use the file's existing temp-dir helper.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo --filter WatchingResolverTests`
Expected: FAIL, `CreateDefault` has no ledger overload; the fresh clip still resolves as `recent_write`.

- [ ] **Step 3: Implement the threading**

```csharp
// WatchingResolver.cs, add field + ctor param
    private readonly SelfActionLedger? _ledger;

    public WatchingResolver(
        IReadOnlyList<IWatchSignal> signals,
        IProcessWindowSource windowSource,
        IReadOnlyCollection<string>? playerNames = null,
        SelfActionLedger? ledger = null)
    {
        _signals = signals;
        _windowSource = windowSource;
        _playerNames = playerNames ?? MediaPlayers.KnownProcessNames;
        _ledger = ledger;
    }

    public static WatchingResolver CreateDefault(
        IProcessWindowSource windowSource, SelfActionLedger? ledger = null) =>
        new(
            new IWatchSignal[] { new PlayerTitleSignal(), new RecentWriteSignal(), new AccessTimeSignal() },
            windowSource, playerNames: null, ledger: ledger);
```

Pass `_ledger` at both `WatchContext.Build` call sites:

```csharp
    // in Resolve(...)
    WatchContext context = WatchContext.Build(libraryRoot, _windowSource, _playerNames, _ledger);
```

```csharp
    // in ResolveReview(...)
    WatchContext context = WatchContext.Build(libraryRoot, windows, _ledger);
```

- [ ] **Step 4: Run to verify it passes + reconcile existing resolver tests**

Run: `dotnet test clipmetascribe.Tests --nologo --filter WatchingResolverTests`
Expected: PASS. Existing gaming-mode tests that created a fresh file and expected a `recent_write` live target still pass (empty baseline, null ledger). Any that relied on a back-dated **write** time to look "old" must also back-date **creation** time, update the `TouchStale` helper to set both, and add a `TouchCreated(path, DateTime)` helper:

```csharp
// In the test helper region:
private static void TouchStale(string path)   // make a clip look old to BOTH signals
{
    var old = DateTime.UtcNow.AddDays(-1);
    File.SetLastWriteTimeUtc(path, old);
    File.SetLastAccessTimeUtc(path, old);
    File.SetCreationTimeUtc(path, old);
}

private static void TouchCreated(string path, DateTime creationUtc) =>
    File.SetCreationTimeUtc(path, creationUtc);
```

Reconcile each shifted assertion as correct new behavior; note it in the commit.

- [ ] **Step 5: Run the full scribe watching suite**

Run: `dotnet test clipmetascribe.Tests --nologo --filter Watch`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add clipmeta.core/Watching/WatchingResolver.cs clipmetascribe.Tests/WatchingResolverTests.cs
git commit -m "feat(watching): inject SelfActionLedger through the resolver"
```

---

## Task 7: Wire `SelfActionLedger` into the MCP server (mark writes/reads, inject into the live resolver)

**Files:**
- Modify: `clipmetamcp/Program.cs` (build one ledger, inject it), `clipmetamcp/Tools/WriteTools.cs` (mark writes), `clipmetamcp/Tools/ReadTools.cs` (mark reads; pass ledger to `CreateDefault`)
- Test: `clipmetamcp.Tests` (a just-written clip is not surfaced as `recent_write`)

**Interfaces:**
- Consumes: `SelfActionLedger` (Task 1); `WatchingResolver.CreateDefault(source, ledger)` (Task 6).
- Produces: a single process-wide `SelfActionLedger` threaded into the read/write tool registrations. Final registration signatures (trailing optionals, accumulated across tasks): `ReadTools.RegisterAll(registry, sandbox, ReviewWatcher? watcher = null, SelfActionLedger? ledger = null, DrainJournal? journal = null)` (this task adds `ledger`; Task 8 adds `journal`); `WriteTools.RegisterAll(registry, sandbox, SelfActionLedger? ledger = null)`.

> Why this task is separate from Task 6: Task 6 lets the resolver *read* a ledger (Core, unit-tested). This task *populates* it in production, without it the ledger is always empty and P0-2/P1-1's exclusions never fire in real use.

- [ ] **Step 1: Write the failing test**, a clip ClipMeta just wrote is not a `recent_write` live target

```csharp
// clipmetamcp.Tests, in the write/watching integration test class
[TestMethod]
public void Watching_DoesNotSurface_AClipThisSessionWrote_AsRecentWrite()
{
    // Shared ledger across the write + read tools, mirroring Program.cs wiring.
    var ledger = new SelfActionLedger();
    // Register write + read tools with `ledger` against a scratch library sandbox (use the harness'
    // existing sandbox/registry setup). Create a fresh clip, write a tag to it via clip_set_fields,
    // then invoke library_watching. The fresh clip must NOT appear with source "recent_write".
    // Assert: no candidate where source == "recent_write" && path == the just-written clip.
}
```

> Build this on the harness the existing write-tool tests already use (scratch clip + sandbox + tool invocation). The key is that the SAME `SelfActionLedger` instance backs both the write tool (which marks) and the watching resolver (which reads).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test clipmetamcp.Tests --nologo --filter Watching_DoesNotSurface`
Expected: FAIL, nothing marks the write, so the fresh clip resolves as `recent_write`.

- [ ] **Step 3: Build the ledger in `Program.cs` and inject it**

```csharp
// Program.cs, one ledger for the process, shared by writers (mark) and the resolver (read)
            var selfLedger = new SelfActionLedger();
```

Pass it to the registrations: `WriteTools.RegisterAll(registry, sandbox, selfLedger);` and `ReadTools.RegisterAll(registry, sandbox, reviewWatcher, selfLedger);` (Task 8 appends `drainJournal` after `selfLedger`).

- [ ] **Step 4: Mark writes in `WriteTools`**

`RegisterAll` gains `SelfActionLedger? ledger = null`; each handler lambda captures it (`args => SetFields(args, sandbox, ledger)`, etc.), threading `ledger` to `ExecuteWrite`. In `ExecuteWrite`, after the write succeeds (just before the ground-truth read-back), mark it:

```csharp
        ledger?.MarkWritten(fullPath);
```

(Place it after `WriteGate.Exit()` / the successful write, so a refused write never marks.)

- [ ] **Step 5: Mark reads in `ReadTools`; inject ledger into the resolver**

`RegisterAll` gains `SelfActionLedger? ledger = null` (after `watcher`). Thread it to the `GetMetadata`, `ExportLibrary`, and `Watching` handlers (capture in the lambdas).

- In `GetMetadata`, after a successful parse/read, add `ledger?.MarkRead(fullPath);`.
- In `ExportLibrary`, mark each exported clip path read: `ledger?.MarkRead(path);` per record (these content reads are the access-time polluters).
- In `Watching`, pass the ledger to the resolver:

```csharp
        var resolver = WatchingResolver.CreateDefault(ProcessWindowSource.ForCurrentPlatform(), ledger);
```

Do NOT mark in `ListLibrary` (directory names only, low pollution, safe for baseline).

- [ ] **Step 6: Run to verify it passes + build**

Run: `dotnet build --nologo -v q` then `dotnet test clipmetamcp.Tests --nologo --filter Watching_DoesNotSurface`
Expected: build 0/0; PASS.

- [ ] **Step 7: Commit**

```bash
git add clipmetamcp/Program.cs clipmetamcp/Tools/WriteTools.cs clipmetamcp/Tools/ReadTools.cs clipmetamcp.Tests/
git commit -m "feat(mcp): populate SelfActionLedger from writes/reads, inject into resolver"
```

---

## Task 8: Drain visibility, `onWritten` callback + pump journal + MCP surface (P0-1)

**Files:**
- Modify: `clipmeta.core/Watching/TagQueue.cs` (callback + extract `ChangedFields`)
- Modify: `clipmeta.core/Watching/QueueDrainPump.cs` (record to journal)
- Modify: `clipmetamcp/Program.cs` (build + inject `DrainJournal`)
- Modify: `clipmetamcp/Tools/ReadTools.cs` (surface `autoFlushed` in `library_watching`)
- Modify: `clipmetamcp/Tools/QueueTools.cs` (surface `autoFlushed` in flush/status)
- Test: `clipmetascribe.Tests/QueueDrainPumpTests.cs` (extend) + `clipmetamcp.Tests` watching/queue tests

**Interfaces:**
- Consumes: `DrainedTag`, `DrainJournal` (Task 2).
- Produces: `TagQueue.Drain(..., Action<DrainedTag>? onWritten = null)`; `QueueDrainPump` ctor gains trailing `DrainJournal? journal = null`; MCP `RegisterAll` overloads gain `DrainJournal? journal`.

- [ ] **Step 1: Write the failing test**, the pump records each auto-flush

```csharp
// clipmetascribe.Tests/QueueDrainPumpTests.cs, ADD (use this file's existing fakes/helpers)
[TestMethod]
public void Pump_RecordsAutoFlush_IntoJournal()
{
    string dir = NewQueueDir();                 // existing helper: temp library dir
    string clip = Path.Combine(dir, "a.mp4");
    File.WriteAllBytes(clip, MinimalMp4());      // existing helper producing a writable clip
    TagQueue.Enqueue(dir, clip,
        Mutation(("tags", "headshot")), confidence: "high");   // existing mutation helper

    var journal = new DrainJournal();
    using var pump = new QueueDrainPump(
        dir, new Mp4Writer(), NullLogger.Instance,
        isInUse: _ => false,                     // lock already clear -> drains immediately
        runExclusive: a => a(),
        pollInterval: TimeSpan.FromMilliseconds(20),
        journal: journal);
    pump.Start();
    pump.Wake();

    Assert.IsTrue(SpinUntil(() => journal.TakePending().Count > 0, seconds: 15) == false
        ? false : true);   // see note below, prefer a single TakePending after the wait
}
```

> Replace the awkward assert with the file's existing wait idiom: spin (generously, 15s, the pass-3 background-timing lesson) until a drain lands, then `var taken = journal.TakePending();` and assert `taken.Single().Path == clip` and `taken[0].Fields` contains `"tags"`. Take ONCE (report-once clears).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo --filter QueueDrainPumpTests`
Expected: FAIL, pump ctor has no `journal` param.

- [ ] **Step 3: Implement, `TagQueue` callback + `ChangedFields`**

In `TagQueue.Drain`, add the trailing param and invoke on success:

```csharp
    public static DrainReport Drain(
        string libraryDir, IMediaWriter writer, IClipMetaLogger logger, Func<string, bool> isInUse,
        Action<DrainedTag>? onWritten = null)
    {
        // ... unchanged setup ...
            try
            {
                writer.WriteMetadata(entry.ClipPath, entry.Mutation.ToMutation(), logger);
                written.Add(entry.ClipPath);
                onWritten?.Invoke(new DrainedTag(
                    entry.ClipPath, ChangedFields(entry.Mutation), DateTimeOffset.UtcNow));
            }
        // ... unchanged catch ...
    }
```

Extract the changed-field logic (reused by `Status`):

```csharp
    /// <summary>User-facing names of the fields a queued mutation changes (set/append/delete).</summary>
    private static List<string> ChangedFields(QueuedMutation mutation)
    {
        var changed = new List<string>();
        changed.AddRange(mutation.SetFields.Keys.Select(DisplayField));
        changed.AddRange(mutation.AppendFields.Keys.Select(DisplayField));
        changed.AddRange(mutation.DeleteFields.Select(DisplayField));
        return changed;
    }
```

In `Status`, replace the three `changed.AddRange(...)` lines with `var changed = ChangedFields(e.Mutation);`.

- [ ] **Step 4: Implement, pump records to journal**

```csharp
// QueueDrainPump.cs, add field + ctor param
    private readonly DrainJournal? _journal;

    public QueueDrainPump(
        string libraryRoot, IMediaWriter writer, IClipMetaLogger logger,
        Func<string, bool> isInUse, Action<Action> runExclusive, TimeSpan pollInterval,
        DrainJournal? journal = null)
    {
        // ... existing assignments ...
        _journal = journal;
    }
```

```csharp
// DrainOnce, record each auto-flush
    private DrainReport DrainOnce()
    {
        DrainReport result = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        _runExclusive(() => result = TagQueue.Drain(
            _libraryRoot, _writer, _logger, _isInUse,
            onWritten: tag => _journal?.Record(tag)));
        return result;
    }
```

- [ ] **Step 5: Run the pump test**

Run: `dotnet test clipmetascribe.Tests --nologo --filter QueueDrainPumpTests`
Expected: PASS. (Synchronous drains pass `onWritten: null`, so they never double-record, only the pump feeds the journal.)

- [ ] **Step 6: Implement, MCP wiring (`Program.cs`)**

```csharp
// Program.cs, build the journal once, inject into pump + tool registration
            var drainJournal = new DrainJournal();
            QueueDrainPump? pump = null;
            if (sandbox.Root is { } libraryRoot)
            {
                pump = new QueueDrainPump(
                    libraryRoot, new Mp4Writer(), logger, LockProbe.IsInUse,
                    runExclusive: action => { WriteGate.Enter(); try { action(); } finally { WriteGate.Exit(); } },
                    pollInterval: TimeSpan.FromSeconds(3),
                    journal: drainJournal);
                pump.Start();
            }
```

Pass `drainJournal` to the read/queue registrations. `ReadTools.RegisterAll` now takes both the Task-7 `selfLedger` and the journal: `ReadTools.RegisterAll(registry, sandbox, reviewWatcher, selfLedger, drainJournal);` and `QueueTools.RegisterAll(registry, sandbox, pump, drainJournal);`.

- [ ] **Step 7: Implement, surface `autoFlushed` in `library_watching`**

In `ReadTools.RegisterAll`, add a trailing `DrainJournal? journal = null` and thread it to the `Watching` handler. In `Watching(...)`, before `return response;`:

```csharp
        // P0-1: surface tags the BACKGROUND pump auto-flushed since the last call (it writes the last
        // clip when its player closes but reports to no one). Report-once: TakePending clears.
        var autoFlushed = new JsonArray();
        foreach (DrainedTag t in journal?.TakePending() ?? Array.Empty<DrainedTag>())
        {
            var fields = new JsonArray();
            foreach (string f in t.Fields) fields.Add(f);
            autoFlushed.Add(new JsonObject
            {
                ["path"] = t.Path,
                ["fields"] = fields,
                ["agoSeconds"] = Math.Round((DateTimeOffset.UtcNow - t.WhenUtc).TotalSeconds, 1),
            });
        }
        response["autoFlushed"] = autoFlushed;
```

- [ ] **Step 8: Implement, surface `autoFlushed` in flush/status**

In `QueueTools.RegisterAll`, add a trailing `DrainJournal? journal = null`; thread to `FlushQueue` and `QueueStatus`. Add a shared helper and include it:

```csharp
    private static JsonArray AutoFlushedJson(DrainJournal? journal)
    {
        var arr = new JsonArray();
        foreach (DrainedTag t in journal?.TakePending() ?? Array.Empty<DrainedTag>())
        {
            var fields = new JsonArray();
            foreach (string f in t.Fields) fields.Add(f);
            arr.Add(new JsonObject
            {
                ["path"] = t.Path,
                ["fields"] = fields,
                ["agoSeconds"] = Math.Round((DateTimeOffset.UtcNow - t.WhenUtc).TotalSeconds, 1),
            });
        }
        return arr;
    }
```

`FlushQueue` returns `DrainJson(drain)` plus `["autoFlushed"] = AutoFlushedJson(journal)`; `QueueStatus`'s result gets `["autoFlushed"] = AutoFlushedJson(journal)` too.

- [ ] **Step 9: Write the MCP behavior test**

```csharp
// clipmetamcp.Tests, in the watching/queue test class
[TestMethod]
public void Watching_SurfacesAutoFlushed_FromJournal()
{
    var journal = new DrainJournal();
    journal.Record(new DrainedTag(@"C:\lib\a.mp4", new[] { "tags" }, DateTimeOffset.UtcNow));
    // Build the watching response via the same handler path the tests already use, passing `journal`.
    // Assert response["autoFlushed"][0]["path"] == @"C:\lib\a.mp4" and a SECOND call's autoFlushed is empty.
}
```

> Mirror the existing watching-tool test's construction (it already builds a sandbox + handler). If the handler isn't directly callable, assert through the registered `ToolDefinition` the other tests use.

- [ ] **Step 10: Build + FULL MCP suite**

Run: `dotnet build --nologo -v q` then `dotnet test clipmetamcp.Tests --nologo`
Expected: build 0/0; full MCP suite green (run the WHOLE project, `ToolsList_ContainsTheFullToolSurface` still asserts 17 tools, unaffected).

- [ ] **Step 11: Commit**

```bash
git add clipmeta.core/Watching/TagQueue.cs clipmeta.core/Watching/QueueDrainPump.cs clipmetamcp/Program.cs clipmetamcp/Tools/ReadTools.cs clipmetamcp/Tools/QueueTools.cs clipmetascribe.Tests/QueueDrainPumpTests.cs clipmetamcp.Tests/
git commit -m "fix(queue): surface silent pump auto-flushes via DrainJournal (P0-1)"
```

---

## Task 9: Player roster soft-advisory guard (P1-2)

**Files:**
- Create: `clipmeta.core/Schema/PlayerRosterGuard.cs`
- Test: `clipmetascribe.Tests/PlayerRosterGuardTests.cs`
- Modify: `clipmetamcp/Tools/WriteTools.cs` (`clip_set_fields`, `clip_append_field`: `roster` arg + advisory)
- Modify: `clipmetamcp/Tools/QueueTools.cs` (`library_queue_tag`: `roster` arg + advisory)
- Test: `clipmetamcp.Tests` write/queue tests

**Interfaces:**
- Consumes: `ClipMetaVocab.Enumerate(root, field)` → `VocabResult.Counts` (keys are known player names); `ClipMetaSchema.Players`.
- Produces: `static class PlayerRosterGuard` with `IReadOnlyList<string> UnknownPlayers(string? playersValue, IReadOnlySet<string> known)`.

- [ ] **Step 1: Write the failing Core test**

```csharp
// clipmetascribe.Tests/PlayerRosterGuardTests.cs
using ClipMetaCore.Schema;

namespace ClipMetaScribe.Tests;

[TestClass]
public class PlayerRosterGuardTests
{
    private static IReadOnlySet<string> Known(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    public void Unknown_FlagsTokensNotInKnownSet()
    {
        var unknown = PlayerRosterGuard.UnknownPlayers("chuck|miami element", Known("chuck", "chicken"));
        CollectionAssert.AreEqual(new[] { "miami element" }, unknown.ToArray());
    }

    [TestMethod]
    public void Unknown_IsCaseInsensitive_AndDeduped()
    {
        var unknown = PlayerRosterGuard.UnknownPlayers("Chuck|chuck|Bob|bob", Known("chuck"));
        CollectionAssert.AreEqual(new[] { "Bob" }, unknown.ToArray());
    }

    [TestMethod]
    public void Unknown_EmptyOrAllKnown_ReturnsEmpty()
    {
        Assert.AreEqual(0, PlayerRosterGuard.UnknownPlayers("", Known("chuck")).Count);
        Assert.AreEqual(0, PlayerRosterGuard.UnknownPlayers("chuck|chicken", Known("chuck", "chicken")).Count);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo --filter PlayerRosterGuardTests`
Expected: FAIL, `PlayerRosterGuard` does not exist.

- [ ] **Step 3: Implement the Core guard**

```csharp
// clipmeta.core/Schema/PlayerRosterGuard.cs
namespace ClipMetaCore.Schema;

/// <summary>
/// Pure check behind the soft "unknown player" advisory: given a pipe-delimited players value and the
/// known-player set (library vocab ∪ a session roster), returns the tokens that match neither, names
/// the model should confirm with the user before they stick (e.g. "miami element" is a warpaint, not a
/// person). The write is never blocked here; this only identifies what to flag.
/// </summary>
public static class PlayerRosterGuard
{
    /// <summary>Tokens in <paramref name="playersValue"/> absent from <paramref name="known"/> (first occurrence, in order).</summary>
    public static IReadOnlyList<string> UnknownPlayers(string? playersValue, IReadOnlySet<string> known)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (string.IsNullOrWhiteSpace(playersValue))
            return Array.Empty<string>();

        var unknown = new List<string>();
        foreach (string token in playersValue.Split(
                     '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!known.Contains(token) && !unknown.Contains(token, StringComparer.OrdinalIgnoreCase))
                unknown.Add(token);
        }
        return unknown;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test clipmetascribe.Tests --nologo --filter PlayerRosterGuardTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Add the MCP advisory helper** (shared by write + queue tools)

Add to `ReadTools` (already the shared helper home) an internal method:

```csharp
    /// <summary>
    /// Builds the soft "unknownPlayer" review array for a players value, or null when every token is
    /// known. Known = library vocab players ∪ the optional session roster arg. Never blocks the write.
    /// </summary>
    internal static JsonArray? UnknownPlayerReview(string? playersValue, string root, JsonArray? roster)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in ClipMetaVocab.Enumerate(root, ClipMetaSchema.Players).Counts.Keys)
            known.Add(name);
        if (roster is not null)
            foreach (JsonNode? n in roster)
                if (n?.GetValue<string>() is { Length: > 0 } s)
                    known.Add(s.Trim());

        IReadOnlyList<string> unknown = PlayerRosterGuard.UnknownPlayers(playersValue, known);
        if (unknown.Count == 0)
            return null;

        var knownArr = new JsonArray();
        foreach (string k in known.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            knownArr.Add(k);

        var review = new JsonArray();
        foreach (string token in unknown)
            review.Add(new JsonObject
            {
                ["type"] = "unknownPlayer",
                ["token"] = token,
                ["knownPlayers"] = knownArr.DeepClone(),
            });
        return review;
    }
```

Add `using ClipMetaCore.Read;` / `ClipMetaCore.Schema` as needed (for `ClipMetaVocab` and `ClipMetaSchema`).

- [ ] **Step 6: Wire into the write tools**

In `WriteTools.CommonWriteProperties()` add a `roster` property:

```csharp
        ["roster"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string" },
            ["description"] = "Optional: tonight's player names. A 'players' value outside this list " +
                              "and the library's known players is flagged (not blocked) so you can " +
                              "confirm it's a person and not a tag. Name players up front to reduce flags.",
        },
```

In `SetFields`, after building the mutation and before/within `ExecuteWrite`'s describe callback, compute the advisory from the raw `players` field value and attach it. Simplest: capture the players value while iterating `fieldArgs`, then in the `describeChange` lambda add the review:

```csharp
        string? playersValue = fieldArgs["players"] is JsonValue pv && pv.TryGetValue(out string? pvs) ? pvs : null;
        string root = sandbox.RequireRoot();
        JsonArray? roster = args?["roster"] as JsonArray;

        return ExecuteWrite(args, sandbox, mutation, result =>
        {
            if (set.Count > 0) result["setFields"] = set;
            if (deleted.Count > 0) result["deletedFields"] = deleted;
            if (ReadTools.UnknownPlayerReview(playersValue, root, roster) is { } review)
                result["review"] = review;
        });
```

In `AppendField`, when `field == ClipMetaSchema.Players`, attach the advisory for `value` the same way.

- [ ] **Step 7: Wire into `library_queue_tag`**

Add the same `roster` property to `QueueTagSchema()`. In `QueueTag`, capture the `players` value from `fieldArgs`, and add to the returned JsonObject:

```csharp
        if (ReadTools.UnknownPlayerReview(playersValue, root, args?["roster"] as JsonArray) is { } review)
            result["review"] = review;   // result = the JsonObject already being returned
```

Also append a one-line "name players up front…" sentence to the `library_queue_tag` and `clip_set_fields` tool descriptions.

- [ ] **Step 8: Write the MCP behavior tests**

```csharp
// clipmetamcp.Tests, write tools
// (a) clip_set_fields players "miami element" with no roster + empty vocab -> result["review"][0]["type"]=="unknownPlayer", token=="miami element", AND the write still landed (fields contains players).
// (b) same call with roster:["miami element"] -> no "review" key.
// (c) players "chuck" when "chuck" already in vocab (seed a clip first) -> no "review" key.
```

Build these against the existing write-tool test harness (it already creates a sandbox + scratch clip and invokes the tool). Assert the write still occurred in (a) (soft, not blocked).

- [ ] **Step 9: Build + FULL MCP suite**

Run: `dotnet build --nologo -v q` then `dotnet test clipmetamcp.Tests --nologo`
Expected: build 0/0; full MCP suite green (tool count still 17; schemas gained `roster` but the surface test asserts the tool set/order, not schema internals).

- [ ] **Step 10: Commit**

```bash
git add clipmeta.core/Schema/PlayerRosterGuard.cs clipmetascribe.Tests/PlayerRosterGuardTests.cs clipmetamcp/Tools/ReadTools.cs clipmetamcp/Tools/WriteTools.cs clipmetamcp/Tools/QueueTools.cs clipmetamcp.Tests/
git commit -m "feat(write): soft unknownPlayer roster advisory (P1-2)"
```

---

## Task 10: Version bump, docs, repack

**Files:**
- Modify: `clipmetamcp/clipmetamcp.csproj`, `tools/mcpb-manifest.json`, `docs/PITFALLS.md`, `CLAUDE.md` (only if a stated fact changed)

- [ ] **Step 1: Bump version to 1.4.0**

In `clipmetamcp/clipmetamcp.csproj` set both `<AssemblyVersion>1.4.0</AssemblyVersion>` and `<InformationalVersion>1.4.0</InformationalVersion>`. In `tools/mcpb-manifest.json` set `"version": "1.4.0"`.

- [ ] **Step 2: Record the PITFALLS entries**

Append to `docs/PITFALLS.md`:
- Two silent drainers: the background pump writes correctly but discards its report, so user-facing drains saw an empty queue → always wire a silent background writer to a report-once journal the foreground surfaces.
- `recent_write` must key on **creation** time, not write time: copy-into-library preserves source mtime (fresh looks old) and ClipMeta's own `File.Replace` bumps mtime (self-write looks fresh). Creation time + index baseline + self-ledger is the fix.
- Adding the ledger turns any fixture's implicit access/write/creation time into signal input, `TouchStale` must set all three; assert with explicit timestamps.

- [ ] **Step 3: Full build + both suites**

Run: `dotnet build --nologo -v q` then `dotnet test --nologo --no-build -v q`
Expected: 0/0 build; all of `clipmetascribe.Tests` + `clipmetamcp.Tests` + `clipmetaview.Tests` green.

- [ ] **Step 4: Repack the bundle**

Run: `pwsh tools/pack-mcpb.ps1` (the version gate passes only because csproj + manifest agree at 1.4.0). Confirm `dist/clipmeta.mcpb` rebuilt.

- [ ] **Step 5: Commit**

```bash
git add clipmetamcp/clipmetamcp.csproj tools/mcpb-manifest.json docs/PITFALLS.md
git commit -m "chore: bump to v1.4.0, record pass-5 pitfalls, repack .mcpb"
```

> Do NOT `git add` `dist/clipmeta.mcpb` unless the repo already tracks it (check `git status` first, match the existing convention from the last repack). Do NOT touch `docs/index.html` / `docs/BUILD-LOG.md`.

---

## Completion

After all tasks: announce and use **superpowers:finishing-a-development-branch** to verify tests, then push + open a PR (the owner merges). The PR body must justify every reconciled existing-test change as new-contract behavior, list the four fixes against their dogfood IDs (P0-1, P0-2, P1-1, P1-2), and note v1.4.0 + repack so the owner knows the bundle is ready to reinstall.
