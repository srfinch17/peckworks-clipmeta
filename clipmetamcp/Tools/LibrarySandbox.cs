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
    /// Validates a clip path for WRITING. Same checks as <see cref="ResolveClipPath"/>, with one
    /// stricter precondition: writes with no configured library are refused outright (spec §3) —
    /// a read outside a sandbox shows someone data; a write outside a sandbox mutates their
    /// files. The message names the fix because the model relays it to the user.
    /// </summary>
    public string ResolveWritePath(string path)
    {
        if (Root is null)
            throw new ToolException(
                "Writing is disabled: no clips library is configured. Tools may only modify " +
                $"files inside a configured library ({EnvVarName}; set automatically when the " +
                "extension is installed with a clips folder).");
        return ResolveClipPath(path);
    }

    /// <summary>
    /// Resolves an arbitrary file path for a library-management operation (a backup file, or a
    /// clip whose backups are being managed) and enforces canonical containment in the library.
    /// Unlike <see cref="ResolveClipPath"/> it does NOT require a <c>.mp4</c> extension (backups
    /// are <c>clip.mp4.bak-&lt;stamp&gt;</c>) and existence is the caller's choice — you can list
    /// or prune the backups of a clip that has since been deleted. Requires a configured library
    /// and rejects ADS syntax, exactly like the clip resolver.
    /// </summary>
    /// <param name="path">Absolute, or relative to the library root.</param>
    /// <param name="mustExist">When true, refuses unless the file is present.</param>
    public string ResolveContainedPath(string path, bool mustExist)
    {
        RequireRoot();
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("A non-empty 'path' is required.");

        string full;
        try
        {
            full = Path.GetFullPath(path, Root!);
        }
        catch (ArgumentException)
        {
            throw new ToolException($"'{path}' is not a valid file path.");
        }

        if (Path.GetFileName(full).Contains(':'))
            throw new ToolException(
                $"'{path}' uses NTFS alternate-data-stream syntax, which is not supported.");

        if (mustExist && !File.Exists(full))
            throw new ToolException($"No file exists at '{full}'. Check the path and try again.");

        // Canonical containment — junctions/symlinks resolved, same rule as ResolveClipPath.
        // A nonexistent path can't be link-resolved, so judge it lexically (GetFullPath already
        // collapsed ".."); an existing path is resolved through its reparse points.
        string toCheck = File.Exists(full) || Directory.Exists(full) ? ResolveRealPath(full) : full;
        if (!IsContained(toCheck, _canonicalRoot!))
            throw new ToolException(
                $"'{full}' is outside the configured clips library '{Root}'. " +
                "Only files inside that folder can be accessed.");

        return full;
    }

    /// <summary>
    /// Returns the configured library root, or refuses when none is set. Directory-scoped tools
    /// (list/find/vocab/export/index) have no meaningful "anywhere" mode — scanning an undefined
    /// directory tree on an LLM's behalf is exactly the surprise this sandbox exists to prevent —
    /// so unlike single-clip reads they hard-require configuration (plan, phase 2).
    /// </summary>
    public string RequireRoot()
    {
        if (Root is null)
            throw new ToolException(
                "No clips library is configured. Library-wide tools need the " +
                $"{EnvVarName} environment variable (set automatically when the extension is " +
                "installed with a clips folder; pick one in the extension's settings).");
        return Root;
    }

    /// <summary>
    /// Validates an optional subfolder argument for a directory-scoped tool and returns the
    /// absolute directory to operate on: the library root when <paramref name="subfolder"/> is
    /// null/blank, otherwise the subfolder resolved against the root. The same canonical
    /// (junction/symlink-resolved) containment check as clip paths applies — a subfolder is
    /// attacker-reachable input exactly like a file path. The directory must exist.
    /// </summary>
    public string ResolveLibraryDirectory(string? subfolder)
    {
        string root = RequireRoot();
        if (string.IsNullOrWhiteSpace(subfolder))
            return root;

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(subfolder, root));
        }
        catch (ArgumentException)
        {
            throw new ToolException($"'{subfolder}' is not a valid folder path.");
        }

        // Root itself is legal ("." or the root's own absolute path); anything else must be
        // strictly inside it on the canonical path, same rule as ResolveClipPath.
        string canonical = ResolveRealPath(full);
        if (!canonical.Equals(_canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !IsContained(canonical, _canonicalRoot!))
        {
            throw new ToolException(
                $"'{subfolder}' is outside the configured clips library '{root}'. " +
                "Only folders inside the library can be listed or searched.");
        }

        if (!Directory.Exists(full))
            throw new ToolException($"No folder exists at '{full}'. Check the subfolder name.");

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
