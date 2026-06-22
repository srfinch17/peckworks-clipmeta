// clipmeta.core/Watching/TagQueue.cs
using System.Text.Json;
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
}
