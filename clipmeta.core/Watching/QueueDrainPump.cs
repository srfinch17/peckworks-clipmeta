using ClipMetaCore.Abstractions;

namespace ClipMetaCore.Watching;

/// <summary>
/// Background driver that makes the deferred-tag queue zero-touch: while the queue holds entries
/// whose clips are still locked, it polls their lock state and drains each one the moment its lock
/// clears — so the LAST clip of a session lands with no explicit flush when the player closes. It
/// idles (zero CPU) on an event when the queue is empty, woken by each enqueue.
/// <para>
/// Why a poll and not a FileSystemWatcher / process-exit hook: a player RELEASING its file handle
/// is not a filesystem event (FSW would watch the wrong thing), and exit hooks are racy against
/// playlists and multiple players. Polling the lock of the small queued set is the right signal,
/// active only while work is pending.
/// </para>
/// <para>
/// Durability is the queue's job, not the pump's: if the host kills the process before a lock
/// clears, the tag stays in <c>.clipmeta-queue</c> and drains on the next session. The pump only
/// lowers latency. Every drain runs inside the injected <c>runExclusive</c> section (the MCP
/// <c>WriteGate</c> in production) so it can never race a direct write at <c>File.Replace</c>.
/// </para>
/// </summary>
public sealed class QueueDrainPump : IDisposable
{
    private readonly string _libraryRoot;
    private readonly IMediaWriter _writer;
    private readonly IClipMetaLogger _logger;
    private readonly Func<string, bool> _isInUse;
    private readonly Action<Action> _runExclusive;
    private readonly TimeSpan _pollInterval;

    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private bool _disposed;

    /// <summary>Creates a pump over the given queue location, write engine, and serialization seam.</summary>
    /// <param name="libraryRoot">Library root holding the <c>.clipmeta-queue</c> file.</param>
    /// <param name="writer">Write engine used to land drained tags (production: <c>new Mp4Writer()</c>).</param>
    /// <param name="logger">Logger for drain errors (never thrown out of the loop).</param>
    /// <param name="isInUse">Lock predicate (production: <c>LockProbe.IsInUse</c>).</param>
    /// <param name="runExclusive">
    /// Runs a drain inside the process-wide write single-flight (production: wraps the MCP WriteGate).
    /// </param>
    /// <param name="pollInterval">How long to wait between drains while a queued clip stays locked.</param>
    public QueueDrainPump(
        string libraryRoot, IMediaWriter writer, IClipMetaLogger logger,
        Func<string, bool> isInUse, Action<Action> runExclusive, TimeSpan pollInterval)
    {
        _libraryRoot = libraryRoot ?? throw new ArgumentNullException(nameof(libraryRoot));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isInUse = isInUse ?? throw new ArgumentNullException(nameof(isInUse));
        _runExclusive = runExclusive ?? throw new ArgumentNullException(nameof(runExclusive));
        _pollInterval = pollInterval;
    }

    /// <summary>Starts the background loop (idle until <see cref="Wake"/>). Idempotent.</summary>
    public void Start()
    {
        if (_thread is not null || _disposed)
            return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "clipmeta-queue-drain-pump" };
        _thread.Start();
    }

    /// <summary>Signals that the queue may have work — call after every enqueue.</summary>
    public void Wake()
    {
        if (!_disposed)
            _wake.Set();
    }

    private void Loop()
    {
        WaitHandle[] waits = { _wake, _cts.Token.WaitHandle };
        while (!_cts.IsCancellationRequested)
        {
            WaitHandle.WaitAny(waits);
            if (_cts.IsCancellationRequested)
                return;

            // Drain repeatedly until nothing locked remains, waiting the poll interval between
            // passes so a clip that is still playing lands as soon as its lock clears.
            while (!_cts.IsCancellationRequested)
            {
                DrainReport report;
                try
                {
                    report = DrainOnce();
                }
                catch (Exception ex)
                {
                    // A drain must never crash the loop (or the host). Log and fall back to idle;
                    // the next enqueue (or the durable queue on a later session) retries.
                    _logger.Log($"queue drain pump: drain failed, will retry on next wake: {ex}");
                    break;
                }

                if (report.StillQueued.Count == 0)
                    break; // nothing left locked — go idle until the next wake

                if (_cts.Token.WaitHandle.WaitOne(_pollInterval))
                    return; // cancelled during the wait
            }
        }
    }

    private DrainReport DrainOnce()
    {
        DrainReport result = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        _runExclusive(() => result = TagQueue.Drain(_libraryRoot, _writer, _logger, _isInUse));
        return result;
    }

    /// <summary>Signals the loop to stop and joins the thread. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts.Cancel();
        _wake.Set(); // unblock the WaitAny so the loop sees cancellation
        _thread?.Join(TimeSpan.FromSeconds(2));

        _cts.Dispose();
        _wake.Dispose();
    }
}
