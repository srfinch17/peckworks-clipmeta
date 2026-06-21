namespace ClipMetaCore.Watching;

/// <summary>
/// One signal's evidence that a particular clip is the one being watched. Several signals may emit
/// a hit for the same clip; the resolver groups hits by path and scores confidence by corroboration.
/// </summary>
/// <param name="ClipPath">Path of an enumerated library clip — never a fabricated path.</param>
/// <param name="Source">The emitting signal's name (also used as the candidate source).</param>
/// <param name="Player">Process name when the evidence came from a player; otherwise null.</param>
/// <param name="Ambiguous">True when this signal alone could not disambiguate the clip.</param>
/// <param name="MatchKind">
/// For a player-title hit, whether the player named a full path or a bare file name; null for
/// non-player signals. The resolver applies the lock-based collision guard to bare-name hits only.
/// </param>
public sealed record SignalHit(
    string ClipPath, string Source, string? Player, bool Ambiguous,
    TitleExtractionKind? MatchKind = null);
