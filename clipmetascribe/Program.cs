using ClipMetaCore;
using ClipMetaCore.Abstractions;
using ClipMetaCore.Logging;
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

    private static MetadataMutation BuildMutation(string[] args, string filePath, bool dryRun, bool backup)
    {
        var mutation = new MetadataMutation
        {
            DryRun = dryRun,
            BackupPath = backup ? filePath + ".bak" : null,
        };

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--set" && i + 2 < args.Length)
            {
                string field = ClipMetaSchema.AtomName(args[i + 1]);
                mutation.SetFields[field] = args[i + 2];
                i += 2;
            }
            else if (args[i] == "--append" && i + 2 < args.Length)
            {
                string field = ClipMetaSchema.AtomName(args[i + 1]);
                mutation.AppendFields[field] = args[i + 2];
                i += 2;
            }
            else if (args[i] == "--clear" && i + 1 < args.Length)
            {
                string field = ClipMetaSchema.AtomName(args[i + 1]);
                mutation.DeleteFields.Add(field);
                i += 1;
            }
        }

        return mutation;
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
              clipmetascribe "C:\clips\" --find <field> <value>
              clipmetascribe "C:\clips\" --vocab <field>
              clipmetascribe "C:\clips\" --export [--format json|csv] [--output <path>]
              clipmetascribe "C:\clips\" --index
              clipmetascribe "C:\clips\" --index-search <field> <value>

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
