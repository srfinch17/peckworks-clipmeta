using ClipMetaCore.Schema;

namespace ClipMetaCore.Write;

/// <summary>
/// Applies canonical normalization rules before writing any metadata.
/// Rules: trim whitespace, lowercase tag values, deduplicate pipe lists,
/// canonicalize timecodes to HH:MM:SS, treat empty string as delete.
/// </summary>
public static class Normalizer
{
    /// <summary>Lowercases and trims a single tag value.</summary>
    public static string NormalizeTag(string value) => value.Trim().ToLowerInvariant();

    /// <summary>
    /// Normalizes a pipe-separated list: trims each item, lowercases, deduplicates
    /// while preserving first-occurrence order.
    /// </summary>
    public static string NormalizePipeList(string value)
    {
        var seen = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string part in value.Split('|'))
        {
            string normalized = part.Trim().ToLowerInvariant();
            if (normalized.Length > 0 && set.Add(normalized))
                seen.Add(normalized);
        }
        return string.Join("|", seen);
    }

    /// <summary>
    /// Appends <paramref name="newItem"/> to an existing pipe-separated list,
    /// normalizing and deduplicating the result.
    /// </summary>
    public static string AppendToPipeList(string existing, string newItem)
    {
        string combined = existing.Length > 0 ? $"{existing}|{newItem}" : newItem;
        return NormalizePipeList(combined);
    }

    /// <summary>
    /// Appends <paramref name="incoming"/> onto <paramref name="existing"/> using the rule for
    /// <paramref name="field"/>: a prose field (notes) joins with a space, preserving case and never
    /// deduplicating; every other field is treated as a pipe list (merge + dedup + lowercase). The
    /// field key may be bare ("notes") or domain-qualified ("com.peckworkslab.clipmeta:notes").
    /// </summary>
    public static string AppendValue(string field, string existing, string incoming)
    {
        if (ClipMetaSchema.ProseFields.Contains(BareName(field)))
            return existing.Length == 0 ? incoming : $"{existing.TrimEnd()} {incoming}";
        return AppendToPipeList(existing, incoming);
    }

    /// <summary>Strips the domain prefix from a possibly-qualified atom key to the bare field name.</summary>
    private static string BareName(string field)
    {
        int colonIdx = field.IndexOf(':');
        return colonIdx >= 0 ? field[(colonIdx + 1)..] : field;
    }

    /// <summary>
    /// Normalizes a timecode string to HH:MM:SS.
    /// Accepts: "45", "0:45", "00:00:45", "1:23:45".
    /// </summary>
    /// <exception cref="ArgumentException">When any segment is not a valid integer.</exception>
    public static string NormalizeTimecode(string value)
    {
        string[] parts = value.Trim().Split(':');
        int h = 0, m = 0, s = 0;
        if (parts.Length == 1)
        {
            if (!int.TryParse(parts[0], out s))
                throw new ArgumentException($"Invalid timecode segment: '{parts[0]}'");
        }
        else if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], out m) || !int.TryParse(parts[1], out s))
                throw new ArgumentException($"Invalid timecode format: '{value}'");
        }
        else
        {
            if (!int.TryParse(parts[0], out h) || !int.TryParse(parts[1], out m) || !int.TryParse(parts[2], out s))
                throw new ArgumentException($"Invalid timecode format: '{value}'");
        }
        return $"{h:D2}:{m:D2}:{s:D2}";
    }

    /// <summary>Normalizes a timecode pipe list (each individual timecode).</summary>
    public static string NormalizeTimecodePipeList(string value)
    {
        var parts = value.Split('|')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(NormalizeTimecode)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return string.Join("|", parts);
    }

    /// <summary>Clamps rating to 1–5. Throws <see cref="ArgumentException"/> for non-integer input.</summary>
    public static string NormalizeRating(string value)
    {
        if (int.TryParse(value.Trim(), out int r))
            return Math.Clamp(r, 1, 5).ToString();
        throw new ArgumentException($"Rating must be an integer 1–5, got: '{value.Trim()}'");
    }

    /// <summary>
    /// Applies normalization to a <see cref="MetadataMutation"/> in place:
    /// normalizes values, moves empty sets to DeleteFields.
    /// </summary>
    public static void ApplyToMutation(MetadataMutation mutation)
    {
        var toDelete = new List<string>();
        var toUpdate = new Dictionary<string, string?>();

        foreach (var (field, value) in mutation.SetFields)
        {
            if (string.IsNullOrEmpty(value))
            {
                toDelete.Add(field);
                continue;
            }
            toUpdate[field] = NormalizeFieldValue(field, value);
        }

        foreach (string field in toDelete)
        {
            mutation.SetFields.Remove(field);
            mutation.DeleteFields.Add(field);
        }
        foreach (var (field, value) in toUpdate)
            mutation.SetFields[field] = value;

        var appendKeys = mutation.AppendFields.Keys.ToList();
        foreach (string field in appendKeys)
            mutation.AppendFields[field] = NormalizeFieldValue(field, mutation.AppendFields[field]);
    }

    /// <summary>
    /// Canonicalizes one field's value by its kind: pipe-list (tags/players), timecode list, rating
    /// clamp, or a plain trim. The field key may be bare ("tags") or domain-qualified. Public so the
    /// dry-run preview (<c>MetadataPreview</c>) applies the exact same rule as the writer, guaranteeing
    /// the preview matches the real write's read-back.
    /// </summary>
    public static string NormalizeFieldValue(string field, string value)
    {
        // Keys in mutation.SetFields are domain-qualified ("com.peckworkslab.clipmeta:tags").
        // PipeFields contains bare names ("tags"). Strip the domain prefix before comparing.
        string bareName = field;
        int colonIdx = field.IndexOf(':');
        if (colonIdx >= 0) bareName = field[(colonIdx + 1)..];

        if (ClipMetaSchema.PipeFields.Contains(bareName))
        {
            if (bareName == ClipMetaSchema.Timecode)
                return NormalizeTimecodePipeList(value);
            return NormalizePipeList(value);
        }
        if (bareName == ClipMetaSchema.Rating)
            return NormalizeRating(value);
        return value.Trim();
    }
}
