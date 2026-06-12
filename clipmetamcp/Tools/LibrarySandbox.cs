namespace ClipMetaMcp.Tools;

/// <summary>
/// Confines tool file access to the user's configured clips folder (spec §3, risk R6 — the
/// caller is an LLM that can hallucinate paths). The root arrives via the
/// <c>CLIPMETA_LIBRARY_ROOT</c> environment variable, which the .mcpb user_config plumbing sets
/// from the folder the user picked at install time.
///
/// Containment is checked against the OS-canonical path (every directory junction and symlink
/// component resolved), not the lexical one: <c>Path.GetFullPath</c> does NOT resolve reparse
/// points while <c>FileStream</c> DOES follow them, so a junction inside the library pointing
/// outside it would otherwise tunnel straight through a purely lexical check (2026-06-11
/// adversarial review, finding F1 — demonstrated escape).
/// </summary>
public sealed class LibrarySandbox
{
    /// <summary>Environment variable carrying the library root path.</summary>
    public const string EnvVarName = "CLIPMETA_LIBRARY_ROOT";

    /// <summary>
    /// Normalized absolute library root as configured, or null when not configured.
    /// Kept in non-verbatim form: <c>Path.GetFullPath</c> does not collapse <c>..</c> inside
    /// <c>\\?\</c> verbatim paths, so a verbatim root would reopen a traversal hole.
    /// </summary>
    public string? Root { get; }

    /// <summary>
    /// The root with every junction/symlink component resolved — what containment is actually
    /// checked against. Differs from <see cref="Root"/> when the user's library path itself
    /// goes through a link (e.g. a relocated-folder junction).
    /// </summary>
    private readonly string? _canonicalRoot;

    /// <summary>
    /// Creates a sandbox over the given root. A null/blank root means unconfigured: read tools
    /// then work anywhere (manual/dev installs), while write tools refuse outright (spec §3).
    /// </summary>
    public LibrarySandbox(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            Root = null;
            _canonicalRoot = null;
            return;
        }

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // Canonicalize once: if the configured root path itself contains link components,
        // clips inside it resolve to the link TARGET — containment must compare like with like
        // or every legitimate clip would be refused.
        _canonicalRoot = Directory.Exists(Root)
            ? Path.TrimEndingDirectorySeparator(ResolveRealPath(Root))
            : Root;
    }

    /// <summary>Creates a sandbox from the <c>CLIPMETA_LIBRARY_ROOT</c> environment variable.</summary>
    public static LibrarySandbox FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(EnvVarName));

    /// <summary>
    /// Validates a clip path for reading and returns its resolved absolute form. Relative paths
    /// resolve against the library root (the working directory of a host-spawned server is
    /// undefined — never resolve against it). Requires containment in the root when one is
    /// configured (checked on the canonical, link-resolved path), an .mp4 extension, an
    /// existing file, and no NTFS alternate-data-stream syntax. Throws
    /// <see cref="ToolException"/> with a model-readable message otherwise.
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

        // NTFS alternate data streams ("clip.mp4:hidden.mp4") would satisfy the .mp4 suffix
        // check with the STREAM name while opening arbitrary hidden content — and File.Replace
        // against a stream path has destructive semantics for the future write tools. Any colon
        // in the final path segment is stream syntax (the drive colon lives in the root segment).
        if (Path.GetFileName(full).Contains(':'))
            throw new ToolException(
                $"'{path}' uses NTFS alternate-data-stream syntax, which is not supported. " +
                "Give the plain path to the .mp4 file.");

        if (!full.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ToolException($"'{full}' is not an .mp4 file. clipmeta tools only operate on MP4 clips.");

        if (!File.Exists(full))
            throw new ToolException($"No file exists at '{full}'. Check the path and try again.");

        // Containment, checked on the canonical path: GetFullPath already collapsed ".."
        // segments (lexical traversal), and ResolveRealPath now resolves junction/symlink
        // components (physical traversal) so the path the OS will actually open is the one
        // being judged.
        if (Root is not null && !IsContained(ResolveRealPath(full), _canonicalRoot!))
            throw new ToolException(
                $"'{full}' is outside the configured clips library '{Root}'. " +
                "Only files inside that folder can be accessed.");

        return full;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is strictly inside <paramref name="root"/>.
    /// Ordinal-ignore-case matches Windows filesystem semantics; the separator-terminated
    /// prefix stops sibling-prefix escapes (root "C:\clips" must not match "C:\clips-evil\x").
    /// </summary>
    internal static bool IsContained(string fullPath, string root)
    {
        // A drive root ("C:\") keeps its separator through TrimEndingDirectorySeparator —
        // appending another would build "C:\\", which nothing starts with and which silently
        // refused every clip on a whole-drive library (2026-06-11 review, finding F3).
        string rootWithSeparator = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves every link component (directory junction, symlink) in an existing path to its
    /// final target, walking from the volume root down. Cloud-placeholder files (Dropbox,
    /// OneDrive) are reparse points but NOT links — <c>ResolveLinkTarget</c> returns null for
    /// them, so they pass through untouched and online-only clips keep working.
    /// </summary>
    internal static string ResolveRealPath(string fullPath)
    {
        string? volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(volumeRoot))
            return fullPath; // no recognizable root — leave as-is; containment then fails closed

        string current = volumeRoot;
        string remainder = fullPath[volumeRoot.Length..];
        foreach (string segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);

            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!info.Exists)
                continue; // nonexistent component: nothing to resolve, keep walking lexically

            // returnFinalTarget follows chained links to the end in one call.
            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = target.FullName;
        }
        return current;
    }
}
