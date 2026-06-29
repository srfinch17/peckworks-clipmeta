using System.IO.Enumeration;

namespace ClipMetaCore.Read;

/// <summary>One clip file found by a library listing, file facts only, no parsing.</summary>
public record ClipFileInfo(
    /// <summary>Full path to the MP4 file.</summary>
    string FilePath,
    /// <summary>File size in bytes.</summary>
    long SizeBytes,
    /// <summary>UTC last-write time.</summary>
    DateTimeOffset LastModified);

/// <summary>
/// Enumerates the MP4 files of a clips library by file name, the discovery primitive for
/// "what clips do I have?" questions, as opposed to the metadata-driven search in
/// <see cref="ClipMetaFinder"/>/<see cref="ClipMetaSearch"/>. Deliberately does NOT parse any
/// file: listing a 500-clip library must cost 500 stat calls, not 500 MP4 parses.
/// </summary>
public static class ClipMetaLibrary
{
    /// <summary>
    /// Lists .mp4 files in <paramref name="directory"/>, newest first (most listings are
    /// "show me my recent clips"). <paramref name="namePattern"/> is a simple wildcard
    /// expression matched case-insensitively against the file NAME only (e.g. "*2026.01*",
    /// "tf2*.mp4"); null/blank means all. Because the pattern is applied to names after a
    /// fixed "*.mp4" directory enumeration, it cannot influence WHICH directories are walked, 
    /// no path separators or traversal sequences have any effect.
    /// </summary>
    /// <param name="directory">Directory to list.</param>
    /// <param name="namePattern">Optional wildcard filter on the file name ('*' and '?').</param>
    /// <param name="recursive">When true (default), includes subdirectories.</param>
    /// <returns>Matching clips, most recently modified first.</returns>
    public static IReadOnlyList<ClipFileInfo> ListClips(
        string directory, string? namePattern = null, bool recursive = true)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        bool filterByName = !string.IsNullOrWhiteSpace(namePattern);

        var clips = new List<ClipFileInfo>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.mp4", option))
        {
            if (filterByName &&
                !FileSystemName.MatchesSimpleExpression(namePattern!, Path.GetFileName(path), ignoreCase: true))
            {
                continue;
            }

            var info = new FileInfo(path);
            clips.Add(new ClipFileInfo(
                path,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
        }

        return clips.OrderByDescending(c => c.LastModified).ToList();
    }
}
