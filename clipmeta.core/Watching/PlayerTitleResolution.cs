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

            // 1. Full-path title (MPC config): an exact, folder-disambiguating reference. A full
            //    path that is NOT in the library is a genuine wrong-directory case, we do NOT fall
            //    back to basename containment, which could match a same-named file in a different
            //    library folder and mislead. It stays unresolved (feeds the wrong-directory warning).
            if (extraction is { Kind: TitleExtractionKind.FullPath } fullPath)
            {
                IReadOnlyList<LibraryClip> exact =
                    context.ByFullPath.TryGetValue(fullPath.Value, out LibraryClip? clip)
                        ? new[] { clip }
                        : Array.Empty<LibraryClip>();
                result.Add(new PlayerMatch(window, TitleExtractionKind.FullPath, fullPath.Value, exact));
                continue;
            }

            // 2. Otherwise, library-aware containment: which KNOWN library basename appears in the
            //    title? Immune to title-format quirks (timecode prefixes, OSD text, paused state)
            //    that defeated the old extract-then-exact-match path for MPC-HC.
            string? matchedName = LibraryTitleMatcher.FindBestMatch(window.WindowTitle, context.ByFileName.Keys);
            if (matchedName is not null)
            {
                result.Add(new PlayerMatch(
                    window, TitleExtractionKind.BareName, matchedName, context.ByFileName[matchedName]));
                continue;
            }

            // 3. Unresolved. If the title named some .mp4 token, surface it (no library match) so the
            //    wrong-directory diagnostics can describe what the player is on; a title naming no
            //    .mp4 at all is omitted entirely.
            if (extraction is { } ext)
                result.Add(new PlayerMatch(window, ext.Kind, ext.Value, Array.Empty<LibraryClip>()));
        }
        return result;
    }
}
