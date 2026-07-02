using System.Security.Cryptography;
using System.Text;

namespace ClipMetaCore.Write;

/// <summary>
/// Thrown when <see cref="CrossProcessLock.Acquire"/> gives up waiting for another holder to
/// release the resource. Derives from <see cref="IOException"/> deliberately: every existing
/// caller of the write engine and the tag queue already treats <see cref="IOException"/> as
/// "the file could not be written this time, fail safe / keep it queued / report and continue",
/// which is exactly the right disposition for a lock timeout, so no catch list anywhere needs
/// a new exception type (PITFALLS 2026-06-21: a never-throw caller must cover the writer's
/// FULL throw surface).
/// </summary>
public sealed class CrossProcessLockTimeoutException : IOException
{
    /// <summary>Creates the timeout error with a caller-facing message.</summary>
    public CrossProcessLockTimeoutException(string message) : base(message) { }
}

/// <summary>
/// Cross-process mutual exclusion for one filesystem resource (a clip being rewritten, or the
/// deferred-tag queue file), backed by a named OS <see cref="Mutex"/>. All in-process
/// concurrency control (the MCP server's write tools, its background drain pump) only ever
/// serialized ONE process, but deployment is explicitly multi-process: Claude Desktop and Claude
/// Code each spawn their own MCP server, and clipmetascribe batch/<c>--flush-queue</c> runs
/// alongside them. A named mutex serializes all of them (threads of one process included), so a
/// queue drain in one process can no longer overwrite an enqueue from another, and two processes
/// can no longer both rebuild the same clip from the same stale parse.
/// <para><b>Mutex name.</b> Derived from the canonicalized resource path
/// (<see cref="Path.GetFullPath(string)"/>, upper-cased to match Windows filesystem case
/// insensitivity) and then SHA-256 hashed, mutex names have length limits (roughly MAX_PATH) and
/// reject some path characters, so the raw path is never embedded; two paths that name the same
/// file always map to the same mutex, and the fixed-length hash can never overflow the limit.</para>
/// <para><b><c>Local\</c> namespace, deliberate.</b> <c>Local\</c> scopes the mutex to the
/// current logon session; <c>Global\</c> would span sessions (services, other logged-on users)
/// at the cost of cross-session ACL friction. ClipMeta is a per-user desktop tool: every
/// cooperating writer (both MCP servers, the CLI) runs in the user's own interactive session, so
/// <c>Local\</c> covers the entire real deployment while keeping the object private to the user.
/// A hypothetical writer in ANOTHER session would not share the lock, but it also would not
/// share the user's session at all in this product's deployment.</para>
/// <para><b>Thread affinity.</b> A <see cref="Mutex"/> must be released by the thread that
/// acquired it. Every span this lock wraps (<c>Mp4Writer.WriteMetadata</c>, the
/// <c>TagQueue</c> read-modify-write operations, the MCP tool handlers, the drain pump's loop)
/// is fully synchronous, no <c>await</c> can migrate the continuation to another thread, so
/// acquire and release always happen on one thread. Do not hold an instance across an
/// <c>await</c>.</para>
/// <para><b>Reentrancy.</b> An OS mutex is recursive on its owning thread: a thread that already
/// holds the lock may <see cref="Acquire"/> the same resource again (each instance releases
/// exactly once on dispose). This is load-bearing: <c>TagQueue.Save</c> acquires the queue lock
/// and is also called from inside <c>TagQueue.Enqueue</c>/<c>Drain</c>, which already hold it.</para>
/// <para><b>Lock ordering (deadlock freedom).</b> Two lock kinds exist: the QUEUE lock (keyed on
/// the <c>.clipmeta-queue</c> path) and per-FILE locks (keyed on the clip path). The only code
/// that ever holds both is <c>TagQueue.Drain</c>, which takes the queue lock and then, per entry,
/// the file lock (nested inside <c>Mp4Writer.WriteMetadata</c>). Nothing acquires the queue lock
/// while holding a file lock, <c>Mp4Writer</c> knows nothing of the queue, so the ordering
/// queue-then-file is globally consistent and cannot deadlock.</para>
/// </summary>
public sealed class CrossProcessLock : IDisposable
{
    /// <summary>
    /// How long <see cref="Acquire"/> waits for another holder before failing. Generous enough
    /// to ride out a full multi-second MP4 rewrite (or a queue drain of several clips) in
    /// another process, short enough that a wedged holder produces a clear error instead of a
    /// silent hang.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Session-local namespace plus a product marker; the per-resource SHA-256 hash is appended.
    /// See the class remarks for why <c>Local\</c> rather than <c>Global\</c>.
    /// </summary>
    private const string MutexNamePrefix = @"Local\PeckworksClipMeta-";

    private readonly Mutex _mutex;
    private bool _disposed;

    private CrossProcessLock(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Acquires the cross-process lock for <paramref name="resourcePath"/>, blocking up to
    /// <paramref name="timeout"/> (default <see cref="DefaultTimeout"/>). Dispose the returned
    /// instance, on the same thread, to release. An <see cref="AbandonedMutexException"/> (the
    /// previous holder's process or thread died without releasing) is treated as a successful
    /// acquisition: ownership IS transferred with that exception, and every resource this lock
    /// guards is crash-consistent on its own (temp-file-then-atomic-swap everywhere), so there is
    /// no half-written state to distrust.
    /// </summary>
    /// <param name="resourcePath">Path of the resource to serialize on (the clip file for a
    /// metadata write, the queue file for a queue operation). It need not exist.</param>
    /// <param name="timeout">Optional override of <see cref="DefaultTimeout"/>.</param>
    /// <exception cref="CrossProcessLockTimeoutException">The lock could not be acquired within
    /// the timeout (an <see cref="IOException"/>, so existing fail-safe catch paths apply).</exception>
    public static CrossProcessLock Acquire(string resourcePath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourcePath);
        TimeSpan wait = timeout ?? DefaultTimeout;

        var mutex = new Mutex(initiallyOwned: false, MutexNameFor(resourcePath));
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                // The previous holder died mid-operation. The wait HAS granted us ownership;
                // the guarded resources are temp-then-atomic-swap, so nothing is torn.
                acquired = true;
            }

            if (!acquired)
                throw new CrossProcessLockTimeoutException(
                    $"Timed out after {wait.TotalSeconds:0} seconds waiting for exclusive access " +
                    $"to '{resourcePath}'. Another clipmeta process (an MCP server or a " +
                    $"clipmetascribe run) appears to be writing it; retry when that operation " +
                    $"finishes.");

            return new CrossProcessLock(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The OS mutex name for a resource path: prefix + SHA-256 of the canonicalized (full,
    /// upper-invariant) path, hex-encoded. Internal so tests can pin the canonicalization
    /// (relative vs absolute, case) without racing real mutexes.
    /// </summary>
    internal static string MutexNameFor(string resourcePath)
    {
        string canonical = Path.GetFullPath(resourcePath).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return MutexNamePrefix + Convert.ToHexString(hash);
    }

    /// <summary>
    /// Releases the lock and the underlying OS handle. Must run on the acquiring thread
    /// (mutex thread affinity). Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
