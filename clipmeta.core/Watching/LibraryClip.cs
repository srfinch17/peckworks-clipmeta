namespace ClipMetaCore.Watching;

/// <summary>A clip enumerated from the library, with the facts resolution needs.</summary>
/// <param name="FullPath">Absolute path to the .mp4 file.</param>
/// <param name="FileName">File name only (for bare-title matching).</param>
/// <param name="LastAccessTimeUtc">Last-access time at enumeration.</param>
/// <param name="LastWriteTimeUtc">
/// Last-write time at enumeration. Unlike access time, it is NOT bumped by merely playing a clip,
/// so it cleanly identifies a clip the game just saved (gaming mode — see <see cref="RecentWriteSignal"/>).
/// </param>
public sealed record LibraryClip(
    string FullPath, string FileName, DateTime LastAccessTimeUtc, DateTime LastWriteTimeUtc);
