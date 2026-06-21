namespace ClipMetaCore.Watching;

/// <summary>The outcome of one resolution pass: the ranked candidates plus any diagnostics.</summary>
/// <param name="Candidates">Ranked watched-clip candidates, best first (may be empty).</param>
/// <param name="Diagnostics">Wrong-directory and related findings (see <see cref="WatchDiagnostics"/>).</param>
public sealed record WatchingResult(IReadOnlyList<WatchingCandidate> Candidates, WatchDiagnostics Diagnostics);
