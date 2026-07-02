using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Rendering;

namespace ClipMetaView;

/// <summary>
/// Contains the application's main logic as a testable static method.
/// <see cref="Program"/> delegates to this class so that exit-code behavior can be verified in tests.
/// </summary>
public static class AppRunner
{
    /// <summary>Exit code: successful parse and render.</summary>
    public const int ExitSuccess = 0;

    /// <summary>Exit code: invalid arguments, missing file, or wrong file extension.</summary>
    public const int ExitBadArgs = 1;

    /// <summary>Exit code: file exists but cannot be parsed as a valid MP4.</summary>
    public const int ExitParseError = 2;

    /// <summary>
    /// Validates arguments, parses the MP4 file, renders the box tree (or its JSON/definitions
    /// equivalent), and returns an exit code.
    /// </summary>
    /// <param name="args">
    /// Command-line arguments passed to the process. Grammar:
    /// <list type="bullet">
    /// <item><c>&lt;path.mp4&gt;</c>, ASCII tree + summary (the default, unchanged).</item>
    /// <item><c>&lt;path.mp4&gt; --json</c> or <c>--json &lt;path.mp4&gt;</c>, box-tree JSON. The flag
    /// position does not change the output. <c>--json</c> requires a path.</item>
    /// <item><c>--definitions</c>, box-definitions JSON. Needs no path; extra args are ignored.</item>
    /// <item><c>--json</c> and <c>--definitions</c> together, or any unknown <c>--flag</c>, is
    /// <see cref="ExitBadArgs"/>.</item>
    /// </list>
    /// </param>
    /// <param name="writer">
    /// Destination for tree/JSON output. When <c>null</c>, defaults to <see cref="Console.Out"/>.
    /// Tests pass a <see cref="StringWriter"/> here to avoid global console state.
    /// </param>
    /// <returns>
    /// <see cref="ExitSuccess"/>, <see cref="ExitBadArgs"/>, or <see cref="ExitParseError"/>.
    /// </returns>
    public static Task<int> RunAsync(string[] args, TextWriter? writer = null)
    {
        writer ??= Console.Out;

        bool wantJson = false;
        bool wantDefinitions = false;
        string? path = null;

        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--json": wantJson = true; break;
                case "--definitions": wantDefinitions = true; break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"Error: Unknown option: {arg}");
                        return Task.FromResult(ExitBadArgs);
                    }
                    path ??= arg;
                    break;
            }
        }

        if (wantJson && wantDefinitions)
        {
            Console.Error.WriteLine("Error: --json and --definitions cannot be combined.");
            return Task.FromResult(ExitBadArgs);
        }

        // --definitions: clip-independent, needs no path.
        if (wantDefinitions)
        {
            writer.WriteLine(BoxTreeJson.DefinitionsToJson(BoxDefinitions.AllDefinitions()));
            return Task.FromResult(ExitSuccess);
        }

        if (path is null)
        {
            Console.Error.WriteLine("Usage: clipmetaview <path-to-file.mp4> [--json]");
            Console.Error.WriteLine("       clipmetaview --definitions");
            Console.Error.WriteLine("  Displays the internal box/atom structure of an MP4 file.");
            return Task.FromResult(ExitBadArgs);
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Error: File not found: {path}");
            return Task.FromResult(ExitBadArgs);
        }

        if (!Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Error: Only .mp4 files are supported. Got: {path}");
            return Task.FromResult(ExitBadArgs);
        }

        try
        {
            var root = Mp4Parser.ParseFile(path);

            if (wantJson)
            {
                long fileSize = new FileInfo(path).Length;
                writer.WriteLine(BoxTreeJson.ToJson(BoxTreeMapper.Map(root, path, fileSize)));
            }
            else
            {
                TreeRenderer.Render(root, path, writer);
                TreeRenderer.RenderSummary(root, writer);
            }
            return Task.FromResult(ExitSuccess);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            Console.Error.WriteLine($"Error: Failed to parse MP4 file: {ex.Message}");
            return Task.FromResult(ExitParseError);
        }
    }
}
