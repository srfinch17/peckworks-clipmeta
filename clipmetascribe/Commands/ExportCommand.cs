using System.Text;
using ClipMetaCore.Read;

namespace ClipMetaScribe.Commands;

/// <summary>Exports clipmeta metadata records to JSON or CSV format.</summary>
internal static class ExportCommand
{
    /// <summary>
    /// Formats <paramref name="records"/> as JSON or CSV and writes to <paramref name="output"/>
    /// (defaults to <see cref="Console.Out"/>).
    /// </summary>
    /// <returns>0 on success, 1 if format is unrecognized.</returns>
    internal static int Run(IReadOnlyList<ExportRecord> records, string format, TextWriter? output = null)
    {
        output ??= Console.Out;

        return format.ToLowerInvariant() switch
        {
            "json" => WriteJson(records, output),
            "csv"  => WriteCsv(records, output),
            _      => WriteFormatError(format),
        };
    }

    private static int WriteJson(IReadOnlyList<ExportRecord> records, TextWriter output)
    {
        output.WriteLine("[");
        for (int i = 0; i < records.Count; i++)
        {
            output.WriteLine("  {");
            var r = records[i];
            var pairs = new List<string> { $"\"file\": \"{JsonEscape(r.FilePath)}\"" };
            pairs.AddRange(r.Fields.Select(f => $"\"{JsonEscape(f.Field)}\": \"{JsonEscape(f.Value)}\""));

            for (int j = 0; j < pairs.Count; j++)
                output.WriteLine($"    {pairs[j]}{(j < pairs.Count - 1 ? "," : "")}");

            output.WriteLine($"  }}{(i < records.Count - 1 ? "," : "")}");
        }
        output.WriteLine("]");
        return 0;
    }

    private static int WriteCsv(IReadOnlyList<ExportRecord> records, TextWriter output)
    {
        // CSV formatting lives in Core so the MCP server's library_export tool emits the
        // exact same bytes as this command (same column order, same quoting).
        ClipMetaExporter.WriteCsv(records, output);
        return 0;
    }

    private static int WriteFormatError(string format)
    {
        Console.Error.WriteLine($"Error: Unknown format '{format}'. Use 'json' or 'csv'.");
        return 1;
    }

    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

}
