namespace ClipMetaCore.Watching;

/// <summary>Whether ClipMeta wrote a file or merely read it.</summary>
public enum SelfTouchKind
{
    /// <summary>ClipMeta opened the file's content (export / get-metadata).</summary>
    Read,
    /// <summary>ClipMeta wrote metadata into the file.</summary>
    Written,
}

/// <summary>
/// Process-wide record of the clips ClipMeta itself touched this session, so signals keyed on raw
/// filesystem timestamps can subtract self-actions: a clip we just wrote is not a fresh user "save",
/// and a clip we just read is not a clip the user just "watched". In-memory and session-scoped — a
/// restart is a new session. Thread-safe: the queue-drain pump thread and request threads share it.
/// </summary>
public sealed class SelfActionLedger
{
    /// <summary>How long a self-action masks a path (also the prune horizon).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, SelfTouchKind Kind)> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates a ledger. <paramref name="clock"/> is injected in tests; defaults to system UTC.</summary>
    public SelfActionLedger(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Records that ClipMeta wrote <paramref name="path"/> just now.</summary>
    public void MarkWritten(string path) => Mark(path, SelfTouchKind.Written);

    /// <summary>Records that ClipMeta read <paramref name="path"/>'s content just now.</summary>
    public void MarkRead(string path) => Mark(path, SelfTouchKind.Read);

    private void Mark(string path, SelfTouchKind kind)
    {
        lock (_gate)
        {
            // A fresh write outranks a later read: don't let a diagnostic read of a clip we just
            // tagged downgrade it to "merely read".
            if (kind == SelfTouchKind.Read &&
                _entries.TryGetValue(path, out var e) && e.Kind == SelfTouchKind.Written &&
                _clock() - e.At <= DefaultWindow)
                return;

            _entries[path] = (_clock(), kind);
            Prune();
        }
    }

    /// <summary>True if ClipMeta WROTE <paramref name="path"/> within <paramref name="window"/> of now.</summary>
    public bool WasWrittenWithin(string path, TimeSpan window, DateTimeOffset now)
    {
        lock (_gate)
            return _entries.TryGetValue(path, out var e) &&
                   e.Kind == SelfTouchKind.Written && now - e.At <= window;
    }

    /// <summary>True if ClipMeta touched (read OR wrote) <paramref name="path"/> within the window.</summary>
    public bool WasTouchedWithin(string path, TimeSpan window, DateTimeOffset now)
    {
        lock (_gate)
            return _entries.TryGetValue(path, out var e) && now - e.At <= window;
    }

    /// <summary>Drops entries older than <see cref="DefaultWindow"/> so the ledger never grows unbounded. Caller holds the lock.</summary>
    private void Prune()
    {
        DateTimeOffset cutoff = _clock() - DefaultWindow;
        foreach (string key in _entries.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
            _entries.Remove(key);
    }
}
