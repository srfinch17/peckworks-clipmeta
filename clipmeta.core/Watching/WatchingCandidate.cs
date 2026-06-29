namespace ClipMetaCore.Watching;

/// <summary>One ranked watched-clip candidate.</summary>
/// <param name="Path">Absolute path to the candidate clip (always a library clip).</param>
/// <param name="Name">File name only.</param>
/// <param name="Source">
/// Dominant evidence source: "player_title" (an open player named it), "recent_write" (a clip just
/// saved to the library while no player was open, gaming mode), or "access_time" (recency fallback).
/// </param>
/// <param name="Player">Process name when a player named it; otherwise null.</param>
/// <param name="LastAccessTimeUtc">Last-access time at enumeration.</param>
/// <param name="SecondsSinceAccess">Seconds between enumeration and the last access (≥ 0).</param>
/// <param name="InUse">True when the file currently has an exclusive-denying open handle.</param>
/// <param name="Confidence">"high" only for a single unambiguous player hit; otherwise "low".</param>
/// <param name="Note">
/// Optional human-readable caveat (e.g. a not-locked bare-name match the agent should confirm
/// before tagging). Null when there is nothing to flag.
/// </param>
public sealed record WatchingCandidate(
    string Path,
    string Name,
    string Source,
    string? Player,
    DateTime LastAccessTimeUtc,
    double SecondsSinceAccess,
    bool InUse,
    string Confidence,
    string? Note = null);
