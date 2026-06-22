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
