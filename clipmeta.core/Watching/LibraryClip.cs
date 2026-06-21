namespace ClipMetaCore.Watching;

/// <summary>A clip enumerated from the library, with the facts resolution needs.</summary>
/// <param name="FullPath">Absolute path to the .mp4 file.</param>
/// <param name="FileName">File name only (for bare-title matching).</param>
/// <param name="LastAccessTimeUtc">Last-access time at enumeration.</param>
public sealed record LibraryClip(string FullPath, string FileName, DateTime LastAccessTimeUtc);
