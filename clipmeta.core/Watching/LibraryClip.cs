namespace ClipMetaCore.Watching;

/// <summary>A clip enumerated from the library, with the facts resolution needs.</summary>
/// <param name="FullPath">Absolute path to the .mp4 file.</param>
/// <param name="FileName">File name only (for bare-title matching).</param>
/// <param name="LastAccessTimeUtc">Last-access time at enumeration.</param>
/// <param name="LastWriteTimeUtc">
/// Last-write time at enumeration. NOT bumped by merely playing a clip.
/// </param>
/// <param name="CreationTimeUtc">
/// NTFS creation time at enumeration. Set fresh when a file appears in a directory even when a copy
/// preserves the source's write time, so it — not write time — identifies a genuinely new clip
/// (gaming mode; see <see cref="RecentWriteSignal"/>).
/// </param>
public sealed record LibraryClip(
    string FullPath, string FileName,
    DateTime LastAccessTimeUtc, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);
