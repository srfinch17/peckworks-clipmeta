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
        return ResolveCore(context, limit, includeAccessFallback);
    }

    /// <summary>The note attached to a clip bound by the previous-stable correction.</summary>
    private const string CorrectedBindNote =
        "bound the clip you were watching; the player has since advanced (so it is now writable)";

    /// <summary>
    /// Review-mode resolution: pick which recorded title the user is describing (the previous-stable
    /// heuristic over the watcher's segment history), resolve it through the normal pipeline, and
    /// promote that bind. Falls back to a live cold-start poll when there is no segment history or the
    /// situation is ambiguous (two players active). This is what closes the poll-at-call-time binding
    /// race: the bound clip is chosen by WHEN each title played, not by when this method was called.
    /// </summary>
    /// <param name="libraryRoot">Library root to resolve against.</param>
    /// <param name="segments">The watcher's title-segment snapshot.</param>
    /// <param name="lastBoundId">Segment id of the previous recommendation (-1 if none).</param>
    /// <param name="now">Current time (injected for testability).</param>
    /// <param name="limit">Maximum candidates to return.</param>
    /// <param name="includeAccessFallback">Whether to include the access-time fallback.</param>
    /// <param name="spokenAt">
    /// AC2: the moment the user actually dictated, if the caller knows it. When supplied, the clip
    /// whose play window covers this instant is bound exactly (closing the latency/backlog gap the
    /// timing heuristic cannot); absent or unmatched, resolution falls back to the heuristic.
    /// </param>
    public WatchingResult ResolveReview(
        string libraryRoot, IReadOnlyList<TitleSegment> segments, long lastBoundId,
        DateTimeOffset now, int limit, bool includeAccessFallback, DateTimeOffset? spokenAt = null)
    {
        ReviewBinding binding = ReviewBindingResolver.Resolve(
            segments, lastBoundId, now, stableThreshold: null, spokenAt: spokenAt);

        // Which windows to resolve: the single chosen title, else the live windows (cold start /
        // ambiguous) so the existing pipeline produces its normal candidates + diagnostics.
        IReadOnlyList<ProcessWindow> windows = binding.Chosen is { } chosen
            ? new[] { new ProcessWindow(chosen.ProcessName, chosen.RawTitle) }
            : _windowSource.GetPlayerWindows(_playerNames);

        WatchContext context = WatchContext.Build(libraryRoot, windows);
        WatchingResult core = ResolveCore(context, limit, includeAccessFallback);

        List<WatchingCandidate> candidates = core.Candidates.ToList();
        bool confident = false;
        long? boundId = null;

        if (binding.Chosen is { } sel)
        {
            // The chosen title resolves to exactly the candidates the pipeline produced for that
            // window. Promote a single player-title match past the not-locked demotion (it is
            // expected to be unlocked — the user advanced away from it), keeping its true lock state.
            int idx = candidates.FindIndex(c => c.Source == PlayerTitleSignal.SourceName);
            bool singleMatch = candidates.Count(c => c.Source == PlayerTitleSignal.SourceName) == 1;
            if (idx >= 0 && singleMatch)
            {
                candidates[idx] = candidates[idx] with
                {
                    Confidence = HighConfidence,
                    Note = binding.CorrectedFrom is null ? candidates[idx].Note : CorrectedBindNote,
                };
                confident = true;
                boundId = sel.Id;
            }
        }

        // A corrected/confident bind is a live target even when unlocked.
        bool anyLive = core.AnyLiveTarget || confident;

        return new WatchingResult(
            candidates, core.Diagnostics, anyLive, binding.Flags, boundId, confident);
    }

    /// <summary>
    /// Resolves over an already-built context — a live snapshot (<see cref="Resolve"/>) or a single
    /// review-chosen title (<see cref="ResolveReview"/>). Holds the entire scoring/ranking pipeline.
    /// </summary>
    private WatchingResult ResolveCore(WatchContext context, int limit, bool includeAccessFallback)
    {
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

        // #6 — attribute the player for a locked clip whose title we could not resolve. When exactly
        // one open recognized player produced no resolved candidate, it is almost certainly the one
        // holding the lock, so name it rather than returning player:null on a live clip. Two such
        // players is genuinely ambiguous — never guess between them.
        string? soleHolder = SoleUnresolvedPlayer(context, playerMatches);
        if (soleHolder is not null)
            for (int i = 0; i < finalCandidates.Count; i++)
                if (finalCandidates[i].InUse && finalCandidates[i].Player is null)
                    finalCandidates[i] = finalCandidates[i] with { Player = soleHolder, Note = PlayerAttributedNote };

        // #8a — once a clip is positively identified by a high-confidence player hit, the leftover
        // access-time guesses are noise; drop them. With no high winner the fallback is all we have,
        // so keep it.
        if (finalCandidates.Any(c => c.Confidence == HighConfidence))
            finalCandidates = finalCandidates
                .Where(c => c.Source != AccessTimeSignal.SourceName)
                .ToList();

        // #3 — a live target is one a player named OR one currently locked. When false, every
        // candidate is an unverified recency guess and the caller must confirm before tagging.
        bool anyLiveTarget = finalCandidates.Any(
            c => c.Source == PlayerTitleSignal.SourceName || c.InUse);

        return new WatchingResult(finalCandidates, diagnostics, anyLiveTarget);
    }

    /// <summary>The caveat attached to a lock attributed to a player by the open-window heuristic.</summary>
    private const string PlayerAttributedNote =
        "player title not recognized — player attributed from the single open player window";

    /// <summary>
    /// The process name of the one open recognized player that resolved no candidate, or null when
    /// none — or more than one — such player is open (ambiguous, so we attribute nothing).
    /// </summary>
    private static string? SoleUnresolvedPlayer(
        WatchContext context, IReadOnlyList<PlayerMatch> playerMatches)
    {
        var resolvedProcesses = new HashSet<string>(
            playerMatches.Where(m => m.Matches.Count > 0).Select(m => m.Window.ProcessName),
            StringComparer.OrdinalIgnoreCase);

        List<string> openUnresolved = context.PlayerWindows
            .Select(w => w.ProcessName)
            .Where(p => !resolvedProcesses.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return openUnresolved.Count == 1 ? openUnresolved[0] : null;
    }

    /// <summary>A candidate plus the per-path facts the collision guard needs before finalizing.</summary>
    private sealed record WorkingCandidate(WatchingCandidate Candidate, bool IsPlayerHit, bool BareNameUnambiguous);
}
