// clipmetascribe/Commands/FlushQueueCommand.cs
using ClipMetaCore.Abstractions;
using ClipMetaCore.Logging;
using ClipMetaCore.Watching;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Commands;

/// <summary>
/// Drains the deferred-tag queue for a library, writing every queued tag whose clip is no longer
/// locked. Used for the final clip of a session, after the player has closed and there is no
/// "next" watched-clip call to pump the drain.
/// </summary>
internal static class FlushQueueCommand
{
    /// <summary>Drains the queue under <paramref name="libraryDir"/> and prints the outcome.</summary>
    /// <param name="libraryDir">Library root holding the queue.</param>
    /// <param name="output">Writer to print to; defaults to <see cref="Console.Out"/>.</param>
    /// <param name="writer">Write engine; defaults to a real <see cref="Mp4Writer"/>.</param>
    /// <param name="isInUse">Lock predicate; defaults to <see cref="LockProbe.IsInUse"/>.</param>
    /// <returns>Exit code 0.</returns>
    internal static int Run(string libraryDir, TextWriter? output = null,
                            IMediaWriter? writer = null, Func<string, bool>? isInUse = null)
    {
        output ??= Console.Out;
        writer ??= new Mp4Writer();
        isInUse ??= LockProbe.IsInUse;

        DrainReport report = TagQueue.Drain(libraryDir, writer, NullLogger.Instance, isInUse);

        if (report.Written.Count == 0 && report.StillQueued.Count == 0 && report.Dropped.Count == 0)
        {
            output.WriteLine("There are no tags queued for this library.");
            return 0;
        }

        foreach (string w in report.Written) output.WriteLine($"  wrote   {w}");
        foreach (string s in report.StillQueued) output.WriteLine($"  still locked (will retry)  {s}");
        foreach (string d in report.Dropped) output.WriteLine($"  dropped (file gone)  {d}");
        output.WriteLine(
            $"Flushed: {report.Written.Count} written, {report.StillQueued.Count} still queued, " +
            $"{report.Dropped.Count} dropped.");
        return 0;
    }
}
