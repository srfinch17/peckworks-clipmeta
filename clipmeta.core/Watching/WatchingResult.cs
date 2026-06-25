namespace ClipMetaCore.Watching;

/// <summary>The outcome of one resolution pass: the ranked candidates plus any diagnostics.</summary>
/// <param name="Candidates">Ranked watched-clip candidates, best first (may be empty).</param>
/// <param name="Diagnostics">Wrong-directory and related findings (see <see cref="WatchDiagnostics"/>).</param>
/// <param name="AnyLiveTarget">
/// True when at least one returned candidate is genuinely live — it has a player-title hit OR its
/// lock probe reported in-use. False means every candidate is an unverified recency guess (e.g. the
/// access-time fallback with nothing open): callers must NOT auto-tag without confirming the path.
/// </param>
public sealed record WatchingResult(
    IReadOnlyList<WatchingCandidate> Candidates, WatchDiagnostics Diagnostics, bool AnyLiveTarget);
