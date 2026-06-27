namespace ClipMetaCore.Watching;

/// <summary>
/// Rewrites review-flag clip strings (raw player-window titles) into clean, deduped library
/// basenames using the same library-aware matcher the resolver uses, so advisories never expose
/// raw titles, OSD/timecode text, or duplicate entries. Pure: no IO — library identity comes from
/// the supplied <see cref="WatchContext"/>. Flag <see cref="ReviewFlag.Type"/> and
/// <see cref="ReviewFlag.StableSeconds"/> are untouched; only the clip payload changes.
/// </summary>
public static class ReviewFlagResolver
{
    /// <summary>
    /// Returns flags whose <c>Clips</c> are each resolved to a library basename via
    /// <see cref="LibraryTitleMatcher.FindBestMatch"/>, with unresolvable entries (foreign files,
    /// bare process names) dropped and the remainder deduped (OrdinalIgnoreCase, first-seen order).
    /// </summary>
    public static IReadOnlyList<ReviewFlag> Resolve(
        IReadOnlyList<ReviewFlag> flags, WatchContext context)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(context);

        var result = new List<ReviewFlag>(flags.Count);
        foreach (ReviewFlag flag in flags)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clips = new List<string>();
            foreach (string raw in flag.Clips)
            {
                string? name = LibraryTitleMatcher.FindBestMatch(raw, context.ByFileName.Keys);
                if (name is not null && seen.Add(name))
                    clips.Add(name);
            }
            result.Add(flag with { Clips = clips });
        }
        return result;
    }
}
