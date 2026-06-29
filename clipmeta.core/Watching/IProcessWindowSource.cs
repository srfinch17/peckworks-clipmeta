namespace ClipMetaCore.Watching;

/// <summary>
/// Supplies the window titles of currently-running media players. The one dependency that cannot
/// run on clip-less CI (live process inspection is Windows-only), so it is isolated behind this
/// interface and faked in tests.
/// </summary>
public interface IProcessWindowSource
{
    /// <summary>
    /// Returns one <see cref="ProcessWindow"/> per running process whose name matches one of
    /// <paramref name="processNames"/> (case-insensitive) and has a non-empty main-window title.
    /// Implementations MUST NOT throw for a single inaccessible or exited process, skip and
    /// continue.
    /// </summary>
    IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> processNames);
}
