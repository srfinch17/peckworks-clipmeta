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
        // Only players whose title resolved to at least one library clip become hits; the resolver
        // handles the unresolved ones (wrong-directory diagnostics) via the same helper.
        var resolved = PlayerTitleResolution.For(context)
            .Where(match => match.Matches.Count > 0)
            .ToList();

        bool multiplePlayers = resolved.Count > 1;
        foreach (PlayerMatch match in resolved)
        {
            bool ambiguousFile = match.Matches.Count > 1;
            foreach (LibraryClip clip in match.Matches)
                yield return new SignalHit(clip.FullPath, SourceName, match.Window.ProcessName,
                    Ambiguous: multiplePlayers || ambiguousFile, MatchKind: match.Kind);
        }
    }
}
