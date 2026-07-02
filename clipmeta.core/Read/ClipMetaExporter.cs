using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Read;

/// <summary>A metadata snapshot for a single MP4 file, ready for export.</summary>
public record ExportRecord(
    /// <summary>Full path to the MP4 file.</summary>
    string FilePath,
    /// <summary>All user-facing clipmeta fields. The internal schema field is excluded.</summary>
    IReadOnlyList<(string Field, string Value)> Fields);

/// <summary>Reads clipmeta metadata from a collection of MP4 files for bulk export.</summary>
public static class ClipMetaExporter
{
    /// <summary>
    /// Reads clipmeta fields from each path in <paramref name="filePaths"/> and returns one
    /// <see cref="ExportRecord"/> per successfully parsed file.
    /// The internal schema field is excluded. Malformed or unreadable files are skipped, not
    /// thrown, so one bad clip never aborts the export.
    /// </summary>
    /// <param name="filePaths">File paths to read. May be empty.</param>
    /// <param name="onFileSkipped">
    /// Optional callback invoked with the file's path and the exception that caused it to be
    /// skipped (locked, unreadable, or unparseable). Lets a caller report which file was
    /// skipped instead of the export going silent about it. Defaults to no-op.
    /// </param>
    /// <returns>One record per file that was parsed successfully, in input order.</returns>
    public static IReadOnlyList<ExportRecord> GetRecords(
        IEnumerable<string> filePaths, Action<string, Exception>? onFileSkipped = null)
    {
        var records = new List<ExportRecord>();
        foreach (string path in filePaths)
        {
            BoxNode root;
            try { root = Mp4Parser.ParseFile(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                onFileSkipped?.Invoke(path, ex);
                continue;
            }

            records.Add(new ExportRecord(path, ClipMetaReader.GetUserFields(root)));
        }
        return records;
    }

    /// <summary>
    /// Writes <paramref name="records"/> as CSV: a "file" column, the well-known fields in
    /// <see cref="ClipMetaSchema.KnownFields"/> order, then any custom fields alphabetically.
    /// Lives in Core (not a CLI) so the clipmetascribe <c>--export</c> command and the MCP
    /// <c>library_export</c> tool emit byte-identical CSV.
    /// </summary>
    /// <param name="records">Records to serialize. May be empty (header row only).</param>
    /// <param name="output">Destination writer.</param>
    public static void WriteCsv(IReadOnlyList<ExportRecord> records, TextWriter output)
    {
        var customFields = records
            .SelectMany(r => r.Fields.Select(f => f.Field))
            .Where(f => !ClipMetaSchema.KnownFields.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var columns = new[] { "file" }.Concat(ClipMetaSchema.KnownFields).Concat(customFields).ToList();
        output.WriteLine(string.Join(",", columns.Select(CsvEscape)));

        foreach (var r in records)
        {
            var fieldMap = r.Fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.OrdinalIgnoreCase);
            var row = columns.Select(col => col == "file" ? r.FilePath : fieldMap.GetValueOrDefault(col, ""));
            output.WriteLine(string.Join(",", row.Select(CsvEscape)));
        }
    }

    /// <summary>RFC-4180 quoting: wrap when the value contains a comma, quote, or newline.</summary>
    private static string CsvEscape(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }
}
