namespace ClipMetaCore.Watching;

/// <summary>
/// The pure heuristic's decision about which recorded title the user is describing. <see cref="Chosen"/>
/// null means "no correction to offer" (cold start, or ambiguous multi-player) — the caller falls back
/// to a live poll.
/// </summary>
/// <param name="Chosen">Segment whose title to resolve to a clip, or null.</param>
/// <param name="CorrectedFrom">Set when the previous-stable segment was chosen over a just-started one.</param>
/// <param name="StableSeconds">How long <see cref="Chosen"/> played (0 when null).</param>
/// <param name="AmbiguousMultiPlayer">True when 2+ players were active — refuse correction, warn.</param>
/// <param name="Flags">Review advisories derived from the segment sequence.</param>
public sealed record ReviewBinding(
    TitleSegment? Chosen,
    TitleSegment? CorrectedFrom,
    double StableSeconds,
    bool AmbiguousMultiPlayer,
    IReadOnlyList<ReviewFlag> Flags);
