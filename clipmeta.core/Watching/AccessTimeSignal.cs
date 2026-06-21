namespace ClipMetaCore.Watching;

/// <summary>
/// Emits library clips ordered by most-recently-accessed first. Recency alone is never certain
/// (indexers, AV, other apps bump access time), so every hit is ambiguous; the resolver only
/// surfaces these as the fallback / corroborating signal.
/// </summary>
public sealed class AccessTimeSignal : IWatchSignal
{
    /// <summary>The signal name and the source tag on its hits.</summary>
    public const string SourceName = "access_time";

    /// <inheritdoc/>
    public string Name => SourceName;

    /// <inheritdoc/>
    public IEnumerable<SignalHit> Detect(WatchContext context)
    {
        foreach (LibraryClip clip in context.LibraryClips.OrderByDescending(c => c.LastAccessTimeUtc))
            yield return new SignalHit(clip.FullPath, SourceName, Player: null, Ambiguous: true);
    }
}
