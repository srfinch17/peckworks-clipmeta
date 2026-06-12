namespace ClipMetaMcp.Tools;

/// <summary>
/// Confines tool file access to the user's configured clips folder (spec §3, risk R6 — the
/// caller is an LLM that can hallucinate paths). The root arrives via the
/// <c>CLIPMETA_LIBRARY_ROOT</c> environment variable, which the .mcpb user_config plumbing sets
/// from the folder the user picked at install time.
/// </summary>
public sealed class LibrarySandbox
{
    /// <summary>Environment variable carrying the library root path.</summary>
    public const string EnvVarName = "CLIPMETA_LIBRARY_ROOT";

    /// <summary>Normalized absolute library root, or null when not configured.</summary>
    public string? Root { get; }

    /// <summary>
    /// Creates a sandbox over the given root. A null/blank root means unconfigured: read tools
    /// then work anywhere (manual/dev installs), while write tools refuse outright (spec §3).
    /// </summary>
    public LibrarySandbox(string? root)
    {
        Root = string.IsNullOrWhiteSpace(root)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>Creates a sandbox from the <c>CLIPMETA_LIBRARY_ROOT</c> environment variable.</summary>
    public static LibrarySandbox FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(EnvVarName));

    /// <summary>
    /// Validates a clip path for reading and returns its resolved absolute form. Relative paths
    /// resolve against the library root (the working directory of a host-spawned server is
    /// undefined — never resolve against it). Requires containment in the root when one is
    /// configured, an .mp4 extension, and an existing file. Throws <see cref="ToolException"/>
    /// with a model-readable message otherwise.
    /// </summary>
    public string ResolveClipPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("A non-empty 'path' is required.");

        string full;
        try
        {
            full = Root is not null ? Path.GetFullPath(path, Root) : Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            throw new ToolException($"'{path}' is not a valid file path.");
        }

        // GetFullPath has already collapsed any ".." segments, so this containment check also
        // rejects traversal attempts like "..\..\outside.mp4".
        if (Root is not null && !IsInsideRoot(full))
            throw new ToolException(
                $"'{full}' is outside the configured clips library '{Root}'. " +
                "Only files inside that folder can be accessed.");

        if (!full.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ToolException($"'{full}' is not an .mp4 file. clipmeta tools only operate on MP4 clips.");

        if (!File.Exists(full))
            throw new ToolException($"No file exists at '{full}'. Check the path and try again.");

        return full;
    }

    private bool IsInsideRoot(string fullPath)
    {
        // Containment = the path starts with "<root><separator>". Ordinal-ignore-case matches
        // Windows filesystem semantics; the appended separator stops sibling-prefix escapes
        // (root "C:\clips" must not match "C:\clips-evil\x.mp4").
        string rootWithSeparator = Root! + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
