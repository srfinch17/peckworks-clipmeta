using ClipMetaCore;
using ClipMetaCore.Abstractions;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Commands;

/// <summary>
/// Applies a write operation to many clips, isolating per-file failures so one unreadable, locked,
/// or refused clip never aborts the run. The operation is supplied as a per-file mutation factory,
/// keeping this orchestrator ignorant of <i>which</i> command it is running.
/// </summary>
internal static class BatchCommand
{
    /// <summary>
    /// For each path in <paramref name="files"/>, obtains its mutation from
    /// <paramref name="mutationFor"/> (a <see langword="null"/> result skips the file — e.g. a
    /// batch-copy's source) and applies it through the unchanged single-file write-safety chain.
    /// User-error exceptions are reported and counted; the run continues. Prints a summary to
    /// <paramref name="output"/> (default <see cref="Console.Out"/>) and returns <c>0</c> when
    /// every file succeeded, else <c>2</c>.
    /// </summary>
    internal static int Run(
        IReadOnlyList<string> files,
        Func<string, MetadataMutation?> mutationFor,
        IClipMetaLogger logger,
        TextWriter? output = null)
    {
        TextWriter o = output ?? Console.Out;
        int updated = 0, failed = 0, skipped = 0;

        foreach (string file in files)
        {
            MetadataMutation? mutation;
            try
            {
                mutation = mutationFor(file);
            }
            catch (Exception ex) when (IsUserError(ex))
            {
                o.WriteLine($"FAILED {file}: {ex.Message}");
                failed++;
                continue;
            }

            if (mutation == null)
            {
                skipped++;
                continue;
            }

            try
            {
                new Mp4Writer().WriteMetadata(file, mutation, logger);
                updated++;
            }
            catch (Exception ex) when (IsUserError(ex))
            {
                o.WriteLine($"FAILED {file}: {ex.Message}");
                failed++;
            }
        }

        o.WriteLine($"Batch complete: {updated} updated, {failed} failed, {skipped} skipped ({files.Count} clips).");
        return failed == 0 ? 0 : 2;
    }

    /// <summary>
    /// The exception set that represents a per-file <i>user</i> problem (a clip that can't be read,
    /// is locked, is malformed, or whose value is invalid) — isolated and counted, never aborting
    /// the batch. Mirrors the catch set <see cref="Program"/> maps for single-file writes. Anything
    /// outside this set is a genuine bug and is allowed to propagate rather than be hidden as a
    /// per-file "failure".
    /// </summary>
    private static bool IsUserError(Exception ex) =>
        ex is IOException
           or UnauthorizedAccessException
           or UnsupportedFormatException
           or InvalidDataException
           or ArgumentException
           or InvalidOperationException;
}
