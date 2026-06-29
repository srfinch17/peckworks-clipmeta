namespace ClipMetaCore.Watching;

/// <summary>
/// Read-only background driver for review-mode tagging. It polls the open media players' window
/// titles on a timer and records a <see cref="TitleSegment"/> each time the active title changes, so
/// <c>library_watching</c> can resolve a tag against what was PLAYING at the user's dictation moment
/// rather than a fresh "what's open now?" snapshot taken a turn later (the binding race). The hot loop
/// only reads titles, no library enumeration, no MP4 work, and never writes a file, so it cannot
/// race any writer. Mirrors the <see cref="QueueDrainPump"/> thread/dispose pattern.
/// </summary>
public sealed class ReviewWatcher : IDisposable
{
    private readonly IProcessWindowSource _windowSource;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _pollInterval;
    private readonly IReadOnlyCollection<string> _playerNames;
    private readonly int _maxSegments;

    private readonly object _gate = new();
    private readonly List<TitleSegment> _segments = new();
    private readonly Dictionary<string, long> _openByProcess = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId = 1;
    private long _lastBoundId = -1;

    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private bool _disposed;

    /// <summary>Creates a watcher over the given OS window source and clock.</summary>
    /// <param name="windowSource">Player-window source (production: <c>ProcessWindowSource.ForCurrentPlatform()</c>).</param>
    /// <param name="clock">Now-provider (injected for tests).</param>
    /// <param name="pollInterval">Time between polls (production: ~250ms).</param>
    /// <param name="playerNames">Recognized players (default <see cref="MediaPlayers.KnownProcessNames"/>).</param>
    /// <param name="maxSegments">Ring-buffer cap; oldest closed segment dropped past this.</param>
    public ReviewWatcher(
        IProcessWindowSource windowSource, Func<DateTimeOffset> clock, TimeSpan pollInterval,
        IReadOnlyCollection<string>? playerNames = null, int maxSegments = 64)
    {
        _windowSource = windowSource ?? throw new ArgumentNullException(nameof(windowSource));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _pollInterval = pollInterval;
        _playerNames = playerNames ?? MediaPlayers.KnownProcessNames;
        _maxSegments = Math.Max(2, maxSegments);
    }

    /// <summary>Id of the segment the last confident resolution recommended, or -1.</summary>
    public long LastBoundId { get { lock (_gate) return _lastBoundId; } }

    /// <summary>Records that <paramref name="segmentId"/> was the last recommended bind.</summary>
    public void MarkBound(long segmentId) { lock (_gate) _lastBoundId = segmentId; }

    /// <summary>A consistent copy of the current segment ring buffer.</summary>
    public IReadOnlyList<TitleSegment> Snapshot() { lock (_gate) return _segments.ToList(); }

    /// <summary>Launches the polling loop. Idempotent.</summary>
    public void Start()
    {
        if (_thread is not null || _disposed) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "clipmeta-review-watcher" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            PollOnce();
            if (_cts.Token.WaitHandle.WaitOne(_pollInterval)) return;
        }
    }

    /// <summary>
    /// One poll: open a new segment for any player whose title changed, close the segment of any
    /// player that vanished or changed title. Never throws, a flaky OS read just skips this tick.
    /// Internal so tests drive it deterministically without the timer.
    /// </summary>
    internal void PollOnce()
    {
        IReadOnlyList<ProcessWindow> windows;
        try { windows = _windowSource.GetPlayerWindows(_playerNames); }
        catch { return; }

        DateTimeOffset now = _clock();
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessWindow w in windows)
            {
                seen.Add(w.ProcessName);
                if (_openByProcess.TryGetValue(w.ProcessName, out long openId))
                {
                    TitleSegment open = _segments.First(s => s.Id == openId);
                    if (string.Equals(open.RawTitle, w.WindowTitle, StringComparison.Ordinal))
                        continue; // unchanged
                    CloseSegment(openId, now);
                }
                OpenSegment(w.ProcessName, w.WindowTitle, now);
            }

            // Players that disappeared close their open segment.
            foreach ((string proc, long id) in _openByProcess.Where(kv => !seen.Contains(kv.Key)).ToList())
                CloseSegment(id, now);

            Trim();
        }
    }

    private void OpenSegment(string proc, string title, DateTimeOffset now)
    {
        long id = _nextId++;
        _segments.Add(new TitleSegment(id, proc, title, now, null));
        _openByProcess[proc] = id;
    }

    private void CloseSegment(long id, DateTimeOffset now)
    {
        int idx = _segments.FindIndex(s => s.Id == id);
        if (idx >= 0) _segments[idx] = _segments[idx] with { EndedAt = now };
        string? proc = _openByProcess.FirstOrDefault(kv => kv.Value == id).Key;
        if (proc is not null) _openByProcess.Remove(proc);
    }

    private void Trim()
    {
        while (_segments.Count > _maxSegments)
        {
            // Never drop a segment that is still open (referenced in _openByProcess).
            int removable = _segments.FindIndex(s => s.EndedAt is not null);
            if (removable < 0) break;
            _segments.RemoveAt(removable);
        }
    }

    /// <summary>Stops the loop and joins the thread. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
