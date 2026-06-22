using ClipMetaCore.Watching;

namespace ClipMetaScribe.Commands;

/// <summary>
/// Resolves which clip an open media player is showing and prints the ranked candidates.
/// Resolve-only: to tag a candidate, run a normal write on its path.
/// </summary>
internal static class WatchingCommand
{
    /// <summary>
    /// Prints watched-clip candidates under <paramref name="libraryDir"/> to
    /// <paramref name="output"/> (default <see cref="Console.Out"/>).
    /// </summary>
    /// <param name="libraryDir">Root directory of the clip library to search.</param>
    /// <param name="limit">Maximum number of candidates to return.</param>
    /// <param name="includeAccessFallback">When true, include access-time candidates if no player resolves a clip.</param>
    /// <param name="output">Writer to print to; defaults to <see cref="Console.Out"/>.</param>
    /// <param name="windowSource">
    /// Injected process-window source for testing. When null (the default), the real
    /// <see cref="ProcessWindowSource.ForCurrentPlatform"/> source is used.
    /// </param>
    /// <returns>Exit code 0.</returns>
    internal static int Run(string libraryDir, int limit, bool includeAccessFallback,
                            TextWriter? output = null, IProcessWindowSource? windowSource = null)
    {
        output ??= Console.Out;

        var resolver = WatchingResolver.CreateDefault(windowSource ?? ProcessWindowSource.ForCurrentPlatform());
        WatchingResult result = resolver.Resolve(libraryDir, limit, includeAccessFallback);

        if (result.Diagnostics.UnresolvedPlayers.Count > 0)
        {
            foreach (UnresolvedPlayer up in result.Diagnostics.UnresolvedPlayers)
            {
                string where = up.ForeignDirectory is null ? "" : $" from \"{up.ForeignDirectory}\"";
                output.WriteLine(
                    $"WARNING: {up.Player} is playing \"{up.ReferencedName}\"{where}, which is not in this " +
                    "library — you may be in the wrong folder. Do not tag until you've confirmed.");
            }
            output.WriteLine();
        }

        IReadOnlyList<WatchingCandidate> candidates = result.Candidates;

        if (candidates.Count == 0)
        {
            output.WriteLine("No watched-clip candidates found.");
            return 0;
        }

        output.WriteLine("Watched-clip candidates (most likely first):");
        foreach (WatchingCandidate c in candidates)
        {
            string via = c.Player is null ? "" : $" via {c.Player}";
            string locked = c.InUse ? "  [in use]" : "";
            output.WriteLine($"  [{c.Confidence}] {c.Path}");
            output.WriteLine($"        source={c.Source}{via}  {c.SecondsSinceAccess:F0}s since access{locked}");
            if (c.Note is not null)
                output.WriteLine($"        note: {c.Note}");
        }

        int pending = TagQueue.Status(libraryDir, LockProbe.IsInUse).Count;
        if (pending > 0)
            output.WriteLine($"\nQueued tags pending: {pending} (run --flush-queue after closing the player).");
        return 0;
    }
}
