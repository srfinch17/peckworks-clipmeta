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
    /// The internal schema field is excluded. Malformed or unreadable files are silently skipped.
    /// </summary>
    /// <param name="filePaths">File paths to read. May be empty.</param>
    /// <returns>One record per file that was parsed successfully, in input order.</returns>
    public static IReadOnlyList<ExportRecord> GetRecords(IEnumerable<string> filePaths)
    {
        var records = new List<ExportRecord>();
        foreach (string path in filePaths)
        {
            BoxNode root;
            try { root = Mp4Parser.ParseFile(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            catch (InvalidDataException) { continue; }

            var fields = ClipMetaReader.GetFields(root)
                .Where(f => !f.Field.Equals(ClipMetaSchema.Schema, StringComparison.Ordinal))
                .ToList();
            records.Add(new ExportRecord(path, fields));
        }
        return records;
    }
}
