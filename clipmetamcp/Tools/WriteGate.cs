using ClipMetaCore.Write;

namespace ClipMetaMcp.Tools;

/// <summary>
/// Write latch for every operation that mutates a clip file, the direct write tools, the backup
/// restore/prune tools, AND the deferred-queue drains. Delegates to Core's
/// <see cref="CrossProcessLock"/>, a named mutex keyed on the canonicalized resource path (the
/// clip for a write, the <c>.clipmeta-queue</c> file for a queue drain), so the serialization
/// spans PROCESSES, not just this server: Claude Desktop's MCP server, Claude Code's, and a
/// clipmetascribe batch or <c>--flush-queue</c> run all contend on the same lock. The MCP and CLI
/// shells therefore share one gate with the Core engine, which takes the same lock internally
/// (reentrant on the acquiring thread, so gating here and inside <c>Mp4Writer</c>/<c>TagQueue</c>
/// is belt and braces, not a deadlock).
/// <para>An earlier in-process <c>SemaphoreSlim</c> version of this gate claimed to retire the
/// <c>File.Replace</c> race "permanently"; that overclaimed. It only ever serialized ONE process,
/// while deployment is explicitly multi-process, and the worst case of the unserialized race is
/// not a torn file but a stale-based write: two writers snapshot the same original, both swap,
/// and the loser's committed fields are silently discarded. The cross-process lock closes that
/// for every process that honors it; see <see cref="CrossProcessLock"/> for the namespace,
/// naming, ordering, and thread-affinity rules.</para>
/// </summary>
internal static class WriteGate
{
    /// <summary>
    /// Acquires the cross-process write lock for <paramref name="resourcePath"/> (bounded wait,
    /// see <see cref="CrossProcessLock.DefaultTimeout"/>). Dispose on the same thread to release.
    /// </summary>
    /// <exception cref="CrossProcessLockTimeoutException">Another clipmeta process held the
    /// resource past the timeout (an <see cref="IOException"/>, mapped by callers' existing
    /// fail-safe catch paths).</exception>
    public static IDisposable Acquire(string resourcePath) => CrossProcessLock.Acquire(resourcePath);
}
