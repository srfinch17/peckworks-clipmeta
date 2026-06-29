namespace ClipMetaCore.Watching;

/// <summary>
/// Gaming-mode signal: surfaces clips the game JUST SAVED to the library, identified by a fresh
/// <see cref="LibraryClip.CreationTimeUtc"/> within the freshness window, excluding paths already
/// indexed (baseline) and paths ClipMeta itself just wrote (self-ledger). Creation time, not write
/// time, is the right key: copying a clip into the library preserves the source's old write time
/// (fresh clip looks old) while always stamping a new creation time; and ClipMeta's tag-write bumps
/// write time (self-write looks fresh). Exactly one clip in the window is the unambiguous just-saved
/// case; two or more is ambiguous (the resolver demotes those to confirm-first).
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
        DateTimeOffset nowOffset = new(DateTime.SpecifyKind(now, DateTimeKind.Utc), TimeSpan.Zero);

        List<LibraryClip> fresh = context.LibraryClips
            .Where(c =>
                // (a) genuinely new to the library, not already in the persisted index
                !context.KnownBaselinePaths.Contains(c.FullPath) &&
                // (b) created within the freshness window (creation time, not write time)
                now - c.CreationTimeUtc <= _window && now - c.CreationTimeUtc >= TimeSpan.Zero &&
                // (c) not a clip ClipMeta itself just wrote (self-write bumps write time, not creation)
                context.Ledger?.WasWrittenWithin(c.FullPath, _window, nowOffset) != true)
            .OrderByDescending(c => c.CreationTimeUtc)
            .ToList();

        // One fresh clip is the unambiguous "just saved" case; several saved at once is ambiguous.
        bool ambiguous = fresh.Count > 1;
        foreach (LibraryClip clip in fresh)
            yield return new SignalHit(clip.FullPath, SourceName, Player: null, Ambiguous: ambiguous);
    }
}
