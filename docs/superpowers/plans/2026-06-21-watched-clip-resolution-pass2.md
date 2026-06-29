# Watched-Clip Resolution, Pass 2 (Deferred-Tag Queue) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist confirmed metadata tags for clips that are locked by a playing media player, then drain them to the files as the locks clear, so you can tag faster than writes can land.

**Architecture:** A new Core `TagQueue` engine persists a JSON queue (`.clipmeta-queue`) in the library root, with corruption-tolerant load, atomic temp-swap save, merge-enqueue, and a single-threaded drain that writes each entry whose lock has cleared through the existing `Mp4Writer`. The MCP server gains `library_queue_tag` / `library_flush_queue` / `library_queue_status` and drains opportunistically on every watched-clip call; the CLI gains `--flush-queue` and a pending-count footer on `--watching`. The queue stores only already-resolved, confirmed paths, it never resolves or guesses.

**Tech Stack:** C# / .NET 10, `System.Text.Json` (BCL), MSTest. Design spec: `docs/superpowers/specs/2026-06-21-watched-clip-resolution-pass2-design.md`.

## Global Constraints

- **.NET 10**; solution `peckworks-clipmeta.slnx`.
- **Zero external NuGet packages** in production projects (`clipmeta.core`, `clipmetascribe`, `clipmetamcp`). `System.Text.Json` is BCL, allowed. Test projects may use MSTest only.
- **CLIs/MCP are thin shells**, no business logic in `Program.cs` or a tool handler; delegate to Core.
- **Big-endian MP4 IO** stays inside the write engine; pass-2 never touches box bytes directly, it calls `Mp4Writer.WriteMetadata`.
- **Build:** `dotnet build --nologo -v q` → 0 warnings, 0 errors.
- **Test:** `dotnet test --nologo --no-build -v q` → all pass. `clipmetascribe.Tests` takes a few minutes (real-clip integration), use a long timeout; not a hang.
- **XML doc comments** on every public type/method; named constants, no magic numbers.
- **Changed the MCP tool surface → run the FULL `clipmetamcp.Tests` project, not a `--filter`.** `ToolsList_ContainsTheFullToolSurface` asserts the exact tool set and order.
- New gotchas → `docs/PITFALLS.md`. Tool-count + repack note → `MEMORY.md` / memory store.
- Branch already created: `feat/watched-clip-resolution-pass2` (the spec is committed there).

---

## File Structure

**Create (Core, `clipmeta.core/Watching/`):**
- `QueuedMutation.cs`, durable mutation DTO + mapping to/from `MetadataMutation`.
- `QueuedTag.cs`, one queue entry record.
- `TagQueueData.cs`, queue file model (version + entries).
- `DrainReport.cs`, result of a drain (written / still-queued / dropped).
- `QueueStatusEntry.cs`, one status row.
- `TagQueue.cs`, the engine: file path, load, save, enqueue, drain, status.

**Create (MCP, `clipmetamcp/Tools/`):**
- `WriteGate.cs`, shared single-flight gate (extracted from `WriteTools`).
- `QueueTools.cs`, registers `library_queue_tag` / `library_flush_queue` / `library_queue_status`.

**Create (CLI, `clipmetascribe/Commands/`):**
- `FlushQueueCommand.cs`, thin shell → `TagQueue.Drain`, prints the report.

**Modify:**
- `clipmetamcp/Tools/WriteTools.cs`, use the shared `WriteGate` instead of its private semaphore.
- `clipmetamcp/Program.cs` (or wherever `RegisterAll` is called), call `QueueTools.RegisterAll`.
- `clipmetamcp.Tests/Phase2ReadToolsTests.cs`, extend `ToolsList_ContainsTheFullToolSurface` with the 3 new names.
- `clipmetascribe/Program.cs`, route `--flush-queue`; add `--flush-queue` to known flags + help; add the pending footer to the `--watching` branch.

**Test files:**
- `clipmetascribe.Tests/TagQueueTests.cs` (Core engine, through the scribe test project per convention).
- `clipmetascribe.Tests/QueuedMutationTests.cs` (mapping).
- `clipmetamcp.Tests/QueueToolsTests.cs` (MCP shape + behavior).

---

### Task 1: `QueuedMutation` DTO + mapping

**Files:**
- Create: `clipmeta.core/Watching/QueuedMutation.cs`
- Test: `clipmetascribe.Tests/QueuedMutationTests.cs`

**Interfaces:**
- Consumes: `ClipMetaCore.Write.MetadataMutation` (existing, `SetFields: Dictionary<string,string?>`, `AppendFields: Dictionary<string,string>`, `DeleteFields: HashSet<string>`, `ClearAll: bool`, plus transient `DryRun`/`BackupPath`).
- Produces: `QueuedMutation` record with `From(MetadataMutation)` and `ToMutation()`.

- [ ] **Step 1: Write the failing test**

```csharp
// clipmetascribe.Tests/QueuedMutationTests.cs
using ClipMetaCore.Watching;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Tests;

[TestClass]
public class QueuedMutationTests
{
    [TestMethod]
    public void From_DropsTransientFlags_AndCapturesDurableState()
    {
        var m = new MetadataMutation { DryRun = true, BackupPath = "x.bak", ClearAll = false };
        m.SetFields["game"] = "TF2";
        m.AppendFields["tags"] = "headshot";
        m.DeleteFields.Add("notes");

        QueuedMutation q = QueuedMutation.From(m);

        Assert.AreEqual("TF2", q.SetFields["game"]);
        Assert.AreEqual("headshot", q.AppendFields["tags"]);
        CollectionAssert.AreEquivalent(new[] { "notes" }, q.DeleteFields.ToList());
        Assert.IsFalse(q.ClearAll);
    }

    [TestMethod]
    public void ToMutation_RoundTrips_AndClearsTransientFlags()
    {
        var original = new MetadataMutation { ClearAll = true };
        original.SetFields["game"] = "TF2";

        MetadataMutation rebuilt = QueuedMutation.From(original).ToMutation();

        Assert.AreEqual("TF2", rebuilt.SetFields["game"]);
        Assert.IsTrue(rebuilt.ClearAll);
        Assert.IsFalse(rebuilt.DryRun);
        Assert.IsNull(rebuilt.BackupPath);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter QueuedMutationTests`
Expected: FAIL, `QueuedMutation` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// clipmeta.core/Watching/QueuedMutation.cs
using ClipMetaCore.Write;

namespace ClipMetaCore.Watching;

/// <summary>
/// The durable subset of a <see cref="MetadataMutation"/>, the field changes worth persisting in
/// the deferred-tag queue. Deliberately omits the transient write-time flags (<c>DryRun</c>,
/// <c>BackupPath</c>) so the on-disk queue schema is independent of how a write is executed.
/// </summary>
public sealed record QueuedMutation(
    /// <summary>Fields to set; a null/empty value deletes (the schema's delete idiom).</summary>
    IReadOnlyDictionary<string, string?> SetFields,
    /// <summary>Fields whose values are appended (pipe-list merge on write).</summary>
    IReadOnlyDictionary<string, string> AppendFields,
    /// <summary>Field names to delete entirely.</summary>
    IReadOnlyList<string> DeleteFields,
    /// <summary>When true, remove ALL clipmeta atoms from the file.</summary>
    bool ClearAll)
{
    /// <summary>Captures the durable state of <paramref name="mutation"/>, dropping transient flags.</summary>
    public static QueuedMutation From(MetadataMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return new QueuedMutation(
            new Dictionary<string, string?>(mutation.SetFields),
            new Dictionary<string, string>(mutation.AppendFields),
            mutation.DeleteFields.ToList(),
            mutation.ClearAll);
    }

    /// <summary>
    /// Rebuilds a <see cref="MetadataMutation"/> for the write engine. <c>DryRun</c> is false and
    /// <c>BackupPath</c> is null, a drained tag is a real write and backups are a per-call policy
    /// concern, not a durable one.
    /// </summary>
    public MetadataMutation ToMutation()
    {
        var m = new MetadataMutation { ClearAll = ClearAll };
        foreach (var (k, v) in SetFields) m.SetFields[k] = v;
        foreach (var (k, v) in AppendFields) m.AppendFields[k] = v;
        foreach (string d in DeleteFields) m.DeleteFields.Add(d);
        return m;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build --nologo -v q && dotnet test clipmetascribe.Tests --nologo --no-build -v q --filter QueuedMutationTests`
Expected: PASS (2/2).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/QueuedMutation.cs clipmetascribe.Tests/QueuedMutationTests.cs
git commit -m "feat(core): QueuedMutation durable DTO for the deferred-tag queue"
```

---

### Task 2: Queue records, `QueuedTag`, `TagQueueData`, `DrainReport`, `QueueStatusEntry`

**Files:**
- Create: `clipmeta.core/Watching/QueuedTag.cs`, `clipmeta.core/Watching/TagQueueData.cs`, `clipmeta.core/Watching/DrainReport.cs`, `clipmeta.core/Watching/QueueStatusEntry.cs`
- Test: (covered by Task 3's serialization test, these are plain records; no standalone test)

**Interfaces:**
- Consumes: `QueuedMutation` (Task 1).
- Produces: `QueuedTag(string ClipPath, QueuedMutation Mutation, DateTimeOffset EnqueuedAtUtc, string Confidence)`; `TagQueueData(int Version, IReadOnlyList<QueuedTag> Entries)`; `DrainReport(IReadOnlyList<string> Written, IReadOnlyList<string> StillQueued, IReadOnlyList<string> Dropped)`; `QueueStatusEntry(string ClipPath, IReadOnlyList<string> ChangedFields, double AgeSeconds, bool Locked)`.

- [ ] **Step 1: Write the records (no separate test, exercised by Task 3)**

```csharp
// clipmeta.core/Watching/QueuedTag.cs
namespace ClipMetaCore.Watching;

/// <summary>One deferred tag: a confirmed clip path and the mutation waiting to be written to it.</summary>
public sealed record QueuedTag(
    /// <summary>Full path to the target clip; the queue key (case-insensitive on Windows).</summary>
    string ClipPath,
    /// <summary>The durable field changes to apply when the file's lock clears.</summary>
    QueuedMutation Mutation,
    /// <summary>When the tag was enqueued (UTC).</summary>
    DateTimeOffset EnqueuedAtUtc,
    /// <summary>The resolution confidence recorded at enqueue time (record-keeping only).</summary>
    string Confidence);
```

```csharp
// clipmeta.core/Watching/TagQueueData.cs
namespace ClipMetaCore.Watching;

/// <summary>The full contents of a library's deferred-tag queue file.</summary>
public sealed record TagQueueData(
    /// <summary>Queue schema version (current: 1).</summary>
    int Version,
    /// <summary>All pending tags, in enqueue order.</summary>
    IReadOnlyList<QueuedTag> Entries);
```

```csharp
// clipmeta.core/Watching/DrainReport.cs
namespace ClipMetaCore.Watching;

/// <summary>The outcome of a single <see cref="TagQueue.Drain"/> pass.</summary>
public sealed record DrainReport(
    /// <summary>Clip paths whose tags were written this pass and removed from the queue.</summary>
    IReadOnlyList<string> Written,
    /// <summary>Clip paths still locked, left in the queue to retry next pass.</summary>
    IReadOnlyList<string> StillQueued,
    /// <summary>Clip paths that no longer exist; dropped from the queue without writing.</summary>
    IReadOnlyList<string> Dropped);
```

```csharp
// clipmeta.core/Watching/QueueStatusEntry.cs
namespace ClipMetaCore.Watching;

/// <summary>A read-only view of one pending queue entry for status reporting.</summary>
public sealed record QueueStatusEntry(
    /// <summary>Target clip path.</summary>
    string ClipPath,
    /// <summary>Names of the fields this entry will change (set/append/delete), for display.</summary>
    IReadOnlyList<string> ChangedFields,
    /// <summary>Seconds since the tag was enqueued.</summary>
    double AgeSeconds,
    /// <summary>Whether the clip is currently locked (cannot be written yet).</summary>
    bool Locked);
```

- [ ] **Step 2: Build to verify the records compile**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add clipmeta.core/Watching/QueuedTag.cs clipmeta.core/Watching/TagQueueData.cs clipmeta.core/Watching/DrainReport.cs clipmeta.core/Watching/QueueStatusEntry.cs
git commit -m "feat(core): deferred-tag queue record types"
```

---

### Task 3: `TagQueue` load / save (atomic, corruption-tolerant)

**Files:**
- Create: `clipmeta.core/Watching/TagQueue.cs`
- Test: `clipmetascribe.Tests/TagQueueTests.cs`

**Interfaces:**
- Consumes: `TagQueueData`, `QueuedTag`, `QueuedMutation` (Tasks 1–2); `Mp4Writer.RetryOnTransientLock` (existing, internal to `clipmeta.core`).
- Produces:
  - `const string TagQueue.QueueFileName = ".clipmeta-queue"`
  - `static string TagQueue.QueuePath(string libraryDir)`
  - `static TagQueueData TagQueue.Load(string libraryDir)`, empty queue on missing/corrupt, never throws.
  - `static void TagQueue.Save(TagQueueData data, string libraryDir)`, atomic temp-swap.

- [ ] **Step 1: Write the failing test**

```csharp
// clipmetascribe.Tests/TagQueueTests.cs
using ClipMetaCore.Watching;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Tests;

[TestClass]
public class TagQueueTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cmqueue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static QueuedTag Tag(string path, string field, string value)
    {
        var m = new MetadataMutation();
        m.AppendFields[field] = value;
        return new QueuedTag(path, QueuedMutation.From(m), DateTimeOffset.UtcNow, "high");
    }

    [TestMethod]
    public void Load_MissingFile_ReturnsEmptyQueue_NeverThrows()
    {
        TagQueueData data = TagQueue.Load(_dir);
        Assert.AreEqual(0, data.Entries.Count);
    }

    [TestMethod]
    public void Save_ThenLoad_RoundTrips()
    {
        var data = new TagQueueData(1, new[] { Tag(Path.Combine(_dir, "a.mp4"), "tags", "headshot") });
        TagQueue.Save(data, _dir);

        TagQueueData reloaded = TagQueue.Load(_dir);

        Assert.AreEqual(1, reloaded.Entries.Count);
        Assert.AreEqual(Path.Combine(_dir, "a.mp4"), reloaded.Entries[0].ClipPath);
        Assert.AreEqual("headshot", reloaded.Entries[0].Mutation.AppendFields["tags"]);
        Assert.AreEqual("high", reloaded.Entries[0].Confidence);
    }

    [TestMethod]
    public void Load_CorruptFile_ReturnsEmptyQueue_NeverThrows()
    {
        File.WriteAllText(Path.Combine(_dir, TagQueue.QueueFileName), "{ this is not valid json ]");
        TagQueueData data = TagQueue.Load(_dir);
        Assert.AreEqual(0, data.Entries.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter TagQueueTests`
Expected: FAIL, `TagQueue` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation (load/save only)**

```csharp
// clipmeta.core/Watching/TagQueue.cs
using System.Text.Json;
using ClipMetaCore.Write;

namespace ClipMetaCore.Watching;

/// <summary>
/// Durable deferred-tag queue stored in a library root. A clip that is playing is locked against
/// our write (<see cref="System.IO.File.Replace(string, string, string?)"/> needs a delete-share
/// the player does not grant), so spoken tags are persisted here and written as the locks clear.
/// The queue stores only confirmed, already-resolved paths, it never resolves or guesses.
/// </summary>
public static class TagQueue
{
    /// <summary>File name written in the library root (sibling of <c>.clipmeta-index</c>).</summary>
    public const string QueueFileName = ".clipmeta-queue";

    /// <summary>Current on-disk schema version.</summary>
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Full path to the queue file inside <paramref name="libraryDir"/>.</summary>
    public static string QueuePath(string libraryDir) => Path.Combine(libraryDir, QueueFileName);

    /// <summary>
    /// Reads the queue. A missing OR unreadable/corrupt file yields an empty queue and never
    /// throws, the queue is opportunistic state on a watched-clip call, never a hard dependency.
    /// </summary>
    public static TagQueueData Load(string libraryDir)
    {
        string path = QueuePath(libraryDir);
        try
        {
            if (!File.Exists(path))
                return new TagQueueData(CurrentVersion, Array.Empty<QueuedTag>());
            string json = File.ReadAllText(path);
            TagQueueData? data = JsonSerializer.Deserialize<TagQueueData>(json, JsonOptions);
            return data?.Entries is null
                ? new TagQueueData(CurrentVersion, Array.Empty<QueuedTag>())
                : data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TagQueueData(CurrentVersion, Array.Empty<QueuedTag>());
        }
    }

    /// <summary>
    /// Writes the queue atomically: serialize to a sibling temp file, then swap it into place with
    /// a retry on a transient AV/indexer lock. Mirrors <c>ClipMetaIndex.WriteToFile</c>, a crash
    /// mid-write leaves the previous queue intact, never a half-written file.
    /// </summary>
    public static void Save(TagQueueData data, string libraryDir)
    {
        ArgumentNullException.ThrowIfNull(data);
        string path = QueuePath(libraryDir);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(data, JsonOptions));
            Mp4Writer.RetryOnTransientLock(
                () => File.Move(tempPath, path, overwrite: true),
                maxAttempts: 5, baseDelayMs: 100);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }
}
```

> **Note on deserialization:** `System.Text.Json` populates the get-only collection properties of `QueuedMutation` (it has a positional constructor, so STJ binds the records by constructor parameter name, `SetFields`, `AppendFields`, `DeleteFields`, `ClearAll`, etc., which is why the property names must match the JSON exactly; they do, since we serialize with the same type).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build --nologo -v q && dotnet test clipmetascribe.Tests --nologo --no-build -v q --filter TagQueueTests`
Expected: PASS (3/3).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/TagQueue.cs clipmetascribe.Tests/TagQueueTests.cs
git commit -m "feat(core): TagQueue atomic, corruption-tolerant load/save"
```

---

### Task 4: `TagQueue.Enqueue` with merge semantics

**Files:**
- Modify: `clipmeta.core/Watching/TagQueue.cs`
- Test: `clipmetascribe.Tests/TagQueueTests.cs` (add cases)

**Interfaces:**
- Produces: `static void TagQueue.Enqueue(string libraryDir, string clipPath, MetadataMutation mutation, string confidence)`, merges onto an existing entry for the same path (set last-wins, append accumulates, delete unions, ClearAll ORs); one entry per clip; persists via `Save`.

- [ ] **Step 1: Write the failing test (add to `TagQueueTests`)**

```csharp
    [TestMethod]
    public void Enqueue_NewClip_AddsOneEntry()
    {
        string clip = Path.Combine(_dir, "a.mp4");
        var m = new MetadataMutation(); m.AppendFields["tags"] = "headshot";
        TagQueue.Enqueue(_dir, clip, m, "high");

        TagQueueData data = TagQueue.Load(_dir);
        Assert.AreEqual(1, data.Entries.Count);
        Assert.AreEqual("headshot", data.Entries[0].Mutation.AppendFields["tags"]);
    }

    [TestMethod]
    public void Enqueue_SameClipTwice_MergesIntoOneEntry()
    {
        string clip = Path.Combine(_dir, "a.mp4");
        var m1 = new MetadataMutation(); m1.AppendFields["tags"] = "headshot";
        var m2 = new MetadataMutation(); m2.AppendFields["tags"] = "airshot"; m2.SetFields["game"] = "TF2";
        TagQueue.Enqueue(_dir, clip, m1, "high");
        TagQueue.Enqueue(_dir, clip, m2, "high");

        TagQueueData data = TagQueue.Load(_dir);
        Assert.AreEqual(1, data.Entries.Count, "same clip must merge, not duplicate");
        // append accumulated both values (pipe-joined), set captured the new field
        StringAssert.Contains(data.Entries[0].Mutation.AppendFields["tags"], "headshot");
        StringAssert.Contains(data.Entries[0].Mutation.AppendFields["tags"], "airshot");
        Assert.AreEqual("TF2", data.Entries[0].Mutation.SetFields["game"]);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter TagQueueTests`
Expected: FAIL, `Enqueue` not defined.

- [ ] **Step 3: Add the implementation to `TagQueue.cs`**

```csharp
    /// <summary>Case-insensitive path comparison matches Windows filesystem semantics.</summary>
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Enqueues a confirmed tag for <paramref name="clipPath"/>. If a tag for that path is already
    /// pending, the new mutation MERGES onto it (set last-wins, append accumulates and pipe-dedups,
    /// delete unions, ClearAll ORs) so a clip never has two competing queue entries. Persists.
    /// </summary>
    public static void Enqueue(string libraryDir, string clipPath, MetadataMutation mutation, string confidence)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        TagQueueData data = Load(libraryDir);
        var entries = data.Entries.ToList();

        int existing = entries.FindIndex(e => PathComparer.Equals(e.ClipPath, clipPath));
        QueuedMutation merged = existing >= 0
            ? Merge(entries[existing].Mutation, QueuedMutation.From(mutation))
            : QueuedMutation.From(mutation);

        var entry = new QueuedTag(clipPath, merged, DateTimeOffset.UtcNow, confidence);
        if (existing >= 0) entries[existing] = entry;
        else entries.Add(entry);

        Save(new TagQueueData(CurrentVersion, entries), libraryDir);
    }

    /// <summary>Layers <paramref name="next"/> onto <paramref name="prior"/> using the field rules.</summary>
    private static QueuedMutation Merge(QueuedMutation prior, QueuedMutation next)
    {
        var set = new Dictionary<string, string?>(prior.SetFields);
        foreach (var (k, v) in next.SetFields) set[k] = v;                       // last-wins

        var append = new Dictionary<string, string>(prior.AppendFields);
        foreach (var (k, v) in next.AppendFields)
            append[k] = append.TryGetValue(k, out string? existingVal) && existingVal.Length > 0
                ? PipeMerge(existingVal, v)                                       // accumulate + dedup
                : v;

        var delete = new HashSet<string>(prior.DeleteFields);
        foreach (string d in next.DeleteFields) delete.Add(d);                    // union

        return new QueuedMutation(set, append, delete.ToList(), prior.ClearAll || next.ClearAll);
    }

    /// <summary>Joins two pipe-delimited lists, dropping duplicate items (first occurrence wins).</summary>
    private static string PipeMerge(string a, string b)
    {
        var seen = new List<string>();
        foreach (string item in (a + "|" + b).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!seen.Contains(item, StringComparer.OrdinalIgnoreCase))
                seen.Add(item);
        return string.Join('|', seen);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build --nologo -v q && dotnet test clipmetascribe.Tests --nologo --no-build -v q --filter TagQueueTests`
Expected: PASS (5/5).

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/TagQueue.cs clipmetascribe.Tests/TagQueueTests.cs
git commit -m "feat(core): TagQueue.Enqueue with per-clip merge semantics"
```

---

### Task 5: `TagQueue.Drain` and `TagQueue.Status`

**Files:**
- Modify: `clipmeta.core/Watching/TagQueue.cs`
- Test: `clipmetascribe.Tests/TagQueueTests.cs` (add cases, these use the real `Mp4Writer` against a real small clip)

**Interfaces:**
- Consumes: `IMediaWriter` (`Mp4Writer` implements it, `WriteMetadata(string, MetadataMutation, IClipMetaLogger)`); `IClipMetaLogger` (`NullLogger.Instance` from `ClipMetaCore.Logging`); a lock predicate.
- Produces:
  - `static DrainReport TagQueue.Drain(string libraryDir, IMediaWriter writer, IClipMetaLogger logger, Func<string,bool> isInUse)`
  - `static IReadOnlyList<QueueStatusEntry> TagQueue.Status(string libraryDir, Func<string,bool> isInUse)`

**Design notes for the implementer:**
- `isInUse` is injected (not a hard call to `LockProbe`) so tests drive lock state deterministically; production callers pass `LockProbe.IsInUse`.
- A drained entry whose clip **no longer exists** → drop into `Dropped` (do not call the writer).
- An entry that is **in use** → leave queued (`StillQueued`).
- Otherwise → `writer.WriteMetadata(...)`; on success drop from the queue (`Written`). A write failure (`IOException`/`InvalidDataException`/etc.) leaves the entry queued under `StillQueued` (retry next pass), never crash the drain.
- Persist the surviving queue once at the end.

- [ ] **Step 1: Write the failing test (add to `TagQueueTests`)**

```csharp
    // A tiny valid .mp4 the write engine accepts. The scribe test project already has a helper
    // for this; reuse whatever the existing write tests use to get a pristine scratch clip.
    // Here we assume TestClips.CopyPristineToScratch returns a writable .mp4 path.
    private string ScratchClip() => TestClips.CopyPristineToScratch(_dir, "a.mp4");

    [TestMethod]
    public void Drain_UnlockedClip_WritesAndRemoves()
    {
        string clip = ScratchClip();
        var m = new MetadataMutation(); m.AppendFields["tags"] = "headshot";
        TagQueue.Enqueue(_dir, clip, m, "high");

        DrainReport report = TagQueue.Drain(
            _dir, new Mp4Writer(), NullLogger.Instance, isInUse: _ => false);

        CollectionAssert.AreEqual(new[] { clip }, report.Written.ToList());
        Assert.AreEqual(0, TagQueue.Load(_dir).Entries.Count, "written entry removed from queue");
        // The tag actually landed in the file:
        var fields = ClipMetaReader.GetUserFields(Mp4Parser.ParseFile(clip));
        Assert.IsTrue(fields.Any(f => f.Field == "tags" && f.Value.Contains("headshot")));
    }

    [TestMethod]
    public void Drain_LockedClip_LeavesQueued()
    {
        string clip = ScratchClip();
        var m = new MetadataMutation(); m.AppendFields["tags"] = "headshot";
        TagQueue.Enqueue(_dir, clip, m, "high");

        DrainReport report = TagQueue.Drain(
            _dir, new Mp4Writer(), NullLogger.Instance, isInUse: _ => true);

        CollectionAssert.AreEqual(new[] { clip }, report.StillQueued.ToList());
        Assert.AreEqual(1, TagQueue.Load(_dir).Entries.Count, "locked entry stays queued");
    }

    [TestMethod]
    public void Drain_VanishedClip_DroppedNoCrash()
    {
        string clip = Path.Combine(_dir, "gone.mp4"); // never created
        var m = new MetadataMutation(); m.AppendFields["tags"] = "headshot";
        TagQueue.Enqueue(_dir, clip, m, "high");

        DrainReport report = TagQueue.Drain(
            _dir, new Mp4Writer(), NullLogger.Instance, isInUse: _ => false);

        CollectionAssert.AreEqual(new[] { clip }, report.Dropped.ToList());
        Assert.AreEqual(0, TagQueue.Load(_dir).Entries.Count, "vanished entry dropped");
    }

    [TestMethod]
    public void Status_ReportsPendingEntries()
    {
        string clip = Path.Combine(_dir, "a.mp4");
        var m = new MetadataMutation(); m.AppendFields["tags"] = "headshot"; m.SetFields["game"] = "TF2";
        TagQueue.Enqueue(_dir, clip, m, "high");

        IReadOnlyList<QueueStatusEntry> status = TagQueue.Status(_dir, isInUse: _ => true);

        Assert.AreEqual(1, status.Count);
        Assert.AreEqual(clip, status[0].ClipPath);
        Assert.IsTrue(status[0].Locked);
        CollectionAssert.AreEquivalent(new[] { "tags", "game" }, status[0].ChangedFields.ToList());
    }
```

> Add `using ClipMetaCore.Logging;`, `using ClipMetaCore.Mp4;`, `using ClipMetaCore.Read;` to the test file. If `TestClips.CopyPristineToScratch` is not the exact existing helper name, use the project's established pristine-copy helper (see other tests in `clipmetascribe.Tests` that write to real clips); the test **must** skip gracefully when no pristine corpus is present, matching the project's clip-less CI convention.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter TagQueueTests`
Expected: FAIL, `Drain`/`Status` not defined.

- [ ] **Step 3: Add the implementation to `TagQueue.cs`**

```csharp
    /// <summary>
    /// Attempts to write every queued tag whose clip is not currently locked, through the supplied
    /// write engine. Single-pass and single-threaded (the caller serializes drains with the write
    /// tools' single-flight gate). Vanished clips are dropped; locked clips and write failures stay
    /// queued for the next pass. The surviving queue is persisted once at the end.
    /// </summary>
    /// <param name="libraryDir">Library root holding the queue file.</param>
    /// <param name="writer">Write engine (production: <c>new Mp4Writer()</c>).</param>
    /// <param name="logger">Logger passed to the write engine.</param>
    /// <param name="isInUse">Lock predicate (production: <c>LockProbe.IsInUse</c>).</param>
    public static DrainReport Drain(
        string libraryDir, IMediaWriter writer, IClipMetaLogger logger, Func<string, bool> isInUse)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(isInUse);

        TagQueueData data = Load(libraryDir);
        var written = new List<string>();
        var stillQueued = new List<string>();
        var dropped = new List<string>();
        var survivors = new List<QueuedTag>();

        foreach (QueuedTag entry in data.Entries)
        {
            if (!File.Exists(entry.ClipPath)) { dropped.Add(entry.ClipPath); continue; }
            if (isInUse(entry.ClipPath)) { stillQueued.Add(entry.ClipPath); survivors.Add(entry); continue; }
            try
            {
                writer.WriteMetadata(entry.ClipPath, entry.Mutation.ToMutation(), logger);
                written.Add(entry.ClipPath);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidDataException
                   or InvalidOperationException or ArgumentException)
            {
                // The write could not land this pass (still-held handle that beat the probe,
                // verification failure, a bad value). Keep it queued rather than losing the tag.
                stillQueued.Add(entry.ClipPath);
                survivors.Add(entry);
            }
        }

        if (survivors.Count != data.Entries.Count)
            Save(new TagQueueData(CurrentVersion, survivors), libraryDir);

        return new DrainReport(written, stillQueued, dropped);
    }

    /// <summary>Returns a read-only view of every pending entry, with its current lock state.</summary>
    public static IReadOnlyList<QueueStatusEntry> Status(string libraryDir, Func<string, bool> isInUse)
    {
        ArgumentNullException.ThrowIfNull(isInUse);
        TagQueueData data = Load(libraryDir);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rows = new List<QueueStatusEntry>(data.Entries.Count);
        foreach (QueuedTag e in data.Entries)
        {
            var changed = new List<string>();
            changed.AddRange(e.Mutation.SetFields.Keys);
            changed.AddRange(e.Mutation.AppendFields.Keys);
            changed.AddRange(e.Mutation.DeleteFields);
            rows.Add(new QueueStatusEntry(
                e.ClipPath, changed, (now - e.EnqueuedAtUtc).TotalSeconds, isInUse(e.ClipPath)));
        }
        return rows;
    }
```

> Add `using ClipMetaCore.Abstractions;` (for `IMediaWriter`/`IClipMetaLogger`) to `TagQueue.cs`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build --nologo -v q && dotnet test clipmetascribe.Tests --nologo --no-build -v q --filter TagQueueTests`
Expected: PASS (9/9). (Drain tests graceful-skip if no pristine corpus.)

- [ ] **Step 5: Commit**

```bash
git add clipmeta.core/Watching/TagQueue.cs clipmetascribe.Tests/TagQueueTests.cs
git commit -m "feat(core): TagQueue.Drain and Status (lock-aware, injectable probe)"
```

---

### Task 6: Extract the shared MCP `WriteGate`

**Files:**
- Create: `clipmetamcp/Tools/WriteGate.cs`
- Modify: `clipmetamcp/Tools/WriteTools.cs` (replace private semaphore usage)
- Test: existing `clipmetamcp.Tests/WriteToolsTests.cs` must still pass (no behavior change).

**Interfaces:**
- Produces: `internal static class WriteGate` with `static void Enter()` / `static void Exit()` wrapping a process-wide `SemaphoreSlim(1,1)`.

This is a pure refactor so the drain (Task 8) can share the write tools' single-flight discipline, a drain must not race a direct `clip_set_fields` at `File.Replace`.

- [ ] **Step 1: Create the shared gate**

```csharp
// clipmetamcp/Tools/WriteGate.cs
namespace ClipMetaMcp.Tools;

/// <summary>
/// Process-wide single-flight latch for every operation that mutates a clip file, the direct
/// write tools AND the deferred-queue drain. Two concurrent rewrites of the same file would race
/// at <c>File.Replace</c>; serializing all writes here retires that race permanently (spec risk
/// R2/R8). The session loop is single-threaded today, so this is insurance against a future
/// pipelined host, at negligible cost.
/// </summary>
internal static class WriteGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Acquires the write latch, blocking until it is free.</summary>
    public static void Enter() => Gate.Wait();

    /// <summary>Releases the write latch.</summary>
    public static void Exit() => Gate.Release();
}
```

- [ ] **Step 2: Replace the private semaphore in `WriteTools.cs`**

Delete the field:
```csharp
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
```
Replace each `WriteGate.Wait();` with `WriteGate.Enter();` and each `WriteGate.Release();` with `WriteGate.Exit();` (3 call sites: `ExecuteWrite`, `RestoreBackup`, `PruneBackups`). The type name `WriteGate` now refers to the new shared class.

- [ ] **Step 3: Build + run the write tests to verify no behavior change**

Run: `dotnet build --nologo -v q && dotnet test clipmetamcp.Tests --nologo --no-build -v q`
Expected: PASS (all existing MCP tests, including `WriteToolsTests`).

- [ ] **Step 4: Commit**

```bash
git add clipmetamcp/Tools/WriteGate.cs clipmetamcp/Tools/WriteTools.cs
git commit -m "refactor(mcp): extract shared WriteGate single-flight latch"
```

---

### Task 7: `FlushQueueCommand` + CLI `--flush-queue` and `--watching` footer

**Files:**
- Create: `clipmetascribe/Commands/FlushQueueCommand.cs`
- Modify: `clipmetascribe/Program.cs` (route `--flush-queue`; add to known flags + help; footer on `--watching`)
- Modify: `clipmetascribe/Commands/WatchingCommand.cs` (append pending-queue footer)
- Test: `clipmetascribe.Tests/TagQueueTests.cs` or a new `FlushQueueCommandTests.cs` (command-level)

**Interfaces:**
- Consumes: `TagQueue.Drain`, `TagQueue.Status`, `LockProbe.IsInUse`, `Mp4Writer`, `NullLogger.Instance`.
- Produces: `static int FlushQueueCommand.Run(string libraryDir, TextWriter? output = null, IMediaWriter? writer = null, Func<string,bool>? isInUse = null)` (trailing injectables default to real impls, the "testable surface" convention).

- [ ] **Step 1: Write the failing test**

```csharp
// clipmetascribe.Tests/FlushQueueCommandTests.cs
using ClipMetaCore.Watching;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;

namespace ClipMetaScribe.Tests;

[TestClass]
public class FlushQueueCommandTests
{
    private string _dir = null!;
    [TestInitialize] public void Init()
    { _dir = Path.Combine(Path.GetTempPath(), "cmflush-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }
    [TestCleanup] public void Cleanup()
    { try { Directory.Delete(_dir, true); } catch { } }

    [TestMethod]
    public void Run_EmptyQueue_ReportsNothingPending()
    {
        var sw = new StringWriter();
        int code = FlushQueueCommand.Run(_dir, sw, new Mp4Writer(), _ => false);
        Assert.AreEqual(0, code);
        StringAssert.Contains(sw.ToString(), "no tags queued");
    }

    [TestMethod]
    public void Run_LockedEntry_ReportsStillQueued()
    {
        var m = new MetadataMutation(); m.AppendFields["tags"] = "headshot";
        TagQueue.Enqueue(_dir, Path.Combine(_dir, "a.mp4"), m, "high");
        var sw = new StringWriter();
        int code = FlushQueueCommand.Run(_dir, sw, new Mp4Writer(), _ => true);
        Assert.AreEqual(0, code);
        StringAssert.Contains(sw.ToString().ToLowerInvariant(), "still");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test clipmetascribe.Tests --nologo -v q --filter FlushQueueCommandTests`
Expected: FAIL, `FlushQueueCommand` not defined.

- [ ] **Step 3: Implement `FlushQueueCommand`**

```csharp
// clipmetascribe/Commands/FlushQueueCommand.cs
using ClipMetaCore.Abstractions;
using ClipMetaCore.Logging;
using ClipMetaCore.Watching;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Commands;

/// <summary>
/// Drains the deferred-tag queue for a library, writing every queued tag whose clip is no longer
/// locked. Used for the final clip of a session, after the player has closed and there is no
/// "next" watched-clip call to pump the drain.
/// </summary>
internal static class FlushQueueCommand
{
    /// <summary>Drains the queue under <paramref name="libraryDir"/> and prints the outcome.</summary>
    /// <param name="libraryDir">Library root holding the queue.</param>
    /// <param name="output">Writer to print to; defaults to <see cref="Console.Out"/>.</param>
    /// <param name="writer">Write engine; defaults to a real <see cref="Mp4Writer"/>.</param>
    /// <param name="isInUse">Lock predicate; defaults to <see cref="LockProbe.IsInUse"/>.</param>
    /// <returns>Exit code 0.</returns>
    internal static int Run(string libraryDir, TextWriter? output = null,
                            IMediaWriter? writer = null, Func<string, bool>? isInUse = null)
    {
        output ??= Console.Out;
        writer ??= new Mp4Writer();
        isInUse ??= LockProbe.IsInUse;

        DrainReport report = TagQueue.Drain(libraryDir, writer, NullLogger.Instance, isInUse);

        if (report.Written.Count == 0 && report.StillQueued.Count == 0 && report.Dropped.Count == 0)
        {
            output.WriteLine("There are no tags queued for this library.");
            return 0;
        }

        foreach (string w in report.Written) output.WriteLine($"  wrote   {w}");
        foreach (string s in report.StillQueued) output.WriteLine($"  still locked (will retry)  {s}");
        foreach (string d in report.Dropped) output.WriteLine($"  dropped (file gone)  {d}");
        output.WriteLine(
            $"Flushed: {report.Written.Count} written, {report.StillQueued.Count} still queued, " +
            $"{report.Dropped.Count} dropped.");
        return 0;
    }
}
```

- [ ] **Step 4: Route `--flush-queue` in `Program.cs`**

Add this branch next to the `--watching` branch (after line ~172):

```csharp
        if (ContainsFlag(args, "--flush-queue"))
        {
            if (filePath == null || !Directory.Exists(filePath))
            {
                Console.Error.WriteLine("Error: --flush-queue requires a valid clips directory as the first argument.");
                return 1;
            }
            try
            {
                return FlushQueueCommand.Run(filePath);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }
```

Add `"--flush-queue"` to the known-flags array (line ~361) so it is recognized, and add a help line near the `--watching` help (after line ~618):

```csharp
              --flush-queue           Write queued deferred tags whose clips are no longer in use
                                      (use with a clips directory).
```

And in the `--watching` branch, after the resolver runs, print the pending footer, implemented inside `WatchingCommand.Run` in Step 5 so the MCP and CLI stay parallel.

- [ ] **Step 5: Add the pending footer to `WatchingCommand.Run`**

At the end of `WatchingCommand.Run` (before `return 0;`), add:

```csharp
        int pending = ClipMetaCore.Watching.TagQueue.Status(libraryDir, LockProbe.IsInUse).Count;
        if (pending > 0)
            output.WriteLine($"\nQueued tags pending: {pending} (run --flush-queue after closing the player).");
```

- [ ] **Step 6: Run tests + build**

Run: `dotnet build --nologo -v q && dotnet test clipmetascribe.Tests --nologo --no-build -v q --filter FlushQueueCommandTests`
Expected: PASS (2/2). Then a full `clipmetascribe.Tests` run later (Task 9).

- [ ] **Step 7: Commit**

```bash
git add clipmetascribe/Commands/FlushQueueCommand.cs clipmetascribe/Commands/WatchingCommand.cs clipmetascribe/Program.cs clipmetascribe.Tests/FlushQueueCommandTests.cs
git commit -m "feat(scribe): --flush-queue command and --watching pending footer"
```

---

### Task 8: MCP queue tools (`library_queue_tag` / `library_flush_queue` / `library_queue_status`)

**Files:**
- Create: `clipmetamcp/Tools/QueueTools.cs`
- Modify: the registration call site (where `ReadTools.RegisterAll` / `WriteTools.RegisterAll` are invoked, `clipmetamcp/Program.cs` or a session builder) to call `QueueTools.RegisterAll`.
- Modify: `clipmetamcp.Tests/Phase2ReadToolsTests.cs` (extend the surface assertion)
- Test: `clipmetamcp.Tests/QueueToolsTests.cs`

**Interfaces:**
- Consumes: `ToolRegistry`, `LibrarySandbox` (`ResolveWritePath`, `RequireRoot`), `TagQueue`, `WriteGate` (Task 6), `LockProbe`, `Mp4Writer`, `NullLogger`. Mutation-from-args mirrors `WriteTools.SetFields`/`AppendField` (uses `ClipMetaSchema.AtomName`).
- Produces: `static void QueueTools.RegisterAll(ToolRegistry registry, LibrarySandbox sandbox)`.

**Behavior:**
- `library_queue_tag(path, fields)`: resolve `path` via `sandbox.ResolveWritePath` (must exist, in-library, .mp4, this is the "dumb queue" library-sandbox check). Build a `MetadataMutation` from `fields` exactly like `clip_set_fields` (empty string = delete). **Drain first** (under `WriteGate`), then `TagQueue.Enqueue`. Return the enqueue confirmation + what the opportunistic drain landed.
- `library_flush_queue()`: `RequireRoot`, drain under `WriteGate`, return the `DrainReport`.
- `library_queue_status()`: `RequireRoot`, return `TagQueue.Status`.
- The drain helper is shared by `queue_tag` and `flush_queue`.

- [ ] **Step 1: Write the failing test**

```csharp
// clipmetamcp.Tests/QueueToolsTests.cs
using System.Text.Json.Nodes;
using ClipMetaCore.Watching;

namespace ClipMetaMcp.Tests;

[TestClass]
public class QueueToolsTests
{
    // Uses the same McpHarness pattern as the other tool tests; _lib is a temp library dir
    // with a small writable .mp4 created in TestInitialize (mirror LibraryWatchingToolTests setup).

    [TestMethod]
    public void QueueTag_LockedClip_PersistsToQueue()
    {
        // Arrange: a clip the harness reports as "in use" (hold an exclusive-denying handle on it).
        // Act: call library_queue_tag with tags=headshot.
        // Assert: the response indicates the tag was queued (not yet written), and the
        // .clipmeta-queue file now has one entry.
    }

    [TestMethod]
    public void QueueStatus_ReflectsPendingEntries()
    {
        // After a queue_tag against a locked clip, library_queue_status lists one pending entry.
    }

    [TestMethod]
    public void FlushQueue_NoLibrary_RefusesCleanly()
    {
        // With no CLIPMETA_LIBRARY_ROOT, library_flush_queue returns isError with a model-readable
        // message naming the env var (RequireRoot path).
    }
}
```

> Fill these in against the actual `McpHarness` API used by `LibraryWatchingToolTests.cs` and `WriteToolsTests.cs`, match how they create `_lib`, hold a file open to simulate `inUse`, and assert `isError`/structured content. Do NOT invent a new harness.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test clipmetamcp.Tests --nologo -v q --filter QueueToolsTests`
Expected: FAIL, `QueueTools` not defined.

- [ ] **Step 3: Implement `QueueTools`**

```csharp
// clipmetamcp/Tools/QueueTools.cs
using System.Text.Json.Nodes;
using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Watching;
using ClipMetaCore.Write;

namespace ClipMetaMcp.Tools;

/// <summary>
/// Registers the deferred-tag queue tools. A clip that is playing is locked against our write, so
/// these persist a CONFIRMED tag and drain the queue as locks clear. The queue never resolves or
/// guesses, the caller passes an already-resolved path (from library_watching, confirmed with the
/// user when confidence was low). Every drain runs under the shared <see cref="WriteGate"/> so it
/// can never race a direct write at <c>File.Replace</c>.
/// </summary>
public static class QueueTools
{
    /// <summary>Registers the queue tools against the given sandbox.</summary>
    public static void RegisterAll(ToolRegistry registry, LibrarySandbox sandbox)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sandbox);

        registry.Register(new ToolDefinition(
            "library_queue_tag",
            "Queues a metadata tag for a clip that is currently being played (and therefore locked " +
            "against writing). Pass the clip 'path' you already resolved with library_watching and " +
            "confirmed, this tool does NOT resolve or guess. 'fields' maps field names to string " +
            "values (empty string deletes), exactly like clip_set_fields. The tag is written " +
            "automatically the next time you call a watched-clip tool after the player advances " +
            "(the lock clears), or immediately via library_flush_queue. Requires a configured library.",
            QueueTagSchema(),
            args => QueueTag(args, sandbox),
            clipPath => new JsonObject
            {
                ["path"] = clipPath,
                ["fields"] = new JsonObject { ["tags"] = "headshot" },
            }));

        registry.Register(new ToolDefinition(
            "library_flush_queue",
            "Writes every queued deferred tag whose clip is no longer locked, use after you stop " +
            "and close the player on the LAST clip, when there is no next watched-clip call to drain " +
            "the queue. Returns what was written, what is still locked (will retry), and what was " +
            "dropped because the clip is gone. Requires a configured library.",
            NoArgsSchema(),
            args => FlushQueue(args, sandbox),
            _ => new JsonObject()));

        registry.Register(new ToolDefinition(
            "library_queue_status",
            "Lists the deferred tags waiting to be written: the clip, which fields will change, how " +
            "long it has waited, and whether it is still locked. Read-only. Requires a configured library.",
            NoArgsSchema(),
            args => QueueStatus(args, sandbox),
            _ => new JsonObject()));
    }

    private static JsonObject QueueTagSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Path to the .mp4 you already resolved and confirmed. " +
                                  "Absolute, or relative to the library root.",
            },
            ["fields"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Field name → string value. Empty string deletes the field.",
                ["additionalProperties"] = new JsonObject { ["type"] = "string" },
            },
        },
        ["required"] = new JsonArray("path", "fields"),
    };

    private static JsonObject NoArgsSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    // ── Handlers ─────────────────────────────────────────────────────────────────────────

    private static JsonObject QueueTag(JsonObject? args, LibrarySandbox sandbox)
    {
        // Library-sandbox check IS the "dumb queue" guard: the path must be a real .mp4 in-library.
        string fullPath = sandbox.ResolveWritePath(ReadTools.GetRequiredString(args, "path"));

        if (args?["fields"] is not JsonObject fieldArgs || fieldArgs.Count == 0)
            throw new ToolException(
                "The 'fields' argument is required: an object mapping field names to string values, " +
                "e.g. { \"tags\": \"headshot\" }.");

        var mutation = new MetadataMutation();
        foreach (var pair in fieldArgs)
        {
            if (pair.Value is not JsonValue value || !value.TryGetValue(out string? text))
                throw new ToolException($"Field '{pair.Key}' must have a string value (use \"\" to delete it).");
            mutation.SetFields[ClipMetaSchema.AtomName(pair.Key)] = text;
        }

        string root = sandbox.RequireRoot();
        DrainReport drain = DrainUnderGate(root);   // opportunistic: land anything already freed
        TagQueue.Enqueue(root, fullPath, mutation, confidence: "high");

        return new JsonObject
        {
            ["queued"] = fullPath,
            ["pending"] = TagQueue.Status(root, LockProbe.IsInUse).Count,
            ["drained"] = DrainJson(drain),
        };
    }

    private static JsonObject FlushQueue(JsonObject? args, LibrarySandbox sandbox)
    {
        string root = sandbox.RequireRoot();
        DrainReport drain = DrainUnderGate(root);
        return DrainJson(drain);
    }

    private static JsonObject QueueStatus(JsonObject? args, LibrarySandbox sandbox)
    {
        string root = sandbox.RequireRoot();
        var entries = new JsonArray();
        foreach (QueueStatusEntry e in TagQueue.Status(root, LockProbe.IsInUse))
        {
            var fields = new JsonArray();
            foreach (string f in e.ChangedFields) fields.Add(f);
            entries.Add(new JsonObject
            {
                ["path"] = e.ClipPath,
                ["changedFields"] = fields,
                ["ageSeconds"] = Math.Round(e.AgeSeconds, 1),
                ["locked"] = e.Locked,
            });
        }
        return new JsonObject { ["pending"] = entries.Count, ["entries"] = entries };
    }

    /// <summary>Drains the queue under the shared write single-flight, with the real probe/engine.</summary>
    private static DrainReport DrainUnderGate(string root)
    {
        WriteGate.Enter();
        try
        {
            return TagQueue.Drain(root, new Mp4Writer(), NullLogger.Instance, LockProbe.IsInUse);
        }
        finally
        {
            WriteGate.Exit();
        }
    }

    private static JsonObject DrainJson(DrainReport drain) => new()
    {
        ["written"] = ToArray(drain.Written),
        ["stillQueued"] = ToArray(drain.StillQueued),
        ["dropped"] = ToArray(drain.Dropped),
    };

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string v in values) array.Add(v);
        return array;
    }
}
```

- [ ] **Step 4: Register the tools at the call site**

Find where `ReadTools.RegisterAll(registry, sandbox)` and `WriteTools.RegisterAll(registry, sandbox)` are called (grep `RegisterAll` in `clipmetamcp/`). Add **after** them:

```csharp
        QueueTools.RegisterAll(registry, sandbox);
```

(Registration order = tools/list order; queue tools come last, after the backup tools.)

- [ ] **Step 5: Extend the tool-surface assertion**

In `clipmetamcp.Tests/Phase2ReadToolsTests.cs`, update `ToolsList_ContainsTheFullToolSurface`'s expected array to append the three new names at the end:

```csharp
                "library_list_backups", "clip_restore_backup", "clip_prune_backups",
                "library_queue_tag", "library_flush_queue", "library_queue_status",
```

- [ ] **Step 6: Run the FULL MCP test project (surface + behavior)**

Run: `dotnet build --nologo -v q && dotnet test clipmetamcp.Tests --nologo --no-build -v q`
Expected: PASS, including the updated `ToolsList_ContainsTheFullToolSurface`, the stdout-purity harness (which now drives the 3 new tools via their `ExampleArguments`), and `QueueToolsTests`.

> If the stdout-purity test fails because `library_queue_tag`'s example path isn't writable, ensure the harness's example-clip setup creates a real `.mp4` (it already does for the write tools, the same `ExampleArguments(clipPath)` contract applies).

- [ ] **Step 7: Commit**

```bash
git add clipmetamcp/Tools/QueueTools.cs clipmetamcp/Program.cs clipmetamcp.Tests/QueueToolsTests.cs clipmetamcp.Tests/Phase2ReadToolsTests.cs
git commit -m "feat(mcp): library_queue_tag/flush_queue/queue_status with opportunistic drain"
```

---

### Task 9: Wire opportunistic drain into `library_watching` + full verification + docs

**Files:**
- Modify: `clipmetamcp/Tools/ReadTools.cs` (`Watching` handler, drain before resolving)
- Modify: `docs/PITFALLS.md`, `MEMORY.md` (+ memory store), `README` if it lists tool counts
- Test: full `dotnet test`

**Interfaces:**
- Consumes: `TagQueue.Drain`, `WriteGate`, `LockProbe`, `Mp4Writer`, `NullLogger` inside the existing `Watching` handler.

- [ ] **Step 1: Add a drain at the start of the `Watching` handler**

In `ReadTools.Watching` (line ~503), after `string root = sandbox.RequireRoot();` and before constructing the resolver, drain opportunistically and surface what landed:

```csharp
        // Opportunistic drain (pass 2): your previous clip's lock cleared when you advanced, so
        // land any queued tags before resolving the current one. Shares the write single-flight.
        DrainReport drained;
        WriteGate.Enter();
        try { drained = TagQueue.Drain(root, new Mp4Writer(), NullLogger.Instance, LockProbe.IsInUse); }
        finally { WriteGate.Exit(); }
```

Then add to the existing `response` object (after `["candidates"] = array,` is set, near line ~536):

```csharp
        if (drained.Written.Count > 0 || drained.Dropped.Count > 0)
            response["drainedFromQueue"] = new JsonObject
            {
                ["written"] = drained.Written.Count,
                ["dropped"] = drained.Dropped.Count,
            };
        int pendingNow = TagQueue.Status(root, LockProbe.IsInUse).Count;
        if (pendingNow > 0) response["queuePending"] = pendingNow;
```

Add the needed usings to `ReadTools.cs`: `using ClipMetaCore.Logging;`, `using ClipMetaCore.Write;` (`ClipMetaCore.Watching` is already imported). `WriteGate` is in the same `ClipMetaMcp.Tools` namespace.

- [ ] **Step 2: Update the `library_watching` description**

Append one sentence to the `library_watching` tool description (line ~110) so the model knows the drain happens:

```
" Calling this also writes any previously queued tags whose clips have since been freed (see library_queue_tag)."
```

- [ ] **Step 3: Build, then run the FULL test suite**

Run: `dotnet build --nologo -v q`
Expected: 0 warnings, 0 errors.

Run: `dotnet test --nologo --no-build -v q`
Expected: ALL pass (give `clipmetascribe.Tests` a long timeout, minutes; not a hang).

- [ ] **Step 4: Update docs**

`docs/PITFALLS.md`, append a pass-2 entry:

```markdown
## 2026-06-21, Deferred-tag queue: a playing clip is locked against File.Replace
A clip a media player is showing cannot be written (File.Replace needs FILE_SHARE_DELETE, which
players don't grant). Pass-2 queues the tag (.clipmeta-queue, JSON, library root) and drains it
when the lock clears, on the next library_watching/library_queue_tag call, or library_flush_queue
/ scribe --flush-queue for the last clip. Drains share the MCP WriteGate so they never race a
direct write. Per-player lock-release on next/stop/close is still a dogfooding TODO, record
observed behavior here when measured.
```

Update the MCP tool count in `MEMORY.md` / `reference_mcp_server` memory from "8 tools" to **17** (14 prior + 3 queue tools), and note the `.mcpb` repack is needed to ship the queue tools to Desktop (`tools/pack-mcpb.ps1`). Update any README tool table.

- [ ] **Step 5: Commit**

```bash
git add clipmetamcp/Tools/ReadTools.cs docs/PITFALLS.md MEMORY.md README.md
git commit -m "feat(mcp): drain queue opportunistically on library_watching; docs + counts"
```

---

## Self-Review

**1. Spec coverage:**
- §2 data model → Tasks 1–2 (`QueuedMutation`, `QueuedTag`, `TagQueueData`, plus `DrainReport`/`QueueStatusEntry`). ✓
- §3 `TagQueue` engine (load/save/enqueue/drain/status) → Tasks 3–5. ✓
- §4 "dumb queue" invariant → Task 8 (`library_queue_tag` only takes a resolved, sandbox-checked path; no resolution in the queue). ✓
- §5 drain triggers: opportunistic on `library_queue_tag` (Task 8) + `library_watching` (Task 9); explicit MCP flush (Task 8) + CLI `--flush-queue` (Task 7). ✓
- §6 surfaces: 3 MCP tools (Task 8), CLI flush + `--watching` footer (Task 7), surface test (Task 8 Step 5), shared `WriteGate` (Task 6). ✓
- §7 safety: single-flight (Tasks 6, 8, 9), cloud-safe probe (injected `LockProbe.IsInUse`), corruption-tolerant load (Task 3), atomic save (Task 3), field-level apply at drain time (Task 5), vanished→drop (Task 5). ✓
- §8 tests: every listed case mapped to a Task 3/5/8 test. ✓
- Definition of Done: full build/test (Task 9 Step 3), zero NuGet (System.Text.Json is BCL), docs (Task 9 Step 4). ✓

**2. Placeholder scan:** The only deferred specifics are the MCP test bodies in Task 8 Step 1, which explicitly instruct mirroring the existing `McpHarness`/`LibraryWatchingToolTests` patterns rather than inventing an API, the surrounding production code and assertions are fully specified. The `TestClips.CopyPristineToScratch` helper name in Task 5 is flagged to match the project's actual pristine-copy helper. No "TBD"/"add error handling"/"similar to" placeholders in production code.

**3. Type consistency:** `QueuedMutation`/`QueuedTag`/`TagQueueData`/`DrainReport`/`QueueStatusEntry` signatures are identical across Tasks 1, 2, 5, 8. `TagQueue.Drain(libraryDir, IMediaWriter, IClipMetaLogger, Func<string,bool>)` and `TagQueue.Status(libraryDir, Func<string,bool>)` match between Task 5 (def), Task 7 (CLI use), Task 8 (MCP use), Task 9 (watching use). `WriteGate.Enter()`/`Exit()` consistent across Tasks 6, 8, 9. `TagQueue.QueueFileName` used in the Task 3 corrupt-file test matches the const.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-06-21-watched-clip-resolution-pass2.md`. Two execution options:

1. **Subagent-Driven (recommended)**, a fresh subagent per task, two-stage review between tasks, fast iteration.
2. **Inline Execution**, execute tasks in this session with checkpoints for review.

Which approach?
