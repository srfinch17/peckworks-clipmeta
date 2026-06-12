using ClipMetaCore.Mp4;
using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Displays file size and a summary of which clipmeta fields are set/unset.</summary>
internal static class StatsCommand
{
    /// <summary>
    /// Parses <paramref name="filePath"/>, computes field stats, and writes
    /// formatted output to <paramref name="output"/> (defaults to <see cref="Console.Out"/>).
    /// </summary>
    /// <returns>Exit code 0 on success.</returns>
    internal static int Run(string filePath, TextWriter? output = null)
    {
        output ??= Console.Out;

        var root       = Mp4Parser.ParseFile(filePath);
        var userFields = ClipMetaReader.GetUserFields(root);

        long bytes = new FileInfo(filePath).Length;
        output.WriteLine($"{Path.GetFileName(filePath)}  ({FormatBytes(bytes)})");

        if (userFields.Count == 0)
        {
            output.WriteLine("  (no clipmeta metadata)");
            return 0;
        }

        var stats = ClipMetaStats.Categorize(userFields);

        output.WriteLine($"  Fields set:    {string.Join(", ", stats.SetFields)}");
        if (stats.KnownUnset.Count > 0)
            output.WriteLine($"  Fields unset:  {string.Join(", ", stats.KnownUnset)}");
        if (stats.CustomFields.Count > 0)
            output.WriteLine($"  Custom fields: {string.Join(", ", stats.CustomFields)}");

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
