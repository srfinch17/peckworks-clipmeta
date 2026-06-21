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
    /// <returns>Exit code 0.</returns>
    internal static int Run(string libraryDir, int limit, bool includeAccessFallback, TextWriter? output = null)
    {
        output ??= Console.Out;

        var resolver = WatchingResolver.CreateDefault(ProcessWindowSource.ForCurrentPlatform());
        WatchingResult result = resolver.Resolve(libraryDir, limit, includeAccessFallback);
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
            string locked = c.InUse ? "  [in use — close/advance the player before tagging]" : "";
            output.WriteLine($"  [{c.Confidence}] {c.Path}");
            output.WriteLine($"        source={c.Source}{via}  {c.SecondsSinceAccess:F0}s since access{locked}");
        }
        return 0;
    }
}
