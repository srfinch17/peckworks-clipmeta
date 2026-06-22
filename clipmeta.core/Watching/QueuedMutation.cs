// clipmeta.core/Watching/QueuedMutation.cs
using ClipMetaCore.Write;

namespace ClipMetaCore.Watching;

/// <summary>
/// The durable subset of a <see cref="MetadataMutation"/> — the field changes worth persisting in
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
    /// <c>BackupPath</c> is null — a drained tag is a real write and backups are a per-call policy
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
