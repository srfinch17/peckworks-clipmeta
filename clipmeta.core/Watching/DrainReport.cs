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
