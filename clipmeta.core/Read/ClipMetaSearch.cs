namespace ClipMetaCore.Read;

/// <summary>Searches a loaded <see cref="IndexData"/> for entries matching field/value criteria.</summary>
public static class ClipMetaSearch
{
    /// <summary>
    /// Returns all entries in <paramref name="index"/> where <paramref name="field"/> has a value
    /// containing <paramref name="value"/> (case-insensitive substring match).
    /// Passing an empty string for <paramref name="value"/> returns all entries that have the field.
    /// </summary>
    /// <param name="index">The index data to search.</param>
    /// <param name="field">The field name to search for (case-insensitive).</param>
    /// <param name="value">The value to match (case-insensitive substring).</param>
    /// <returns>A read-only list of entries matching the criteria.</returns>
    public static IReadOnlyList<IndexEntry> Find(IndexData index, string field, string value)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);

        var results = new List<IndexEntry>();
        foreach (var entry in index.Entries)
        {
            foreach (var (f, v) in entry.Fields)
            {
                if (f.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                    v.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(entry);
                    break;
                }
            }
        }
        return results;
    }
}
