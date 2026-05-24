using ClipMetaCore.Mp4;
using ClipMetaView.Rendering;

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
    /// Validates arguments, parses the MP4 file, renders the box tree, and returns an exit code.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the process.</param>
    /// <param name="writer">
    /// Destination for tree output. When <c>null</c>, defaults to <see cref="Console.Out"/>.
    /// Tests pass a <see cref="StringWriter"/> here to avoid global console state.
    /// </param>
    /// <returns>
    /// <see cref="ExitSuccess"/>, <see cref="ExitBadArgs"/>, or <see cref="ExitParseError"/>.
    /// </returns>
    public static Task<int> RunAsync(string[] args, TextWriter? writer = null)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: clipmetaview <path-to-file.mp4>");
            Console.Error.WriteLine("  Displays the internal box/atom structure of an MP4 file.");
            Console.Error.WriteLine("  Editable metadata fields are highlighted.");
            return Task.FromResult(ExitBadArgs);
        }

        string filePath = args[0];

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"Error: File not found: {filePath}");
            return Task.FromResult(ExitBadArgs);
        }

        if (!Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Error: Only .mp4 files are supported. Got: {filePath}");
            return Task.FromResult(ExitBadArgs);
        }

        try
        {
            var root = Mp4Parser.ParseFile(filePath);
            TreeRenderer.Render(root, filePath, writer);
            TreeRenderer.RenderSummary(root, writer);
            return Task.FromResult(ExitSuccess);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            Console.Error.WriteLine($"Error: Failed to parse MP4 file: {ex.Message}");
            return Task.FromResult(ExitParseError);
        }
    }
}
