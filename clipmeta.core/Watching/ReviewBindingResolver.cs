namespace ClipMetaCore.Watching;

/// <summary>
/// Pure heuristic that decides which recorded title the user is describing. The core rule: if the
/// currently-open clip only JUST started (under <see cref="DefaultStableThreshold"/>), the user has
/// already advanced and is describing the PREVIOUS clip they actually watched — so bind that instead.
/// This is the entire fix for the poll-at-call-time binding race; it depends only on segment timing,
/// never on when the tool happened to be called.
/// </summary>
public static class ReviewBindingResolver
{
    /// <summary>A clip open for less than this is treated as "just advanced to" — not the subject.</summary>
    public static readonly TimeSpan DefaultStableThreshold = TimeSpan.FromSeconds(2);

    /// <summary>Applies the previous-stable rule and derives review flags from the segment sequence.</summary>
    /// <param name="segments">Title segments, any order (sorted internally by start time).</param>
    /// <param name="lastBoundId">Id of the segment the previous resolution recommended, or -1.</param>
    /// <param name="now">Current time (injected for testability).</param>
    /// <param name="stableThreshold">Override the just-started threshold (tests).</param>
    public static ReviewBinding Resolve(
        IReadOnlyList<TitleSegment> segments, long lastBoundId, DateTimeOffset now,
        TimeSpan? stableThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        TimeSpan threshold = stableThreshold ?? DefaultStableThreshold;

        if (segments.Count == 0)
            return new ReviewBinding(null, null, 0, false, Array.Empty<ReviewFlag>());

        List<TitleSegment> ordered = segments.OrderBy(s => s.StartedAt).ToList();
        TitleSegment current = ordered[^1];

        // Ambiguity: another player produced an OPEN segment within the threshold window of `current`.
        bool multiPlayer = ordered.Any(s =>
            !string.Equals(s.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            (current.StartedAt - s.StartedAt).Duration() <= threshold &&
            s.EndedAt is null);
        if (multiPlayer)
            return new ReviewBinding(
                null, null, 0, true,
                new[]
                {
                    new ReviewFlag(
                        ReviewFlag.TypeMultiplePlayersActive,
                        NamesOf(ordered.Where(s => s.EndedAt is null))),
                });

        // Previous-stable correction.
        TitleSegment chosen = current;
        TitleSegment? correctedFrom = null;
        if (current.DurationSeconds(now) < threshold.TotalSeconds && ordered.Count >= 2)
        {
            TitleSegment prior = ordered[^2];
            if (prior.DurationSeconds(now) >= threshold.TotalSeconds)
            {
                chosen = prior;
                correctedFrom = current;
            }
        }

        var flags = new List<ReviewFlag>();
        double stable = chosen.DurationSeconds(now);
        if (correctedFrom is not null)
            flags.Add(new ReviewFlag(
                ReviewFlag.TypeAutoCorrected, new[] { Display(chosen), Display(correctedFrom) }, stable));

        if (chosen.Id == lastBoundId)
            flags.Add(new ReviewFlag(ReviewFlag.TypeSameClipTwice, new[] { Display(chosen) }));

        // Skip: stable, never-bound segments strictly between the last bind and the chosen one.
        if (lastBoundId >= 0)
        {
            List<string> skipped = ordered
                .Where(s => s.Id > lastBoundId && s.Id < chosen.Id &&
                            s.DurationSeconds(now) >= threshold.TotalSeconds)
                .Select(Display).ToList();
            if (skipped.Count > 0)
                flags.Add(new ReviewFlag(ReviewFlag.TypeSequenceSkip, skipped));
        }

        return new ReviewBinding(chosen, correctedFrom, stable, false, flags);
    }

    private static IReadOnlyList<string> NamesOf(IEnumerable<TitleSegment> segs) =>
        segs.Select(Display).ToList();

    /// <summary>Best display string for a segment — its raw title (which contains the file name).</summary>
    private static string Display(TitleSegment s) => s.RawTitle;
}
