using ClipMetaCore.Read;

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

    /// <summary>Paths already known to the library (from the persisted index). Empty when no index.</summary>
    public IReadOnlySet<string> KnownBaselinePaths { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Session self-action ledger (paths ClipMeta wrote/read), or null when not tracked.</summary>
    public SelfActionLedger? Ledger { get; init; }

    /// <summary>
    /// Enumerates <paramref name="libraryRoot"/> for .mp4 files (recursive), builds the lookups,
    /// and captures the player-window snapshot from <paramref name="source"/>. Files whose access
    /// time cannot be read are skipped (a vanished/locked file must not abort the whole pass).
    /// </summary>
    public static WatchContext Build(
        string libraryRoot, IProcessWindowSource source,
        IReadOnlyCollection<string> playerNames, SelfActionLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Build(libraryRoot, source.GetPlayerWindows(playerNames), ledger);
    }

    /// <summary>
    /// Builds a context over supplied player windows instead of polling a source — used by review-mode
    /// resolution, which has already chosen WHICH title to resolve from the watcher's segment history.
    /// </summary>
    public static WatchContext Build(
        string libraryRoot, IReadOnlyList<ProcessWindow> playerWindows, SelfActionLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(playerWindows);
        (List<LibraryClip> clips, var byName, var byPath) = EnumerateLibrary(libraryRoot);
        return new WatchContext
        {
            LibraryClips = clips,
            ByFileName = byName,
            ByFullPath = byPath,
            PlayerWindows = playerWindows,
            KnownBaselinePaths = LoadBaseline(libraryRoot),
            Ledger = ledger,
        };
    }

    /// <summary>
    /// Enumerates <paramref name="libraryRoot"/> for .mp4 files (recursive) and builds the name/path
    /// lookups. Files whose access time cannot be read are skipped (a vanished/locked file must not
    /// abort the whole pass). Shared by both <see cref="Build(string, IProcessWindowSource, IReadOnlyCollection{string}, SelfActionLedger?)"/>
    /// and the supplied-windows overload.
    /// </summary>
    private static (List<LibraryClip> Clips,
                    IReadOnlyDictionary<string, IReadOnlyList<LibraryClip>> ByName,
                    IReadOnlyDictionary<string, LibraryClip> ByPath)
        EnumerateLibrary(string libraryRoot)
    {
        var clips = new List<LibraryClip>();
        foreach (string path in Directory.EnumerateFiles(libraryRoot, "*.mp4", SearchOption.AllDirectories))
        {
            DateTime accessTime, writeTime, creationTime;
            try
            {
                accessTime = File.GetLastAccessTimeUtc(path);
                writeTime = File.GetLastWriteTimeUtc(path);
                creationTime = File.GetCreationTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            clips.Add(new LibraryClip(path, Path.GetFileName(path), accessTime, writeTime, creationTime));
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

        return (
            clips,
            byName.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<LibraryClip>)kv.Value, StringComparer.OrdinalIgnoreCase),
            byPath);
    }

    /// <summary>
    /// Loads the set of paths the persisted index already knows. A missing/unreadable index yields an
    /// empty set (so a not-yet-indexed library degrades to creation-time + ledger novelty, never throws).
    /// </summary>
    private static IReadOnlySet<string> LoadBaseline(string libraryRoot)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string indexPath = Path.Combine(libraryRoot, ClipMetaIndex.IndexFileName);
            if (!File.Exists(indexPath))
                return known;
            foreach (IndexEntry entry in ClipMetaIndex.ReadFromFile(indexPath).Entries)
                known.Add(entry.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Treat a corrupt/locked index as "no baseline" — never let it abort a resolution pass.
        }
        return known;
    }
}
