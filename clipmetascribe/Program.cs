using ClipMetaCore;
using ClipMetaCore.Abstractions;
using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;

namespace ClipMetaScribe;

internal static class Program
{
    // async signature retained for future await-able operations
#pragma warning disable CS1998
    private static async Task<int> Main(string[] args)
#pragma warning restore CS1998
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        if (ContainsFlag(args, "--version"))
        {
            Console.WriteLine("clipmetascribe 1.0.0 (ClipMetaCore 1.0.0)");
            return 0;
        }

        bool verbose = args.Contains("--verbose");
        bool dryRun = args.Contains("--dry-run");
        bool yes = args.Contains("--yes");
        bool backup = args.Contains("--backup");

        string? logPath = GetFlag(args, "--log");
        // --log present but its "value" is missing or is the next flag — without this check the
        // logger would happily create a file literally named "--set" (and the swallowed flag
        // would still be parsed as a flag elsewhere, compounding the confusion).
        if (ContainsFlag(args, "--log") && (logPath == null || KnownFlags.Contains(logPath)))
        {
            Console.Error.WriteLine("Error: --log is missing a file path. Usage: --log <path>");
            return 1;
        }

        IClipMetaLogger logger = logPath != null
            ? new FileLogger(logPath, verbose ? LogLevel.Verbose : LogLevel.Simple)
            : NullLogger.Instance;

        // First positional argument (must not start with --) is the file path
        string? filePath = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null;

        // --find takes a directory path, so handle it before the File.Exists check
        if (ContainsFlag(args, "--find"))
        {
            if (filePath == null || !Directory.Exists(filePath))
            {
                Console.Error.WriteLine("Error: --find requires a valid directory as the first argument.");
                return 1;
            }
            var (findField, findValue) = GetFindArgs(args);
            if (findField == null || findValue == null ||
                findField.StartsWith("--", StringComparison.Ordinal) ||
                findValue.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Error: --find requires a field name and value: --find <field> <value>");
                return 1;
            }
            try
            {
                return FindCommand.Run(filePath, findField, findValue);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }

        if (ContainsFlag(args, "--vocab"))
        {
            if (filePath == null || !Directory.Exists(filePath))
            {
                Console.Error.WriteLine("Error: --vocab requires a valid directory as the first argument.");
                return 1;
            }
            string? vocabField = GetFlag(args, "--vocab");
            if (vocabField == null || vocabField.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Error: --vocab requires a field name: --vocab <field>");
                return 1;
            }
            try
            {
                return VocabCommand.Run(filePath, vocabField);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }

        if (ContainsFlag(args, "--index"))
        {
            if (filePath == null || !Directory.Exists(filePath))
            {
                Console.Error.WriteLine("Error: --index requires a valid directory as the first argument.");
                return 1;
            }
            try
            {
                return IndexCommand.Run(filePath);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }

        if (ContainsFlag(args, "--index-search"))
        {
            if (filePath == null || !Directory.Exists(filePath))
            {
                Console.Error.WriteLine("Error: --index-search requires a valid directory as the first argument.");
                return 1;
            }
            var (isField, isValue) = GetIndexSearchArgs(args);
            if (isField == null || isValue == null ||
                isField.StartsWith("--", StringComparison.Ordinal) ||
                isValue.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Error: --index-search requires a field name and value: --index-search <field> <value>");
                return 1;
            }
            try
            {
                return IndexSearchCommand.Run(filePath, isField, isValue);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }

        if (ContainsFlag(args, "--export"))
        {
            if (filePath == null)
            {
                Console.Error.WriteLine("Error: --export requires a file or directory path as the first argument.");
                return 1;
            }
            string exportFormat = GetFlag(args, "--format") ?? "json";
            if (exportFormat.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Error: --format requires a value: --format json|csv");
                return 1;
            }
            string? outputPath = GetFlag(args, "--output");
            if (outputPath?.StartsWith("--", StringComparison.Ordinal) == true)
            {
                Console.Error.WriteLine("Error: --output requires a file path.");
                return 1;
            }

            IEnumerable<string> exportPaths;
            if (Directory.Exists(filePath))
                exportPaths = Directory.EnumerateFiles(filePath, "*.mp4", SearchOption.AllDirectories);
            else if (File.Exists(filePath))
            {
                if (!filePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"Error: '{filePath}' is not an MP4 file.");
                    return 1;
                }
                exportPaths = new[] { filePath };
            }
            else
            {
                Console.Error.WriteLine($"Error: Path not found: {filePath}");
                return 1;
            }

            StreamWriter? fileWriter = null;
            try
            {
                TextWriter exportOutput = Console.Out;
                if (outputPath != null)
                {
                    fileWriter = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
                    exportOutput = fileWriter;
                }
                var records = ClipMetaExporter.GetRecords(exportPaths);
                return ExportCommand.Run(records, exportFormat, exportOutput);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
            finally
            {
                fileWriter?.Dispose();
            }
        }

        // A directory plus a write operation → batch the write across every .mp4 in the directory.
        // (Directory READ commands — find/vocab/index/export — were handled and returned above.)
        if (filePath != null && Directory.Exists(filePath) && IsWriteOp(args))
        {
            try
            {
                return RunBatch(args, filePath, dryRun, yes, backup, logger);
            }
            catch (ArgumentException ex)   // e.g. --copy-from missing its source path
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
            // Enumerating the directory (or reading a --copy-from source) can surface an IO or
            // permission error — e.g. a disconnected share or an unreadable subfolder. Report it
            // cleanly instead of crashing the batch with an unhandled stack trace.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }
        }

        if (filePath == null || !File.Exists(filePath))
        {
            if (filePath != null && Path.HasExtension(filePath))
            {
                Console.Error.WriteLine($"Error: File not found: {filePath}");
                return 1;
            }
            PrintUsage();
            return 1;
        }

        if (!Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Error: Only .mp4 files are supported: {filePath}");
            return 1;
        }

        try
        {
            if (ContainsFlag(args, "--list"))
            {
                return ListCommand.Run(filePath);
            }

            if (ContainsFlag(args, "--stats"))
            {
                return StatsCommand.Run(filePath);
            }

            if (ContainsFlag(args, "--clear-all"))
            {
                return WriteCommand.RunClearAll(filePath, dryRun, yes, backup ? filePath + ".bak" : null, logger);
            }

            if (ContainsFlag(args, "--copy-from"))
            {
                int cfIndex = Array.FindIndex(args, a => a.Equals("--copy-from", StringComparison.OrdinalIgnoreCase));
                string source = RequireArg(args, cfIndex, 1, "a source .mp4 path");
                // Explicit --set/--append/--clear in the same invocation layer over the copied fields.
                var extra = BuildMutation(args, filePath, dryRun, backup);
                return CopyTagsCommand.Run(filePath, source, extra, logger);
            }

            var mutation = BuildMutation(args, filePath, dryRun, backup);

            if (mutation.SetFields.Count > 0 || mutation.AppendFields.Count > 0 || mutation.DeleteFields.Count > 0)
            {
                return WriteCommand.Run(filePath, mutation, logger);
            }

            Console.Error.WriteLine("Error: No write operation specified. Use --set, --append, --clear, --clear-all, --list, --stats, or see --vocab / --find for directory commands.");
            PrintUsage();
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (UnsupportedFormatException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        // Bad user input detected past arg parsing: invalid --set rating/timecode values
        // (Normalizer throws ArgumentException) or malformed write operations (BuildMutation).
        // Without this catch these surfaced as a raw .NET stack trace instead of an error line.
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        // Operations the write engine refuses by design, e.g. appending to a non-text atom or
        // updating a non-freeform (©nam-style) atom. User-fixable, so exit 1, not a crash.
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Verification failed: {ex.Message}");
            return 3;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Every flag this tool understands. Used to catch the "swallowed flag" class of mistake:
    /// in <c>--set notes --backup</c> the user forgot the value, and without this check
    /// "--backup" would be silently stored as the notes text (while ALSO still activating the
    /// backup flag, since flag detection scans the whole arg list independently).
    /// </summary>
    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--set", "--append", "--clear", "--clear-all", "--copy-from", "--list", "--stats",
        "--find", "--vocab", "--index", "--index-search", "--export",
        "--format", "--output", "--dry-run", "--backup", "--verbose",
        "--log", "--yes", "--version",
    };

    /// <summary>
    /// Validates that a positional argument following a flag is usable, throwing a precise
    /// <see cref="ArgumentException"/> (which Main reports as exit code 1) when it is missing
    /// or is actually the NEXT flag. Values that merely look dashy (e.g. notes of
    /// "--great clip--") are accepted — only exact matches of known flags are rejected,
    /// so expressiveness is not lost.
    /// </summary>
    /// <param name="args">The full argument array.</param>
    /// <param name="flagIndex">Index of the flag whose argument is being read.</param>
    /// <param name="argOffset">1 for the first argument after the flag, 2 for the second.</param>
    /// <param name="description">What the argument is, for the error message ("a field name").</param>
    private static string RequireArg(string[] args, int flagIndex, int argOffset, string description)
    {
        string flag = args[flagIndex];
        int index = flagIndex + argOffset;
        if (index >= args.Length)
            throw new ArgumentException($"{flag} is missing {description}. Usage: {FlagUsage(flag)}");
        string value = args[index];
        if (KnownFlags.Contains(value))
            throw new ArgumentException(
                $"{flag} expected {description} but found the flag '{value}'. " +
                $"Usage: {FlagUsage(flag)}");
        return value;
    }

    /// <summary>Usage string per write flag, shown in argument errors.</summary>
    private static string FlagUsage(string flag) => flag.ToLowerInvariant() switch
    {
        "--set" => "--set <field> <value>",
        "--append" => "--append <field> <value>",
        "--clear" => "--clear <field>",
        "--log" => "--log <path>",
        _ => $"{flag} <value>",
    };

    /// <summary>
    /// Collects all --set/--append/--clear operations from the argument list into a single
    /// <see cref="MetadataMutation"/>. Internal (not private) so argument-validation tests can
    /// drive it directly without spawning the executable.
    /// </summary>
    /// <exception cref="ArgumentException">When an operation is missing its field or value.</exception>
    internal static MetadataMutation BuildMutation(string[] args, string filePath, bool dryRun, bool backup)
    {
        var mutation = new MetadataMutation
        {
            DryRun = dryRun,
            BackupPath = backup ? filePath + ".bak" : null,
        };

        for (int i = 0; i < args.Length; i++)
        {
            // Flag names match case-insensitively, consistent with ContainsFlag/GetFlag.
            if (args[i].Equals("--set", StringComparison.OrdinalIgnoreCase))
            {
                string field = RequireArg(args, i, 1, "a field name");
                string value = RequireArg(args, i, 2, "a value");
                mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
                i += 2;
            }
            else if (args[i].Equals("--append", StringComparison.OrdinalIgnoreCase))
            {
                string field = RequireArg(args, i, 1, "a field name");
                string value = RequireArg(args, i, 2, "a value");
                mutation.AppendFields[ClipMetaSchema.AtomName(field)] = value;
                i += 2;
            }
            else if (args[i].Equals("--clear", StringComparison.OrdinalIgnoreCase))
            {
                string field = RequireArg(args, i, 1, "a field name");
                mutation.DeleteFields.Add(ClipMetaSchema.AtomName(field));
                i += 1;
            }
        }

        return mutation;
    }

    /// <summary>True when the arguments contain any metadata-write operation (the ops that batch
    /// over a directory). Directory READ commands are dispatched earlier and are not included.</summary>
    private static bool IsWriteOp(string[] args) =>
        ContainsFlag(args, "--set") || ContainsFlag(args, "--append") ||
        ContainsFlag(args, "--clear") || ContainsFlag(args, "--clear-all") ||
        ContainsFlag(args, "--copy-from");

    /// <summary>
    /// Applies a write operation to every .mp4 in <paramref name="directory"/> (recursive),
    /// delegating per-file iteration and failure isolation to <see cref="BatchCommand"/>. Builds
    /// the op-specific per-file mutation factory; confirms a folder-wide --clear-all once; parses a
    /// --copy-from source a single time and skips that source clip.
    /// </summary>
    private static int RunBatch(string[] args, string directory, bool dryRun, bool yes, bool backup, IClipMetaLogger logger)
    {
        var files = Directory.EnumerateFiles(directory, "*.mp4", SearchOption.AllDirectories)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                             .ToList();
        if (files.Count == 0)
        {
            Console.WriteLine($"No .mp4 files found in '{directory}'.");
            return 0;
        }

        if (ContainsFlag(args, "--clear-all"))
        {
            if (!yes && !dryRun)
            {
                Console.Write(
                    $"This will remove ALL clipmeta metadata from {files.Count} clip(s) under " +
                    $"'{directory}'. Type YES to confirm: ");
                if (Console.ReadLine()?.Trim() != "YES")
                {
                    Console.WriteLine("Cancelled.");
                    return 0;
                }
            }
            return BatchCommand.Run(files,
                file => new MetadataMutation
                {
                    ClearAll = true,
                    DryRun = dryRun,
                    BackupPath = backup ? file + ".bak" : null,
                },
                logger, dryRun: dryRun);
        }

        if (ContainsFlag(args, "--copy-from"))
        {
            int cfIndex = Array.FindIndex(args, a => a.Equals("--copy-from", StringComparison.OrdinalIgnoreCase));
            string source = RequireArg(args, cfIndex, 1, "a source .mp4 path");
            if (!File.Exists(source) ||
                !Path.GetExtension(source).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Error: --copy-from source must be an existing .mp4 file: {source}");
                return 1;
            }

            BoxNode sourceTree;
            try
            {
                sourceTree = Mp4Parser.ParseFile(source);
            }
            catch (Exception ex) when (ex is UnsupportedFormatException or InvalidDataException or IOException)
            {
                Console.Error.WriteLine($"Error: cannot read --copy-from source '{source}': {ex.Message}");
                return 1;
            }

            string sourceFull = Path.GetFullPath(source);
            return BatchCommand.Run(files, file =>
            {
                // Copying a clip onto itself is a no-op, not a failure — skip the source.
                if (string.Equals(Path.GetFullPath(file), sourceFull, StringComparison.OrdinalIgnoreCase))
                    return null;

                var mutation = ClipMetaCopier.BuildCopyMutation(sourceTree);
                var extra = BuildMutation(args, file, dryRun, backup);   // explicit ops layer on top
                foreach (var (key, value) in extra.SetFields) mutation.SetFields[key] = value;
                foreach (var (key, value) in extra.AppendFields) mutation.AppendFields[key] = value;
                foreach (var key in extra.DeleteFields) mutation.DeleteFields.Add(key);
                mutation.DryRun = extra.DryRun;
                mutation.BackupPath = extra.BackupPath;
                return mutation;
            }, logger, dryRun: dryRun);
        }

        // --set / --append / --clear across the folder: a fresh mutation per file.
        return BatchCommand.Run(files, file => BuildMutation(args, file, dryRun, backup), logger, dryRun: dryRun);
    }

    private static bool ContainsFlag(string[] args, string flag)
        => Array.Exists(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetFlag(string[] args, string flag)
    {
        int idx = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    private static (string? field, string? value) GetFindArgs(string[] args)
    {
        int idx = Array.FindIndex(args, a => a.Equals("--find", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return (null, null);
        string? field = idx + 1 < args.Length ? args[idx + 1] : null;
        string? value = idx + 2 < args.Length ? args[idx + 2] : null;
        return (field, value);
    }

    private static (string? field, string? value) GetIndexSearchArgs(string[] args)
    {
        int idx = Array.FindIndex(args, a => a.Equals("--index-search", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return (null, null);
        string? field = idx + 1 < args.Length ? args[idx + 1] : null;
        string? value = idx + 2 < args.Length ? args[idx + 2] : null;
        return (field, value);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            clipmetascribe — MP4 metadata writer (Peckworks Lab)

            Usage:
              clipmetascribe "clip.mp4" --list
              clipmetascribe "clip.mp4" --stats
              clipmetascribe "clip.mp4" --export [--format json|csv] [--output <path>]
              clipmetascribe "clip.mp4" --set <field> <value>
              clipmetascribe "clip.mp4" --append <field> <value>
              clipmetascribe "clip.mp4" --clear <field>
              clipmetascribe "clip.mp4" --clear-all [--yes]
              clipmetascribe "dest.mp4" --copy-from "source.mp4"
              clipmetascribe "C:\clips\" --find <field> <value>
              clipmetascribe "C:\clips\" --vocab <field>
              clipmetascribe "C:\clips\" --export [--format json|csv] [--output <path>]
              clipmetascribe "C:\clips\" --index
              clipmetascribe "C:\clips\" --index-search <field> <value>

            Batch (a write op on a directory applies to every .mp4 in it, recursively):
              clipmetascribe "C:\clips\" --set <field> <value>
              clipmetascribe "C:\clips\" --copy-from "source.mp4"
              clipmetascribe "C:\clips\" --clear-all --yes

            Fields:  game  players  tags  timecode  rating  notes  (or any custom name)

            Examples:
              clipmetascribe "clip.mp4" --list
              clipmetascribe "clip.mp4" --stats
              clipmetascribe "clip.mp4" --export
              clipmetascribe "clip.mp4" --export --format csv
              clipmetascribe "clip.mp4" --export --format json --output metadata.json
              clipmetascribe "clip.mp4" --set game "Team Fortress 2"
              clipmetascribe "clip.mp4" --set tags "rocket jump|headshot"
              clipmetascribe "clip.mp4" --append tags "market garden"
              clipmetascribe "clip.mp4" --clear tags
              clipmetascribe "clip.mp4" --clear-all --yes
              clipmetascribe "dest.mp4" --copy-from "source.mp4"
              clipmetascribe "clip.mp4" --set game "TF2" --append tags "headshot" --set rating "4"
              clipmetascribe "C:\clips\" --find game "Team Fortress 2"
              clipmetascribe "C:\clips\" --find tags "headshot"
              clipmetascribe "C:\clips\" --vocab game
              clipmetascribe "C:\clips\" --vocab tags
              clipmetascribe "C:\clips\" --export --format csv --output library.csv
              clipmetascribe "C:\clips\" --index
              clipmetascribe "C:\clips\" --index-search game "Team Fortress 2"

            Options:
              --dry-run         Preview changes without writing
              --backup          Keep .bak copy of original before write
              --verbose         Verbose logging (requires --log)
              --log <path>      Write structured log to file
              --yes             Skip confirmation prompts
              --version         Print version and exit
              --format json|csv Export format (default: json). Use with --export.
              --output <path>   Write export to file instead of stdout. Use with --export.

            Exit codes:  0=success  1=bad args / not found  2=write failure  3=verification failure
            """);
    }
}
