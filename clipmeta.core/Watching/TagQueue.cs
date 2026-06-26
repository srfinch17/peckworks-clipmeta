// clipmeta.core/Watching/TagQueue.cs
using System.Text.Json;
using ClipMetaCore.Abstractions;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;

namespace ClipMetaCore.Watching;

/// <summary>
/// Durable deferred-tag queue stored in a library root. A clip that is playing is locked against
/// our write (<see cref="System.IO.File.Replace(string, string, string?)"/> needs a delete-share
/// the player does not grant), so spoken tags are persisted here and written as the locks clear.
/// The queue stores only confirmed, already-resolved paths — it never resolves or guesses.
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
    /// throws — the queue is opportunistic state on a watched-clip call, never a hard dependency.
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
                ? MergeAppend(k, existingVal, v)                                  // prose-join or pipe-merge
                : v;

        var delete = new HashSet<string>(prior.DeleteFields);
        foreach (string d in next.DeleteFields) delete.Add(d);                    // union

        return new QueuedMutation(set, append, delete.ToList(), prior.ClearAll || next.ClearAll);
    }

    /// <summary>
    /// Accumulates a queued append for <paramref name="field"/>: a prose field (notes) joins with a
    /// space (case preserved, no dedup), every other field pipe-merges. Mirrors the write engine's
    /// <c>Normalizer.AppendValue</c> so the in-queue merge matches what eventually lands on disk.
    /// </summary>
    private static string MergeAppend(string field, string a, string b) =>
        ClipMetaSchema.ProseFields.Contains(DisplayField(field))
            ? $"{a.TrimEnd()}{Normalizer.ProseSeparator}{b}"
            : PipeMerge(a, b);

    /// <summary>Joins two pipe-delimited lists, dropping duplicate items (first occurrence wins).</summary>
    private static string PipeMerge(string a, string b)
    {
        var seen = new List<string>();
        foreach (string item in (a + "|" + b).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!seen.Contains(item, StringComparer.OrdinalIgnoreCase))
                seen.Add(item);
        return string.Join('|', seen);
    }

    /// <summary>
    /// Writes the queue atomically: serialize to a sibling temp file, then swap it into place with
    /// a retry on a transient AV/indexer lock. Mirrors <c>ClipMetaIndex.WriteToFile</c> — a crash
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
                   or InvalidOperationException or ArgumentException
                   or UnsupportedFormatException)
            {
                // The write could not land this pass (still-held handle that beat the probe,
                // verification failure, a bad value, or an unsupported/fragmented format).
                // Keep it queued rather than losing the tag.
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
            changed.AddRange(e.Mutation.SetFields.Keys.Select(DisplayField));
            changed.AddRange(e.Mutation.AppendFields.Keys.Select(DisplayField));
            changed.AddRange(e.Mutation.DeleteFields.Select(DisplayField));
            rows.Add(new QueueStatusEntry(
                e.ClipPath, changed, (now - e.EnqueuedAtUtc).TotalSeconds, isInUse(e.ClipPath)));
        }
        return rows;
    }

    /// <summary>
    /// Strips the clipmeta domain prefix from an atom key for display, so status shows the
    /// user-facing field name (<c>tags</c>) rather than the qualified atom (<c>domain:tags</c>).
    /// A key without the prefix (an unusual custom atom) is returned unchanged.
    /// </summary>
    private static string DisplayField(string atomKey)
    {
        string prefix = ClipMetaSchema.Domain + ":";
        return atomKey.StartsWith(prefix, StringComparison.Ordinal) ? atomKey[prefix.Length..] : atomKey;
    }
}
