namespace ClipMetaCore.Watching;

/// <summary>
/// Pure, library-aware matching of a media-player window title to a known library file name.
/// Rather than extract a token from arbitrary title text and require it to equal a library key
/// (the old approach, which MPC-HC's timecode-prefixed titles silently defeated), this asks the
/// inverse question: which KNOWN library basename appears inside the title? That is immune to
/// every title-format quirk, playback-position prefixes, OSD text, paused state, custom formats, 
/// because the candidates are ground truth, not a guess.
/// </summary>
public static class LibraryTitleMatcher
{
    // Characters that cannot occur inside a Windows file name (plus the separators that bound one).
    // A match preceded/followed by one of these, or by whitespace, or by a string edge, sits at a
    // genuine file-name boundary; a match flanked by ordinary file-name characters is a substring of
    // a DIFFERENT, longer name (e.g. "clip.mp4" inside "myclip.mp4") and must be rejected.
    private const string BoundaryChars = "\\/:*?\"<>|";

    /// <summary>
    /// Returns the known basename that appears in <paramref name="title"/> at a file-name boundary,
    /// preferring the longest (most specific) match so prefix-overlapping names resolve
    /// deterministically. Case-insensitive (Windows file-system semantics). Returns null when the
    /// title is blank or names no known file.
    /// </summary>
    public static string? FindBestMatch(string? title, IEnumerable<string> knownBasenames)
    {
        ArgumentNullException.ThrowIfNull(knownBasenames);
        if (string.IsNullOrWhiteSpace(title))
            return null;

        string? best = null;
        foreach (string name in knownBasenames)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            if (!ContainsAtBoundary(title, name))
                continue;
            if (best is null || name.Length > best.Length)
                best = name;
        }
        return best;
    }

    private static bool ContainsAtBoundary(string title, string name)
    {
        int index = 0;
        while ((index = title.IndexOf(name, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int after = index + name.Length;
            bool leftIsBoundary = index == 0 || IsBoundary(title[index - 1]);
            bool rightIsBoundary = after >= title.Length || IsBoundary(title[after]);
            if (leftIsBoundary && rightIsBoundary)
                return true;
            index++;
        }
        return false;
    }

    private static bool IsBoundary(char c) =>
        char.IsWhiteSpace(c) || BoundaryChars.IndexOf(c) >= 0;
}
