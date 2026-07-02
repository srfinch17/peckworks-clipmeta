using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Searches a directory for MP4 files matching a clipmeta field/value filter.</summary>
internal static class FindCommand
{
    /// <summary>
    /// Searches <paramref name="directory"/> for clips where <paramref name="field"/> contains
    /// <paramref name="value"/> and writes results to <paramref name="output"/>
    /// (defaults to <see cref="Console.Out"/>).
    /// </summary>
    /// <returns>Exit code 0 on success.</returns>
    internal static int Run(string directory, string field, string value, TextWriter? output = null)
    {
        output ??= Console.Out;

        output.WriteLine($"Searching {directory} for {field} = \"{value}\"");

        int count = 0;
        // A locked or unparseable clip must not abort the search; name it so the user knows to
        // re-run once it frees up, rather than the scan silently going quiet about it.
        foreach (string match in ClipMetaFinder.Find(directory, field, value,
            onFileSkipped: (path, ex) => output.WriteLine($"SKIPPED {path}: {ex.Message}")))
        {
            string relative = Path.GetRelativePath(directory, match);
            output.WriteLine($"  {relative}");
            count++;
        }

        if (count == 0)
            output.WriteLine("  No matches found.");
        else
            output.WriteLine($"{count} match(es) found.");

        return 0;
    }
}
