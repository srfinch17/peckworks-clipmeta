namespace ClipMetaCore.Watching;

/// <summary>The outcome of one resolution pass: the ranked candidates plus any diagnostics.</summary>
/// <param name="Candidates">Ranked watched-clip candidates, best first (may be empty).</param>
/// <param name="Diagnostics">Wrong-directory and related findings (see <see cref="WatchDiagnostics"/>).</param>
/// <param name="AnyLiveTarget">
/// True when at least one returned candidate is genuinely live — it has a player-title hit, its lock
/// probe reported in-use, OR it is a history-confirmed review-mode corrected bind. False means every
/// candidate is an unverified recency guess (e.g. the access-time fallback with nothing open):
/// callers must NOT auto-tag without confirming the path.
/// </param>
/// <param name="Review">
/// Non-blocking review-mode advisories (auto-correction, same-clip-twice, skip, multi-player) for the
/// caller to mention to the user and reconcile later. Null/empty outside review mode.
/// </param>
/// <param name="BoundSegmentId">
/// The watcher segment id the top recommendation resolved from, for the shell to <c>MarkBound</c>.
/// Null when there was no confident single-match recommendation.
/// </param>
/// <param name="RecommendationConfident">
/// True when review-mode resolution produced a single unambiguous recommended clip (the shell marks
/// it bound and may treat it as the tag target).
/// </param>
public sealed record WatchingResult(
    IReadOnlyList<WatchingCandidate> Candidates,
    WatchDiagnostics Diagnostics,
    bool AnyLiveTarget,
    IReadOnlyList<ReviewFlag>? Review = null,
    long? BoundSegmentId = null,
    bool RecommendationConfident = false);
