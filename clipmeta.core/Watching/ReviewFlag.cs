namespace ClipMetaCore.Watching;

/// <summary>
/// A non-blocking advisory about a binding the user may want to reconcile later: an auto-correction,
/// a clip tagged twice, a skipped clip, or too many players open to bind safely. Surfaced inline on
/// the library_watching response; never interrupts the run.
/// </summary>
/// <param name="Type">One of the <c>Type*</c> constants.</param>
/// <param name="Clips">Display names/titles the flag refers to (e.g. the bound + corrected-from clip).</param>
/// <param name="StableSeconds">For <see cref="TypeAutoCorrected"/>: how long the bound clip had played.</param>
public sealed record ReviewFlag(string Type, IReadOnlyList<string> Clips, double StableSeconds = 0)
{
    /// <summary>Bound the previous stable clip because the open one had only just started.</summary>
    public const string TypeAutoCorrected = "autoCorrected";

    /// <summary>This resolution targets the same clip the previous one did (player did not advance).</summary>
    public const string TypeSameClipTwice = "sameClipTwice";

    /// <summary>Stable clips played between the last bind and this one were never tagged.</summary>
    public const string TypeSequenceSkip = "sequenceSkip";

    /// <summary>More than one player is active — too ambiguous to bind a clip safely.</summary>
    public const string TypeMultiplePlayersActive = "multiplePlayersActive";

    /// <summary>
    /// A <c>spoken_at</c> timestamp was supplied but matched no recorded segment (it aged out of
    /// history, or fell in a gap when no player was open), so the binding is the heuristic's best
    /// guess rather than an exact hit — confirm before tagging.
    /// </summary>
    public const string TypeTimestampUnmatched = "timestampUnmatched";
}
