using ClipMetaCore.Schema;

namespace ClipMetaCore.Read;

/// <summary>Categorized summary of which clipmeta fields are set on a clip.</summary>
public record ClipMetaFieldStats(
    /// <summary>Names of all user fields present, in document order (duplicates preserved).</summary>
    IReadOnlyList<string> SetFields,
    /// <summary>Well-known fields (see <see cref="ClipMetaSchema.KnownFields"/>) not present.</summary>
    IReadOnlyList<string> KnownUnset,
    /// <summary>Fields present that are not well-known, user-invented custom names.</summary>
    IReadOnlyList<string> CustomFields);

/// <summary>
/// Categorizes a clip's user fields into set / known-unset / custom. Shared by the CLI
/// <c>--stats</c> command and the MCP <c>clip_get_stats</c> tool so both report identically.
/// </summary>
public static class ClipMetaStats
{
    /// <summary>
    /// Categorizes <paramref name="userFields"/> (typically from
    /// <see cref="ClipMetaReader.GetUserFields"/>, internal fields must already be excluded).
    /// </summary>
    /// <param name="userFields">User-facing (Field, Value) pairs in document order.</param>
    /// <returns>The categorized field names.</returns>
    public static ClipMetaFieldStats Categorize(IEnumerable<(string Field, string Value)> userFields)
    {
        var setFields = userFields.Select(f => f.Field).ToList();
        var knownUnset = ClipMetaSchema.KnownFields
            .Where(k => !setFields.Contains(k, StringComparer.Ordinal))
            .ToList();
        var customFields = setFields
            .Where(n => !ClipMetaSchema.KnownFields.Contains(n, StringComparer.Ordinal))
            .ToList();
        return new ClipMetaFieldStats(setFields, knownUnset, customFields);
    }
}
