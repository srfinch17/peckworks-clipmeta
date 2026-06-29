using System.Threading;

namespace ClipMetaMcp.Tools;

/// <summary>
/// Process-wide single-flight latch for every operation that mutates a clip file, the direct
/// write tools AND the deferred-queue drain. Two concurrent rewrites of the same file would race
/// at <c>File.Replace</c>; serializing all writes here retires that race permanently (spec risk
/// R2/R8). The session loop is single-threaded today, so this is insurance against a future
/// pipelined host, at negligible cost.
/// </summary>
internal static class WriteGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Acquires the write latch, blocking until it is free.</summary>
    public static void Enter() => Gate.Wait();

    /// <summary>Releases the write latch.</summary>
    public static void Exit() => Gate.Release();
}
