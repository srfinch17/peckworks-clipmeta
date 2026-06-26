namespace ClipMetaCore.Watching;

/// <summary>
/// Gaming-mode signal: surfaces clips the game JUST SAVED to the library, identified by a
/// <see cref="LibraryClip.LastWriteTimeUtc"/> within a freshness window of now. Write time (unlike
/// access time) is not bumped by merely watching an old clip, so this cleanly answers "tag the clip
/// I just made" when no media player is open. Exactly one clip in the window is the unambiguous
/// just-saved case; two or more is ambiguous (the resolver demotes those to confirm-first).
/// </summary>
public sealed class RecentWriteSignal : IWatchSignal
{
    /// <summary>The signal name and the source tag on its hits.</summary>
    public const string SourceName = "recent_write";

    /// <summary>Default window for treating a write as "just saved".</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _window;

    /// <summary>Creates the signal.</summary>
    /// <param name="clock">Now-provider in UTC (injected for tests); defaults to the system clock.</param>
    /// <param name="window">Freshness window; defaults to <see cref="DefaultWindow"/>.</param>
    public RecentWriteSignal(Func<DateTime>? clock = null, TimeSpan? window = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _window = window ?? DefaultWindow;
    }

    /// <inheritdoc/>
    public string Name => SourceName;

    /// <inheritdoc/>
    public IEnumerable<SignalHit> Detect(WatchContext context)
    {
        DateTime now = _clock();
        List<LibraryClip> fresh = context.LibraryClips
            .Where(c => now - c.LastWriteTimeUtc <= _window && now - c.LastWriteTimeUtc >= TimeSpan.Zero)
            .OrderByDescending(c => c.LastWriteTimeUtc)
            .ToList();

        // One fresh clip is the unambiguous "just saved" case; several saved at once is ambiguous.
        bool ambiguous = fresh.Count > 1;
        foreach (LibraryClip clip in fresh)
            yield return new SignalHit(clip.FullPath, SourceName, Player: null, Ambiguous: ambiguous);
    }
}
