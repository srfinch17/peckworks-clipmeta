using ClipMetaCore.Mp4;
using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Displays all com.peckworkslab.clipmeta metadata fields from an MP4 file.</summary>
internal static class ListCommand
{
    /// <summary>
    /// Parses <paramref name="filePath"/>, extracts ClipMeta fields, and writes
    /// formatted output to <paramref name="output"/> (defaults to <see cref="Console.Out"/>).
    /// </summary>
    /// <returns>Exit code 0 on success.</returns>
    internal static int Run(string filePath, TextWriter? output = null)
    {
        output ??= Console.Out;

        // Deliberately GetFields, not GetUserFields: --list is the raw inspection view and shows
        // everything stored in the file, including the internal schema-version field. Every other
        // user-facing surface (stats, export, index, MCP tools) filters internals out.
        var root   = Mp4Parser.ParseFile(filePath);
        var fields = ClipMetaReader.GetFields(root);

        output.WriteLine(Path.GetFileName(filePath));

        if (fields.Count == 0)
        {
            output.WriteLine("  (no clipmeta metadata)");
            return 0;
        }

        int pad = fields.Max(f => f.Field.Length);
        foreach (var (field, value) in fields)
            output.WriteLine($"  {field.PadRight(pad)}  {value}");

        return 0;
    }
}
