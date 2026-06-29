namespace ClipMetaCore.Watching;

/// <summary>The media players ClipMeta recognizes by process name.</summary>
public static class MediaPlayers
{
    /// <summary>
    /// Process names (without the <c>.exe</c> suffix, as <see cref="System.Diagnostics.Process.ProcessName"/>
    /// reports them) of recognized players. Matched case-insensitively. <b>Append here to support a
    /// new player</b>, no other code changes are required.
    /// </summary>
    public static IReadOnlyList<string> KnownProcessNames { get; } = new[]
    {
        "mpc-hc", "mpc-hc64", "mpc-be", "vlc", "mpv", "wmplayer", "PotPlayer",
    };
}
