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
