namespace ClipMetaCore.Watching;

/// <summary>
/// Runs the registered <see cref="IWatchSignal"/>s over a once-enumerated library, groups their
/// evidence per clip, and scores confidence by corroboration: a single unambiguous player-title hit
/// is "high" (auto-safe to write); everything else is "low" (confirm before mutating). The lock
/// probe enriches the few returned candidates as a tiebreaker and a pre-write warning.
/// </summary>
public sealed class WatchingResolver
{
    /// <summary>Confidence value for an auto-safe candidate.</summary>
    public const string HighConfidence = "high";

    /// <summary>Confidence value for a candidate that needs confirmation before a write.</summary>
    public const string LowConfidence = "low";

    private readonly IReadOnlyList<IWatchSignal> _signals;
    private readonly IProcessWindowSource _windowSource;
    private readonly IReadOnlyCollection<string> _playerNames;

    /// <summary>Creates a resolver over the given signals and process source.</summary>
    public WatchingResolver(
        IReadOnlyList<IWatchSignal> signals,
        IProcessWindowSource windowSource,
        IReadOnlyCollection<string>? playerNames = null)
    {
        _signals = signals;
        _windowSource = windowSource;
        _playerNames = playerNames ?? MediaPlayers.KnownProcessNames;
    }

    /// <summary>The pass-1 resolver: player-title then access-time signals.</summary>
    public static WatchingResolver CreateDefault(IProcessWindowSource windowSource) =>
        new(new IWatchSignal[] { new PlayerTitleSignal(), new AccessTimeSignal() }, windowSource);

    /// <summary>
    /// Resolves the watched-clip candidates under <paramref name="libraryRoot"/>, best first, capped
    /// at <paramref name="limit"/>. When <paramref name="includeAccessFallback"/> is false, only
    /// player-title candidates are returned (empty when no player resolves a clip).
    /// </summary>
    public IReadOnlyList<WatchingCandidate> Resolve(string libraryRoot, int limit, bool includeAccessFallback)
    {
        WatchContext context = WatchContext.Build(libraryRoot, _windowSource, _playerNames);

        var hitsByPath = new Dictionary<string, List<SignalHit>>(StringComparer.OrdinalIgnoreCase);
        foreach (IWatchSignal signal in _signals)
            foreach (SignalHit hit in signal.Detect(context))
            {
                if (!hitsByPath.TryGetValue(hit.ClipPath, out List<SignalHit>? list))
                    hitsByPath[hit.ClipPath] = list = new List<SignalHit>();
                list.Add(hit);
            }

        DateTime now = DateTime.UtcNow;
        var candidates = new List<WatchingCandidate>();
        foreach ((string path, List<SignalHit> hits) in hitsByPath)
        {
            bool hasPlayer = hits.Any(h => h.Source == PlayerTitleSignal.SourceName);

            // include_access_fallback governs whether access-only candidates appear at all.
            if (!hasPlayer && !includeAccessFallback)
                continue;

            // Safety: only ever surface clips that were enumerated from the library.
            if (!context.ByFullPath.TryGetValue(path, out LibraryClip? clip))
                continue;

            bool playerUnambiguous = hits.Any(h => h.Source == PlayerTitleSignal.SourceName && !h.Ambiguous);
            string source = hasPlayer ? PlayerTitleSignal.SourceName : AccessTimeSignal.SourceName;
            string? player = hits.FirstOrDefault(h => h.Player is not null)?.Player;

            candidates.Add(new WatchingCandidate(
                Path: clip.FullPath,
                Name: clip.FileName,
                Source: source,
                Player: player,
                LastAccessTimeUtc: clip.LastAccessTimeUtc,
                SecondsSinceAccess: Math.Max(0, (now - clip.LastAccessTimeUtc).TotalSeconds),
                InUse: false, // enriched below, only for the returned set
                Confidence: playerUnambiguous ? HighConfidence : LowConfidence));
        }

        // Rank (high first, then most-recent access) and cap BEFORE probing, so the lock probe only
        // opens the handful of files we actually return — never the whole library on a fallback pass.
        List<WatchingCandidate> ranked = candidates
            .OrderByDescending(c => c.Confidence == HighConfidence)
            .ThenByDescending(c => c.LastAccessTimeUtc)
            .Take(limit)
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
            ranked[i] = ranked[i] with { InUse = ProbeInUse(ranked[i].Path) };

        // Final ordering applies the lock probe as a tiebreaker within equal confidence.
        return ranked
            .OrderByDescending(c => c.Confidence == HighConfidence)
            .ThenByDescending(c => c.InUse)
            .ThenByDescending(c => c.LastAccessTimeUtc)
            .ToList();
    }

    /// <summary>
    /// True when the file has an open handle that denies exclusive access. Best-effort and never
    /// fatal: an unexpected failure reports not-in-use and the resolution continues.
    /// </summary>
    private static bool ProbeInUse(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
