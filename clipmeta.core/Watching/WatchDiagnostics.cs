namespace ClipMetaCore.Watching;

/// <summary>A media player open on a file that is NOT in the configured library.</summary>
/// <param name="Player">Process name of the player.</param>
/// <param name="ReferencedName">The file the title named (full path or bare name).</param>
/// <param name="ForeignDirectory">
/// The folder the player is reading from — populated ONLY when the title gave a full path (MPC).
/// Null for bare-name titles: we genuinely do not know where the file is, and will not search.
/// </param>
public sealed record UnresolvedPlayer(string Player, string ReferencedName, string? ForeignDirectory);

/// <summary>Side-band findings from a resolution pass, beyond the ranked candidates.</summary>
/// <param name="UnresolvedPlayers">
/// Players open on files outside the library. Non-empty means "you may be playing from the wrong
/// folder" — surfaces should warn and not tag.
/// </param>
public sealed record WatchDiagnostics(IReadOnlyList<UnresolvedPlayer> UnresolvedPlayers);
