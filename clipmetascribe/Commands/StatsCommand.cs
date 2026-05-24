using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;

namespace ClipMetaScribe.Commands;

/// <summary>Displays file size and a summary of which clipmeta fields are set/unset.</summary>
internal static class StatsCommand
{
    private static readonly string[] KnownFields =
    [
        ClipMetaSchema.Game, ClipMetaSchema.Players, ClipMetaSchema.Tags,
        ClipMetaSchema.Timecode, ClipMetaSchema.Rating, ClipMetaSchema.Notes,
    ];

    /// <summary>
    /// Parses <paramref name="filePath"/>, computes field stats, and writes
    /// formatted output to <paramref name="output"/> (defaults to <see cref="Console.Out"/>).
    /// </summary>
    /// <returns>Exit code 0 on success.</returns>
    internal static int Run(string filePath, TextWriter? output = null)
    {
        output ??= Console.Out;

        var root   = Mp4Parser.ParseFile(filePath);
        var fields = ClipMetaReader.GetFields(root);

        long bytes = new FileInfo(filePath).Length;
        output.WriteLine($"{Path.GetFileName(filePath)}  ({FormatBytes(bytes)})");

        // Exclude the internal schema version field from user-visible stats
        var userFields = fields.Where(f => !f.Field.Equals(ClipMetaSchema.Schema, StringComparison.Ordinal)).ToList();

        if (userFields.Count == 0)
        {
            output.WriteLine("  (no clipmeta metadata)");
            return 0;
        }

        var setFieldNames = userFields.Select(f => f.Field).ToList();
        var knownUnset    = KnownFields.Where(k => !setFieldNames.Contains(k, StringComparer.Ordinal)).ToList();
        var customFields  = setFieldNames.Where(n => !KnownFields.Contains(n, StringComparer.Ordinal)).ToList();

        output.WriteLine($"  Fields set:    {string.Join(", ", setFieldNames)}");
        if (knownUnset.Count > 0)
            output.WriteLine($"  Fields unset:  {string.Join(", ", knownUnset)}");
        if (customFields.Count > 0)
            output.WriteLine($"  Custom fields: {string.Join(", ", customFields)}");

        return 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }
}
