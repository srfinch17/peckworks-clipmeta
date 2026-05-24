using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Searches a clipmeta index for files matching a field/value filter.</summary>
internal static class IndexSearchCommand
{
    /// <summary>
    /// Loads <c>.clipmeta-index</c> from <paramref name="directory"/> and writes matching
    /// file paths to <paramref name="output"/>. Returns exit code 1 if no index exists.
    /// </summary>
    /// <param name="directory">The directory containing the index file.</param>
    /// <param name="field">The field name to search for.</param>
    /// <param name="value">The value to match (case-insensitive substring).</param>
    /// <param name="output">Output writer; defaults to <see cref="Console.Out"/>.</param>
    /// <returns>Exit code 0 on success, 1 if no index found, 2 on read error.</returns>
    internal static int Run(string directory, string field, string value, TextWriter? output = null)
    {
        output ??= Console.Out;

        string indexPath = Path.Combine(directory, ClipMetaIndex.IndexFileName);
        if (!File.Exists(indexPath))
        {
            Console.Error.WriteLine($"Error: No index found at '{indexPath}'. Run --index first.");
            return 1;
        }

        IndexData data;
        try { data = ClipMetaIndex.ReadFromFile(indexPath); }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Error reading index: {ex.Message}");
            return 2;
        }

        var matches = ClipMetaSearch.Find(data, field, value);

        output.WriteLine($"Searching index for {field} = \"{value}\"");
        if (matches.Count == 0)
        {
            output.WriteLine("  No matches found.");
        }
        else
        {
            foreach (var entry in matches)
            {
                string relative = Path.GetRelativePath(directory, entry.FilePath);
                output.WriteLine($"  {relative}");
            }
            output.WriteLine($"{matches.Count} match(es) found.");
        }
        return 0;
    }
}
