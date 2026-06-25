using System.Text.RegularExpressions;

namespace ClipMetaCore.Watching;

/// <summary>Whether a parsed title reference is a full path or a bare filename.</summary>
public enum TitleExtractionKind
{
    /// <summary>A drive-rooted absolute path, e.g. MPC-HC's title format.</summary>
    FullPath,

    /// <summary>A bare file name, e.g. VLC's "<c>name.mp4 - VLC media player</c>" format.</summary>
    BareName,
}

/// <summary>One <c>.mp4</c> reference extracted from a player window title.</summary>
public readonly record struct TitleExtraction(TitleExtractionKind Kind, string Value);

/// <summary>
/// Pure extraction of an <c>.mp4</c> reference from a media-player window title. Tries a
/// drive-rooted full path first (MPC-HC style), then a bare file name (VLC style). A title with no
/// <c>.mp4</c> (an embedded metadata title, a stopped player, a custom format) yields null. This
/// type only parses text; resolving a reference to a real library clip is the signal's job.
/// <para>
/// Note: bare-name <em>resolution</em> no longer relies on this extractor — title-format quirks
/// (a timecode prefix, OSD text) make token extraction brittle, so <see cref="PlayerTitleResolution"/>
/// matches the title against known library basenames via <see cref="LibraryTitleMatcher"/> instead.
/// Extraction here still drives full-path resolution and the wrong-directory diagnostics.
/// </para>
/// </summary>
public static partial class PlayerTitleParser
{
    // Drive-rooted absolute path ending in .mp4. Excludes characters illegal in Windows paths
    // (and the pipe/quote a title might use as a separator) so the match stops at the path's edge.
    [GeneratedRegex(@"([A-Za-z]:\\[^""|*?<>]+?\.mp4)", RegexOptions.IgnoreCase)]
    private static partial Regex FullPathRegex();

    // Bare file name ending in .mp4: no path separators, drive colon, or wildcard/quote chars.
    [GeneratedRegex(@"([^\\/:*?""<>|]+?\.mp4)", RegexOptions.IgnoreCase)]
    private static partial Regex BareNameRegex();

    /// <summary>Extracts the first <c>.mp4</c> reference from <paramref name="title"/>, or null.</summary>
    public static TitleExtraction? Extract(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        Match full = FullPathRegex().Match(title);
        if (full.Success)
            return new TitleExtraction(TitleExtractionKind.FullPath, full.Groups[1].Value);

        Match bare = BareNameRegex().Match(title);
        if (bare.Success)
            return new TitleExtraction(TitleExtractionKind.BareName, bare.Groups[1].Value);

        return null;
    }
}
