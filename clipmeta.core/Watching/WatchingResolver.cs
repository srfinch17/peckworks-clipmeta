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

    /// <summary>The caveat attached to a bare-name match whose file is not currently locked.</summary>
    private const string NotLockedNote =
        "not currently locked — may be a same-named file elsewhere; confirm before tagging";

    /// <summary>
    /// Resolves the watched-clip candidates under <paramref name="libraryRoot"/>, best first, capped
    /// at <paramref name="limit"/>. When <paramref name="includeAccessFallback"/> is false, only
    /// player-title candidates are returned (empty when no player resolves a clip).
    /// </summary>
    public WatchingResult Resolve(string libraryRoot, int limit, bool includeAccessFallback)
    {
        WatchContext context = WatchContext.Build(libraryRoot, _windowSource, _playerNames);

        // One source of truth for player→library resolution: feeds both the hit grouping (below)
        // and the wrong-directory diagnostics (here).
        IReadOnlyList<PlayerMatch> playerMatches = PlayerTitleResolution.For(context);
        bool anyPlayerResolved = playerMatches.Any(m => m.Matches.Count > 0);
        var unresolvedPlayers = playerMatches
            .Where(m => m.Matches.Count == 0)
            .Select(m => new UnresolvedPlayer(
                m.Window.ProcessName,
                m.ReferencedValue,
                m.Kind == TitleExtractionKind.FullPath ? Path.GetDirectoryName(m.ReferencedValue) : null))
            .Distinct()
            .ToList();
        var diagnostics = new WatchDiagnostics(unresolvedPlayers);

        // Row 7: a player is open on a file outside the library and NOTHING resolved → suppress the
        // access-time guesses so there is no tempting wrong answer; the warning leads.
        bool suppressAccessFallback = !anyPlayerResolved && unresolvedPlayers.Count > 0;

        var hitsByPath = new Dictionary<string, List<SignalHit>>(StringComparer.OrdinalIgnoreCase);
        foreach (IWatchSignal signal in _signals)
            foreach (SignalHit hit in signal.Detect(context))
            {
                if (!hitsByPath.TryGetValue(hit.ClipPath, out List<SignalHit>? list))
                    hitsByPath[hit.ClipPath] = list = new List<SignalHit>();
                list.Add(hit);
            }

        DateTime now = DateTime.UtcNow;
        var working = new List<WorkingCandidate>();
        foreach ((string path, List<SignalHit> hits) in hitsByPath)
        {
            bool hasPlayer = hits.Any(h => h.Source == PlayerTitleSignal.SourceName);

            if (!hasPlayer && !includeAccessFallback)
                continue;
            if (!hasPlayer && suppressAccessFallback)
                continue;

            if (!context.ByFullPath.TryGetValue(path, out LibraryClip? clip))
                continue;

            bool playerUnambiguous = hits.Any(h => h.Source == PlayerTitleSignal.SourceName && !h.Ambiguous);
            bool bareNameUnambiguous = hits.Any(h =>
                h.Source == PlayerTitleSignal.SourceName && !h.Ambiguous &&
                h.MatchKind == TitleExtractionKind.BareName);
            string source = hasPlayer ? PlayerTitleSignal.SourceName : AccessTimeSignal.SourceName;
            string? player = hits.FirstOrDefault(h => h.Player is not null)?.Player;

            var candidate = new WatchingCandidate(
                Path: clip.FullPath,
                Name: clip.FileName,
                Source: source,
                Player: player,
                LastAccessTimeUtc: clip.LastAccessTimeUtc,
                SecondsSinceAccess: Math.Max(0, (now - clip.LastAccessTimeUtc).TotalSeconds),
                InUse: false,
                Confidence: playerUnambiguous ? HighConfidence : LowConfidence,
                Note: null);

            working.Add(new WorkingCandidate(candidate, hasPlayer, bareNameUnambiguous));
        }

        // Collision guard: probe player-hit candidates now (there are at most a few — one per open
        // player), so a bare-name high hit whose file is NOT locked is demoted to low + note. This
        // is the only probing done before the cap; access-time candidates are still probed after it.
        for (int i = 0; i < working.Count; i++)
        {
            if (!working[i].IsPlayerHit)
                continue;
            bool inUse = LockProbe.IsInUse(working[i].Candidate.Path);
            WatchingCandidate c = working[i].Candidate with { InUse = inUse };
            if (working[i].BareNameUnambiguous && c.Confidence == HighConfidence && !inUse)
                c = c with { Confidence = LowConfidence, Note = NotLockedNote };
            working[i] = working[i] with { Candidate = c };
        }

        // Rank (high first, then most-recent access) and cap BEFORE probing the access-time rows,
        // so the lock probe never opens the whole library on a fallback pass.
        List<WorkingCandidate> ranked = working
            .OrderByDescending(w => w.Candidate.Confidence == HighConfidence)
            .ThenByDescending(w => w.Candidate.LastAccessTimeUtc)
            .Take(limit)
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
            if (!ranked[i].IsPlayerHit) // player hits already probed above
                ranked[i] = ranked[i] with
                {
                    Candidate = ranked[i].Candidate with { InUse = LockProbe.IsInUse(ranked[i].Candidate.Path) },
                };

        List<WatchingCandidate> finalCandidates = ranked
            .Select(w => w.Candidate)
            .OrderByDescending(c => c.Confidence == HighConfidence)
            .ThenByDescending(c => c.InUse)
            .ThenByDescending(c => c.LastAccessTimeUtc)
            .ToList();

        return new WatchingResult(finalCandidates, diagnostics);
    }

    /// <summary>A candidate plus the per-path facts the collision guard needs before finalizing.</summary>
    private sealed record WorkingCandidate(WatchingCandidate Candidate, bool IsPlayerHit, bool BareNameUnambiguous);
}
