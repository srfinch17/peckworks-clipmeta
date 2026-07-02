using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Displays distinct values for a metadata field across a directory of MP4 files.</summary>
internal static class VocabCommand
{
    /// <summary>
    /// Scans <paramref name="directory"/> for distinct values of <paramref name="field"/>
    /// and writes results to <paramref name="output"/> (defaults to <see cref="Console.Out"/>).
    /// </summary>
    /// <param name="directory">Directory to scan for .mp4 files.</param>
    /// <param name="field">Bare field name to enumerate (e.g. "game", "tags").</param>
    /// <param name="output">Output writer; defaults to <see cref="Console.Out"/> when null.</param>
    /// <returns>Exit code 0 on success.</returns>
    internal static int Run(string directory, string field, TextWriter? output = null)
    {
        output ??= Console.Out;
        output.WriteLine($"Scanning {directory} for field: {field}");

        // A locked or unparseable clip must not abort the scan; name it so the user knows to
        // re-run once it frees up, rather than the scan silently going quiet about it.
        var result = ClipMetaVocab.Enumerate(directory, field,
            onFileSkipped: (path, ex) => output.WriteLine($"SKIPPED {path}: {ex.Message}"));

        if (result.Counts.Count == 0)
        {
            output.WriteLine($"  (no clips have field '{field}')");
            return 0;
        }

        int labelWidth = result.Counts.Keys.Max(k => k.Length) + 2;
        foreach (var kvp in result.Counts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            output.WriteLine($"  {kvp.Key.PadRight(labelWidth)}{kvp.Value} clip(s)");

        output.WriteLine($"{result.Counts.Count} distinct value(s) across {result.ClipsWithField} clip(s).");
        return 0;
    }
}
