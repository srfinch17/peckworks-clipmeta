using System.Text.Json;
using System.Text.Json.Nodes;
using ClipMetaMcp.Tools;

namespace ClipMetaMcp.Install;

/// <summary>Outcome of an install/uninstall operation, for the human-facing report.</summary>
/// <param name="Success">False means the config was refused and left byte-identical.</param>
/// <param name="Message">One-line explanation written for the person running the command.</param>
/// <param name="BackupPath">Timestamped copy of the pre-change config, or null when none was made.</param>
public sealed record InstallResult(bool Success, string Message, string? BackupPath);

/// <summary>
/// The manual-config fallback (spec §4): writes a <c>mcpServers.clipmeta</c> entry into Claude
/// Desktop's <c>claude_desktop_config.json</c> for hosts/situations where bundle install isn't
/// available, or, as discovered in the field, where it is silently broken (the Microsoft
/// Store build; see PITFALLS 2026-06-12).
///
/// Contract mirrors the write engine's golden rule, scaled down: the existing config is backed
/// up (timestamped sibling) before any change, an unparseable config is REFUSED, never
/// "repaired", and everything that is not the clipmeta entry round-trips untouched.
/// </summary>
public static class ClaudeConfigInstaller
{
    /// <summary>The key written under <c>mcpServers</c>.</summary>
    public const string ServerKey = "clipmeta";

    // ── Config discovery ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the config file Claude Desktop will actually read, newest install style first:
    /// the Microsoft Store (MSIX) build virtualizes <c>%APPDATA%\Claude</c> into its package
    /// container, when that container exists, a file at the classic path is INVISIBLE to the
    /// app, so the container must win. Returns the first existing file; if none exists yet,
    /// the preferred location to create (container when present, else classic).
    /// </summary>
    public static string DiscoverConfigPath()
    {
        var candidates = new List<string>();

        // MSIX containers: %LOCALAPPDATA%\Packages\Claude_<publisherhash>\LocalCache\Roaming\Claude
        string packagesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packagesDir))
        {
            foreach (string package in Directory.EnumerateDirectories(packagesDir, "Claude_*"))
            {
                string containerConfigDir = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                if (Directory.Exists(containerConfigDir))
                    candidates.Add(Path.Combine(containerConfigDir, "claude_desktop_config.json"));
            }
        }

        // Classic installer build.
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude", "claude_desktop_config.json"));

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    // ── Install / uninstall (pure file operations, what the tests drive) ────────────────

    /// <summary>
    /// Inserts or updates the clipmeta entry in <paramref name="configPath"/>. Creates the file
    /// (and directory) when absent. <paramref name="command"/>/<paramref name="commandArgs"/>
    /// are how the host should launch the server; <paramref name="libraryRoot"/> becomes the
    /// sandbox env var (null = omitted: reads work anywhere, writes stay disabled).
    /// </summary>
    public static InstallResult InstallInto(
        string configPath, string command, IReadOnlyList<string> commandArgs, string? libraryRoot)
    {
        JsonObject root;
        string? backupPath = null;

        if (File.Exists(configPath))
        {
            backupPath = MakeBackup(configPath);
            try
            {
                root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
                    ?? throw new JsonException("top-level value is not an object");
            }
            catch (JsonException ex)
            {
                // Never "fix" a config we can't parse, it may hold servers the user cares
                // about in a form we'd destroy. Refuse; the file is untouched.
                return new InstallResult(false,
                    $"Refused: '{configPath}' is not valid JSON ({ex.Message}). " +
                    "Fix or remove the file and run --install again. Nothing was changed.",
                    backupPath);
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            root = new JsonObject();
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            if (root.ContainsKey("mcpServers"))
                return new InstallResult(false,
                    $"Refused: 'mcpServers' in '{configPath}' is not an object. Nothing was changed.",
                    backupPath);
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        var entryArgs = new JsonArray();
        foreach (string arg in commandArgs)
            entryArgs.Add(arg);
        var entry = new JsonObject
        {
            ["command"] = command,
            ["args"] = entryArgs,
        };
        if (libraryRoot is not null)
        {
            entry["env"] = new JsonObject
            {
                [LibrarySandbox.EnvVarName] = Path.GetFullPath(libraryRoot),
            };
        }
        servers[ServerKey] = entry; // insert or replace, re-running --install is idempotent

        File.WriteAllText(configPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        string libraryNote = libraryRoot is not null
            ? $"library root: {Path.GetFullPath(libraryRoot)}"
            : "no library root set, read tools work anywhere, WRITE tools stay disabled " +
              "(re-run with --library-root <folder> to enable them)";
        return new InstallResult(true,
            $"Installed '{ServerKey}' into {configPath} ({libraryNote}). " +
            "Fully quit and restart Claude Desktop to pick it up.",
            backupPath);
    }

    /// <summary>
    /// Removes the clipmeta entry from <paramref name="configPath"/>, leaving every other
    /// server and setting untouched. Absent file or absent entry is a graceful no-op.
    /// </summary>
    public static InstallResult UninstallFrom(string configPath)
    {
        if (!File.Exists(configPath))
            return new InstallResult(true, $"Nothing to do: no config file at {configPath}.", null);

        string backupPath = MakeBackup(configPath);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
                ?? throw new JsonException("top-level value is not an object");
        }
        catch (JsonException ex)
        {
            return new InstallResult(false,
                $"Refused: '{configPath}' is not valid JSON ({ex.Message}). Nothing was changed.",
                backupPath);
        }

        if (root["mcpServers"] is not JsonObject servers || !servers.ContainsKey(ServerKey))
            return new InstallResult(true,
                $"Nothing to do: no '{ServerKey}' entry in {configPath}.", backupPath);

        servers.Remove(ServerKey);
        File.WriteAllText(configPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new InstallResult(true,
            $"Removed '{ServerKey}' from {configPath}. Restart Claude Desktop to apply.",
            backupPath);
    }

    /// <summary>
    /// Timestamped sibling copy (config.json.bak-yyyyMMdd-HHmmss-fff). Milliseconds because
    /// install immediately after uninstall within one second must not clobber the first backup.
    /// </summary>
    private static string MakeBackup(string configPath)
    {
        string backupPath = $"{configPath}.bak-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        File.Copy(configPath, backupPath, overwrite: false);
        return backupPath;
    }

    // ── Command-line entry points (Program.cs delegates here) ────────────────────────────

    /// <summary>Runs the --install flow: resolve our own launch command, discover the config, report.</summary>
    public static int RunInstall(string? libraryRoot, string? explicitConfigPath)
    {
        (string? command, string[] args) = ResolveOwnCommand();
        if (command is null)
        {
            Console.Error.WriteLine("Error: cannot determine this executable's own path.");
            return 1;
        }

        string configPath = explicitConfigPath ?? DiscoverConfigPath();
        InstallResult result = InstallInto(configPath, command, args, libraryRoot);
        return Report(result);
    }

    /// <summary>Runs the --uninstall flow.</summary>
    public static int RunUninstall(string? explicitConfigPath)
    {
        string configPath = explicitConfigPath ?? DiscoverConfigPath();
        InstallResult result = UninstallFrom(configPath);
        return Report(result);
    }

    /// <summary>
    /// How an MCP host should launch this server. The published single-file exe is its own
    /// command; under `dotnet run` (dev) the host is dotnet.exe and the entry dll becomes the
    /// argument, same resolution rule the selftest uses for spawning itself.
    /// </summary>
    private static (string? Command, string[] Args) ResolveOwnCommand()
    {
        string? exePath = Environment.ProcessPath;
        if (exePath is null || !File.Exists(exePath))
            return (null, []);

        if (Path.GetFileNameWithoutExtension(exePath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string entryAssembly = Environment.GetCommandLineArgs()[0];
            if (string.IsNullOrEmpty(entryAssembly) || !File.Exists(entryAssembly))
                return (null, []);
            return (exePath, [Path.GetFullPath(entryAssembly)]);
        }

        return (exePath, []);
    }

    private static int Report(InstallResult result)
    {
        Console.WriteLine(result.Message);
        if (result.BackupPath is not null)
            Console.WriteLine($"Backup of the previous config: {result.BackupPath}");
        return result.Success ? 0 : 1;
    }
}
