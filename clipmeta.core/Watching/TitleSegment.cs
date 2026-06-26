namespace ClipMetaCore.Watching;

/// <summary>
/// One uninterrupted span during which a media player showed a single window title. The watcher
/// records these over time so a tag can bind to the title that was actually playing at the user's
/// dictation moment, not whatever the player advanced to by the time the tool runs.
/// </summary>
/// <param name="Id">Monotonic id assigned when the segment opens (enables cross-call bind tracking).</param>
/// <param name="ProcessName">The player process the title came from.</param>
/// <param name="RawTitle">The raw window title (resolved to a clip later, at call time).</param>
/// <param name="StartedAt">When this title first appeared.</param>
/// <param name="EndedAt">When it changed/closed; null while it is still the current title.</param>
public sealed record TitleSegment(
    long Id, string ProcessName, string RawTitle, DateTimeOffset StartedAt, DateTimeOffset? EndedAt)
{
    /// <summary>How long this segment played, measured to <paramref name="now"/> when still open.</summary>
    public double DurationSeconds(DateTimeOffset now) =>
        Math.Max(0, ((EndedAt ?? now) - StartedAt).TotalSeconds);
}
