namespace ClipMetaCore.Write;

/// <summary>Describes a set of metadata changes to apply atomically to one file.</summary>
public sealed class MetadataMutation
{
    /// <summary>Fields to set (or delete when value is null or empty string).</summary>
    public Dictionary<string, string?> SetFields { get; } = new();

    /// <summary>Fields to append values to; deduplicates pipe-delimited lists on write.</summary>
    public Dictionary<string, string> AppendFields { get; } = new();

    /// <summary>Field names to delete entirely.</summary>
    public HashSet<string> DeleteFields { get; } = new();

    /// <summary>When true, remove ALL com.peckworkslab.clipmeta atoms from the file.</summary>
    public bool ClearAll { get; set; }

    /// <summary>
    /// When true (the default), a write that stores a user field also stamps
    /// <c>tagged_by: Peckworks ClipMeta</c>. Opt-out for users who don't want provenance written
    /// into their files. A caller that supplies its own <c>tagged_by</c> value keeps it regardless.
    /// </summary>
    public bool StampProvenance { get; set; } = true;

    /// <summary>When true, log what would change without writing anything.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// When non-null, File.Replace will save the original file here before swapping.
    /// Set by callers that pass --backup; null means no backup.
    /// </summary>
    public string? BackupPath { get; set; }
}
