namespace ClipMetaCore.Watching;

/// <summary>
/// One signal's evidence that a particular clip is the one being watched. Several signals may emit
/// a hit for the same clip; the resolver groups hits by path and scores confidence by corroboration.
/// </summary>
/// <param name="ClipPath">Path of an enumerated library clip — never a fabricated path.</param>
/// <param name="Source">The emitting signal's name (also used as the candidate source).</param>
/// <param name="Player">Process name when the evidence came from a player; otherwise null.</param>
/// <param name="Ambiguous">True when this signal alone could not disambiguate the clip.</param>
public sealed record SignalHit(string ClipPath, string Source, string? Player, bool Ambiguous);
