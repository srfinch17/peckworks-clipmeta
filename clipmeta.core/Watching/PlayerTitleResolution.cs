namespace ClipMetaCore.Watching;

/// <summary>One player window whose title named an .mp4, with the library clips it resolves to.</summary>
/// <param name="Window">The player window the title came from.</param>
/// <param name="Kind">Whether the title named a full path or a bare file name.</param>
/// <param name="ReferencedValue">The extracted reference (full path, or bare file name).</param>
/// <param name="Matches">Library clips the reference resolves to; empty when the file is not in the library.</param>
public sealed record PlayerMatch(
    ProcessWindow Window,
    TitleExtractionKind Kind,
    string ReferencedValue,
    IReadOnlyList<LibraryClip> Matches);

/// <summary>
/// Single source of truth for turning open player windows into resolved/unresolved matches against
/// the enumerated library. Both <see cref="PlayerTitleSignal"/> (for hits) and
/// <see cref="WatchingResolver"/> (for wrong-directory diagnostics) use this, so the two can never
/// disagree about what resolved. Windows whose titles contain no .mp4 are omitted entirely.
/// </summary>
public static class PlayerTitleResolution
{
    /// <summary>Resolves every player window whose title names an .mp4 against the library.</summary>
    public static IReadOnlyList<PlayerMatch> For(WatchContext context)
    {
        var result = new List<PlayerMatch>();
        foreach (ProcessWindow window in context.PlayerWindows)
        {
            TitleExtraction? extraction = PlayerTitleParser.Extract(window.WindowTitle);
            if (extraction is null)
                continue;

            TitleExtraction value = extraction.Value;
            IReadOnlyList<LibraryClip> matches = value.Kind == TitleExtractionKind.FullPath
                ? (context.ByFullPath.TryGetValue(value.Value, out LibraryClip? clip)
                    ? new[] { clip }
                    : Array.Empty<LibraryClip>())
                : (context.ByFileName.TryGetValue(value.Value, out IReadOnlyList<LibraryClip>? list)
                    ? list
                    : Array.Empty<LibraryClip>());

            result.Add(new PlayerMatch(window, value.Kind, value.Value, matches));
        }
        return result;
    }
}
