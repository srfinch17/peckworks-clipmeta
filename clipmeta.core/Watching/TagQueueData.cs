// clipmeta.core/Watching/TagQueueData.cs
namespace ClipMetaCore.Watching;

/// <summary>The full contents of a library's deferred-tag queue file.</summary>
public sealed record TagQueueData(
    /// <summary>Queue schema version (current: 1).</summary>
    int Version,
    /// <summary>All pending tags, in enqueue order.</summary>
    IReadOnlyList<QueuedTag> Entries);
