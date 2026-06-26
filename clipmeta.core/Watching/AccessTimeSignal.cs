namespace ClipMetaCore.Watching;

/// <summary>
/// Emits library clips ordered by most-recently-accessed first. Recency alone is never certain
/// (indexers, AV, other apps bump access time), so every hit is ambiguous; the resolver only
/// surfaces these as the fallback / corroborating signal. Excludes clips that ClipMeta itself
/// read or wrote within the session (diagnostic reads must not float a stale clip to the top).
/// </summary>
public sealed class AccessTimeSignal : IWatchSignal
{
    /// <summary>The signal name and the source tag on its hits.</summary>
    public const string SourceName = "access_time";

    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates the signal.</summary>
    /// <param name="clock">Now-provider in UTC (injected for tests); defaults to the system clock.</param>
    public AccessTimeSignal(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <inheritdoc/>
    public string Name => SourceName;

    /// <inheritdoc/>
    public IEnumerable<SignalHit> Detect(WatchContext context)
    {
        DateTimeOffset now = _clock();
        foreach (LibraryClip clip in context.LibraryClips.OrderByDescending(c => c.LastAccessTimeUtc))
        {
            // Skip clips ClipMeta itself just read (export / get-metadata bump access time): a
            // diagnostic read must not float a dead file to the top of the fallback ranking.
            if (context.Ledger?.WasTouchedWithin(clip.FullPath, SelfActionLedger.DefaultWindow, now) == true)
                continue;
            yield return new SignalHit(clip.FullPath, SourceName, Player: null, Ambiguous: true);
        }
    }
}
