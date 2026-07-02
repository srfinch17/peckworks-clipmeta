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

        // A locked or unparseable clip must not abort the scan (one bad file must not brick the
        // library index); name it so the user knows to re-run once it frees up.
        var data = ClipMetaIndex.Build(directory,
            onFileSkipped: (path, ex) => output.WriteLine($"SKIPPED {path}: {ex.Message}"));
        string indexPath = Path.Combine(directory, ClipMetaIndex.IndexFileName);
        ClipMetaIndex.WriteToFile(data, indexPath);

        output.WriteLine($"Indexed {data.Entries.Count} file(s). Index saved to {indexPath}.");
        return 0;
    }
}
