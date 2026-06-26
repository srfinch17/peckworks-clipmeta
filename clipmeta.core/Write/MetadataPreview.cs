using ClipMetaCore.Schema;

namespace ClipMetaCore.Write;

/// <summary>
/// Computes the curated (user-facing) fields a write would leave on a clip, WITHOUT touching the file.
/// Reuses the writer's <see cref="Normalizer"/> so the prediction matches an actual write's read-back
/// exactly — the property the dry-run preview depends on. Internal bookkeeping stamps
/// (<c>schema</c>, <c>tagged_by</c>) are never shown, matching what a real post-write
/// <c>clip_get_metadata</c> returns (it hides internal fields).
/// </summary>
public static class MetadataPreview
{
    /// <summary>
    /// Predicts the post-write user fields given the clip's <paramref name="current"/> user fields
    /// (from <c>ClipMetaReader.GetUserFields</c>) and the <paramref name="mutation"/> to apply.
    /// </summary>
    /// <param name="current">Current user fields, in document order.</param>
    /// <param name="mutation">The mutation whose result to predict.</param>
    /// <returns>Predicted (Field, Value) pairs, document order preserved, new fields appended.</returns>
    public static IReadOnlyList<(string Field, string Value)> Predict(
        IReadOnlyList<(string Field, string Value)> current, MetadataMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(mutation);

        var fields = new List<(string Field, string Value)>(current);

        // --clear-all removes every clipmeta atom; any explicit sets in the same mutation are applied
        // on top of the cleared slate below (the public API permits set + clear-all together).
        if (mutation.ClearAll)
            fields.Clear();

        int IndexOf(string bare) =>
            fields.FindIndex(f => string.Equals(f.Field, bare, StringComparison.Ordinal));

        void Remove(string bare) { int i = IndexOf(bare); if (i >= 0) fields.RemoveAt(i); }
        void Upsert(string bare, string value)
        {
            int i = IndexOf(bare);
            if (i >= 0) fields[i] = (bare, value);   // replace in place (order preserved)
            else fields.Add((bare, value));          // new field appended, like the writer
        }

        foreach (string key in mutation.DeleteFields)
            Remove(Bare(key));

        foreach (var (key, value) in mutation.SetFields)
        {
            string bare = Bare(key);
            if (string.IsNullOrEmpty(value)) { Remove(bare); continue; }   // empty == delete idiom
            Upsert(bare, Normalizer.NormalizeFieldValue(key, value));
        }

        foreach (var (key, incoming) in mutation.AppendFields)
        {
            string bare = Bare(key);
            int i = IndexOf(bare);
            string currentVal = i >= 0 ? fields[i].Value : string.Empty;
            string normalizedIncoming = Normalizer.NormalizeFieldValue(key, incoming);
            Upsert(bare, Normalizer.AppendValue(key, currentVal, normalizedIncoming));
        }

        return fields;
    }

    /// <summary>Strips the domain prefix from a possibly-qualified atom key to the bare field name.</summary>
    private static string Bare(string atomKey)
    {
        int colon = atomKey.IndexOf(':');
        return colon >= 0 ? atomKey[(colon + 1)..] : atomKey;
    }
}
