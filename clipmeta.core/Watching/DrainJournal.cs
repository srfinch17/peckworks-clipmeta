namespace ClipMetaCore.Watching;

/// <summary>
/// Report-once buffer of tags the background <see cref="QueueDrainPump"/> auto-flushed. The pump
/// writes the last clip the instant its player closes but reports to no caller; this lets the next
/// library_watching / library_flush_queue / library_queue_status surface "your queued tag landed".
/// <see cref="TakePending"/> returns and CLEARS, so each auto-flush is reported exactly once.
/// Thread-safe: the pump thread records, request threads take.
/// </summary>
public sealed class DrainJournal
{
    /// <summary>Most recent entries kept; older ones are dropped if never taken (no unbounded growth).</summary>
    private const int Cap = 50;

    private readonly object _gate = new();
    private readonly List<DrainedTag> _pending = new();

    /// <summary>Appends an auto-flushed tag, dropping the oldest beyond <see cref="Cap"/>.</summary>
    public void Record(DrainedTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        lock (_gate)
        {
            _pending.Add(tag);
            if (_pending.Count > Cap)
                _pending.RemoveRange(0, _pending.Count - Cap);
        }
    }

    /// <summary>Returns all pending auto-flushes (oldest first) and clears the buffer.</summary>
    public IReadOnlyList<DrainedTag> TakePending()
    {
        lock (_gate)
        {
            var copy = _pending.ToList();
            _pending.Clear();
            return copy;
        }
    }
}
