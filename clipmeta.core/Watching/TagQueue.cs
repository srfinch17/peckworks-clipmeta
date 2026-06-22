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
