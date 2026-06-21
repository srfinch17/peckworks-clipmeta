namespace ClipMetaCore.Watching;

/// <summary>
/// Shared inputs for one resolution pass. The library is enumerated exactly once here so signals
/// don't each re-scan it, and the lookups make title→clip resolution O(1).
/// </summary>
public sealed class WatchContext
{
    /// <summary>Every clip under the library root, enumerated once.</summary>
    public required IReadOnlyList<LibraryClip> LibraryClips { get; init; }

    /// <summary>File name → clip(s), for resolving a bare title filename (case-insensitive).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<LibraryClip>> ByFileName { get; init; }

    /// <summary>Full path → clip, for validating a full-path title (case-insensitive).</summary>
    public required IReadOnlyDictionary<string, LibraryClip> ByFullPath { get; init; }

    /// <summary>Window titles of running players (empty on non-Windows / when none run).</summary>
    public required IReadOnlyList<ProcessWindow> PlayerWindows { get; init; }

    /// <summary>
    /// Enumerates <paramref name="libraryRoot"/> for .mp4 files (recursive), builds the lookups,
    /// and captures the player-window snapshot from <paramref name="source"/>. Files whose access
    /// time cannot be read are skipped (a vanished/locked file must not abort the whole pass).
    /// </summary>
    public static WatchContext Build(
        string libraryRoot, IProcessWindowSource source, IReadOnlyCollection<string> playerNames)
    {
        var clips = new List<LibraryClip>();
        foreach (string path in Directory.EnumerateFiles(libraryRoot, "*.mp4", SearchOption.AllDirectories))
        {
            DateTime accessTime;
            try
            {
                accessTime = File.GetLastAccessTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            clips.Add(new LibraryClip(path, Path.GetFileName(path), accessTime));
        }

        var byName = new Dictionary<string, List<LibraryClip>>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, LibraryClip>(StringComparer.OrdinalIgnoreCase);
        foreach (LibraryClip clip in clips)
        {
            if (!byName.TryGetValue(clip.FileName, out List<LibraryClip>? list))
                byName[clip.FileName] = list = new List<LibraryClip>();
            list.Add(clip);
            byPath[clip.FullPath] = clip;
        }

        return new WatchContext
        {
            LibraryClips = clips,
            ByFileName = byName.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<LibraryClip>)kv.Value, StringComparer.OrdinalIgnoreCase),
            ByFullPath = byPath,
            PlayerWindows = source.GetPlayerWindows(playerNames),
        };
    }
}
