namespace ClipMetaCore.Watching;

/// <summary>One tag the background pump auto-flushed: the clip, the fields it changed, and when.</summary>
/// <param name="Path">Clip whose queued tag was written.</param>
/// <param name="Fields">User-facing names of the fields the write changed.</param>
/// <param name="WhenUtc">When the auto-flush landed.</param>
public sealed record DrainedTag(string Path, IReadOnlyList<string> Fields, DateTimeOffset WhenUtc);
