namespace ClipMetaCore.Watching;

/// <summary>
/// Resolves a clip from a player's window title. Drops anything that does not resolve to a clip
/// inside the enumerated library (no fabrication). A hit is ambiguous when more than one recognized
/// player has a resolvable title, or when a bare filename matches more than one library clip.
/// </summary>
public sealed class PlayerTitleSignal : IWatchSignal
{
    /// <summary>The signal name and the source tag on its hits.</summary>
    public const string SourceName = "player_title";

    /// <inheritdoc/>
    public string Name => SourceName;

    /// <inheritdoc/>
    public IEnumerable<SignalHit> Detect(WatchContext context)
    {
        var perPlayer = new List<(ProcessWindow Window, IReadOnlyList<LibraryClip> Clips)>();
        foreach (ProcessWindow window in context.PlayerWindows)
        {
            TitleExtraction? extraction = PlayerTitleParser.Extract(window.WindowTitle);
            if (extraction is null)
                continue;
            IReadOnlyList<LibraryClip> matches = Resolve(extraction.Value, context);
            if (matches.Count > 0)
                perPlayer.Add((window, matches));
        }

        bool multiplePlayers = perPlayer.Count > 1;
        foreach ((ProcessWindow window, IReadOnlyList<LibraryClip> clips) in perPlayer)
        {
            bool ambiguousFile = clips.Count > 1;
            foreach (LibraryClip clip in clips)
                yield return new SignalHit(clip.FullPath, SourceName, window.ProcessName,
                    Ambiguous: multiplePlayers || ambiguousFile);
        }
    }

    private static IReadOnlyList<LibraryClip> Resolve(TitleExtraction extraction, WatchContext context)
    {
        if (extraction.Kind == TitleExtractionKind.FullPath)
        {
            return context.ByFullPath.TryGetValue(extraction.Value, out LibraryClip? clip)
                ? new[] { clip }
                : Array.Empty<LibraryClip>();
        }

        return context.ByFileName.TryGetValue(extraction.Value, out IReadOnlyList<LibraryClip>? list)
            ? list
            : Array.Empty<LibraryClip>();
    }
}
