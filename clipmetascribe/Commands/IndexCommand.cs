using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Builds a metadata index for a directory of MP4 files.</summary>
internal static class IndexCommand
{
    /// <summary>
    /// Scans <paramref name="directory"/>, builds a metadata index, and writes it to
    /// <c>.clipmeta-index</c> inside the directory.
    /// </summary>
    /// <param name="directory">The directory to index.</param>
    /// <param name="output">Output writer; defaults to <see cref="Console.Out"/>.</param>
    /// <returns>Exit code 0 on success.</returns>
    internal static int Run(string directory, TextWriter? output = null)
    {
        output ??= Console.Out;

        var data = ClipMetaIndex.Build(directory);
        string indexPath = Path.Combine(directory, ClipMetaIndex.IndexFileName);
        ClipMetaIndex.WriteToFile(data, indexPath);

        output.WriteLine($"Indexed {data.Entries.Count} file(s). Index saved to {indexPath}.");
        return 0;
    }
}
