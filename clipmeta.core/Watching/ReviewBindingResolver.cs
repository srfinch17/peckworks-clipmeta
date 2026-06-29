namespace ClipMetaCore.Watching;

/// <summary>
/// Pure heuristic that decides which recorded title the user is describing. The core rule: if the
/// currently-open clip only JUST started (under <see cref="DefaultStableThreshold"/>), the user has
/// already advanced and is describing the PREVIOUS clip they actually watched, so bind that instead.
/// This is the entire fix for the poll-at-call-time binding race; it depends only on segment timing,
/// never on when the tool happened to be called.
/// </summary>
public static class ReviewBindingResolver
{
    /// <summary>A clip open for less than this is treated as "just advanced to", not the subject.</summary>
    public static readonly TimeSpan DefaultStableThreshold = TimeSpan.FromSeconds(2);

    /// <summary>Applies the previous-stable rule and derives review flags from the segment sequence.</summary>
    /// <param name="segments">Title segments, any order (sorted internally by start time).</param>
    /// <param name="lastBoundId">Id of the segment the previous resolution recommended, or -1.</param>
    /// <param name="now">Current time (injected for testability).</param>
    /// <param name="stableThreshold">Override the just-started threshold (tests).</param>
    /// <param name="spokenAt">
    /// AC2: when supplied, bind the segment whose play window covers this instant (the moment the user
    /// actually dictated), bypassing the timing heuristic for an exact hit. Falls back to the heuristic
    ///, flagged <see cref="ReviewFlag.TypeTimestampUnmatched"/>, when no segment covers it.
    /// </param>
    public static ReviewBinding Resolve(
        IReadOnlyList<TitleSegment> segments, long lastBoundId, DateTimeOffset now,
        TimeSpan? stableThreshold = null, DateTimeOffset? spokenAt = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        TimeSpan threshold = stableThreshold ?? DefaultStableThreshold;

        if (segments.Count == 0)
            return new ReviewBinding(null, null, 0, false, Array.Empty<ReviewFlag>());

        List<TitleSegment> ordered = segments.OrderBy(s => s.StartedAt).ToList();

        // AC2: an exact spoken-at lookup takes precedence over the timing heuristic.
        if (spokenAt is { } at)
        {
            List<TitleSegment> covering = ordered
                .Where(s => s.StartedAt <= at && at < (s.EndedAt ?? now))
                .ToList();

            int coveringPlayers = covering
                .Select(s => s.ProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (coveringPlayers > 1)
                return new ReviewBinding(
                    null, null, 0, true,
                    new[] { new ReviewFlag(ReviewFlag.TypeMultiplePlayersActive, NamesOf(covering)) });

            if (covering.Count >= 1)
            {
                TitleSegment hit = covering[^1]; // newest covering segment (ordered by start)
                return new ReviewBinding(
                    hit, null, hit.DurationSeconds(now), false,
                    DeriveSequenceFlags(ordered, hit, lastBoundId, now, threshold).ToList());
            }

            // No segment covers the spoken instant (aged out, or a gap): best-effort heuristic, but
            // tell the caller the exact lookup missed so it confirms before tagging.
            ReviewBinding fallback = ComputeHeuristic(ordered, lastBoundId, now, threshold);
            return fallback with
            {
                Flags = fallback.Flags
                    .Append(new ReviewFlag(ReviewFlag.TypeTimestampUnmatched, Array.Empty<string>()))
                    .ToList(),
            };
        }

        return ComputeHeuristic(ordered, lastBoundId, now, threshold);
    }

    /// <summary>The previous-stable timing heuristic over an already-start-sorted segment list.</summary>
    private static ReviewBinding ComputeHeuristic(
        List<TitleSegment> ordered, long lastBoundId, DateTimeOffset now, TimeSpan threshold)
    {
        TitleSegment current = ordered[^1];

        // Ambiguity (#2): two or more distinct players currently have an OPEN segment. Any such
        // overlap is too ambiguous to bind, independent of when each started (the old near-
        // simultaneous-start rule missed players opened seconds apart, the common case).
        List<TitleSegment> openSegments = ordered.Where(s => s.EndedAt is null).ToList();
        int openPlayers = openSegments
            .Select(s => s.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (openPlayers > 1)
            return new ReviewBinding(
                null, null, 0, true,
                new[] { new ReviewFlag(ReviewFlag.TypeMultiplePlayersActive, NamesOf(openSegments)) });

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
        flags.AddRange(DeriveSequenceFlags(ordered, chosen, lastBoundId, now, threshold));

        return new ReviewBinding(chosen, correctedFrom, stable, false, flags);
    }

    /// <summary>
    /// Same-clip-twice and sequence-skip advisories, derived from where <paramref name="chosen"/> sits
    /// relative to the previous bind. Shared by the heuristic and the exact spoken-at path.
    /// </summary>
    private static IEnumerable<ReviewFlag> DeriveSequenceFlags(
        List<TitleSegment> ordered, TitleSegment chosen, long lastBoundId,
        DateTimeOffset now, TimeSpan threshold)
    {
        if (chosen.Id == lastBoundId)
            yield return new ReviewFlag(ReviewFlag.TypeSameClipTwice, new[] { Display(chosen) });

        // Skip: stable, never-bound segments strictly between the last bind and the chosen one.
        if (lastBoundId >= 0)
        {
            List<string> skipped = ordered
                .Where(s => s.Id > lastBoundId && s.Id < chosen.Id &&
                            s.DurationSeconds(now) >= threshold.TotalSeconds)
                .Select(Display).ToList();
            if (skipped.Count > 0)
                yield return new ReviewFlag(ReviewFlag.TypeSequenceSkip, skipped);
        }
    }

    private static IReadOnlyList<string> NamesOf(IEnumerable<TitleSegment> segs) =>
        segs.Select(Display).ToList();

    /// <summary>Best display string for a segment, its raw title (which contains the file name).</summary>
    private static string Display(TitleSegment s) => s.RawTitle;
}
