namespace ClipMetaCore.Watching;

/// <summary>
/// One pluggable confidence signal. Adding a new player or detection method means adding a new
/// implementation and registering it — never editing the resolver.
/// </summary>
public interface IWatchSignal
{
    /// <summary>Stable identifier, also used as <see cref="SignalHit.Source"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Emits zero or more evidence hits for the current moment. MUST only reference clips present
    /// in <see cref="WatchContext.LibraryClips"/> — a signal selects among already-enumerated clips,
    /// it never constructs a path. MUST NOT throw for ordinary failures (player closed, file gone,
    /// source unreadable): emit nothing instead.
    /// </summary>
    IEnumerable<SignalHit> Detect(WatchContext context);
}
