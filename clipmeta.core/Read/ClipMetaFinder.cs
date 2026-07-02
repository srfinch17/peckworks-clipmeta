using ClipMetaCore.Mp4;

namespace ClipMetaCore.Read;

/// <summary>Searches directories for MP4 files matching a clipmeta field/value filter.</summary>
public static class ClipMetaFinder
{
    /// <summary>
    /// Enumerates .mp4 files in <paramref name="directory"/> and yields paths where
    /// <paramref name="field"/> has a value containing <paramref name="value"/>
    /// (case-insensitive substring match).
    /// Malformed or unreadable files are skipped, not thrown, so one bad clip never aborts
    /// the scan.
    /// </summary>
    /// <param name="directory">The directory to search.</param>
    /// <param name="field">Bare field name (e.g. "game"), matched case-insensitively.</param>
    /// <param name="value">Substring to search for within the field value, case-insensitively.</param>
    /// <param name="recursive">When true, searches subdirectories. Default: true.</param>
    /// <param name="onFileSkipped">
    /// Optional callback invoked with the file's path and the exception that caused it to be
    /// skipped (locked, unreadable, or unparseable). Lets a caller report which file was
    /// skipped instead of the scan going silent about it. Defaults to no-op.
    /// </param>
    public static IEnumerable<string> Find(
        string directory, string field, string value, bool recursive = true,
        Action<string, Exception>? onFileSkipped = null)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (string path in Directory.EnumerateFiles(directory, "*.mp4", option))
        {
            BoxNode root;
            try { root = Mp4Parser.ParseFile(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                onFileSkipped?.Invoke(path, ex);
                continue;
            }

            var fields = ClipMetaReader.GetFields(root);
            foreach (var (f, v) in fields)
            {
                if (f.Equals(field, StringComparison.OrdinalIgnoreCase) &&
                    v.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                    break; // don't yield the same file twice if multiple fields match
                }
            }
        }
    }
}
