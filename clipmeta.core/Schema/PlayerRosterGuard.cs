namespace ClipMetaCore.Schema;

/// <summary>
/// Pure check behind the soft "unknown player" advisory: given a pipe-delimited players value and the
/// known-player set (library vocab ∪ a session roster), returns the tokens that match neither — names
/// the model should confirm with the user before they stick (e.g. "miami element" is a warpaint, not a
/// person). The write is never blocked here; this only identifies what to flag.
/// </summary>
public static class PlayerRosterGuard
{
    /// <summary>Tokens in <paramref name="playersValue"/> absent from <paramref name="known"/> (first occurrence, in order).</summary>
    public static IReadOnlyList<string> UnknownPlayers(string? playersValue, IReadOnlySet<string> known)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (string.IsNullOrWhiteSpace(playersValue))
            return Array.Empty<string>();

        var unknown = new List<string>();
        foreach (string token in playersValue.Split(
                     '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!known.Contains(token) && !unknown.Contains(token, StringComparer.OrdinalIgnoreCase))
                unknown.Add(token);
        }
        return unknown;
    }
}
