using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Read;

/// <summary>A single entry in a clipmeta index representing one MP4 file's cached metadata.</summary>
public record IndexEntry(
    /// <summary>Full path to the MP4 file.</summary>
    string FilePath,
    /// <summary>File size in bytes at index build time.</summary>
    long FileSizeBytes,
    /// <summary>UTC last-write time at index build time.</summary>
    DateTimeOffset LastModified,
    /// <summary>All user-facing clipmeta fields at index build time. The schema field is excluded.</summary>
    IReadOnlyList<(string Field, string Value)> Fields);

/// <summary>Snapshot of all indexed MP4 metadata for a directory.</summary>
public record IndexData(
    /// <summary>Directory that was indexed.</summary>
    string Directory,
    /// <summary>UTC timestamp when the index was built.</summary>
    DateTimeOffset Built,
    /// <summary>One entry per MP4 file in the indexed directory.</summary>
    IReadOnlyList<IndexEntry> Entries);

/// <summary>Builds and serializes a metadata index for a directory of MP4 files.</summary>
public static class ClipMetaIndex
{
    /// <summary>The file name written inside the indexed directory.</summary>
    public const string IndexFileName = ".clipmeta-index";

    /// <summary>
    /// Escapes backslashes and newlines in field values for safe serialization.
    /// </summary>
    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    /// <summary>
    /// Unescapes backslashes and newlines in field values after deserialization.
    /// Processes in order: \\n → \n, \\r → \r, \\\\ → \\ to handle all escape sequences correctly.
    /// </summary>
    private static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char next = s[i + 1];
                if (next == 'n') { sb.Append('\n'); i += 2; }
                else if (next == 'r') { sb.Append('\r'); i += 2; }
                else if (next == '\\') { sb.Append('\\'); i += 2; }
                else { sb.Append(s[i]); i++; }
            }
            else { sb.Append(s[i]); i++; }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Scans all .mp4 files in <paramref name="directory"/> and returns an
    /// <see cref="IndexData"/> snapshot. Malformed or unreadable files are silently skipped.
    /// The internal schema field is excluded.
    /// </summary>
    /// <param name="directory">Directory to scan.</param>
    /// <param name="recursive">When true, scans subdirectories recursively.</param>
    /// <returns>An <see cref="IndexData"/> snapshot of the directory.</returns>
    public static IndexData Build(string directory, bool recursive = true)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = new List<IndexEntry>();

        foreach (string path in Directory.EnumerateFiles(directory, "*.mp4", option))
        {
            BoxNode root;
            try { root = Mp4Parser.ParseFile(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            catch (InvalidDataException) { continue; }

            var fields = ClipMetaReader.GetFields(root)
                .Where(f => !f.Field.Equals(ClipMetaSchema.Schema, StringComparison.Ordinal))
                .ToList();

            var info = new FileInfo(path);
            entries.Add(new IndexEntry(
                path,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                fields));
        }

        return new IndexData(directory, DateTimeOffset.UtcNow, entries);
    }

    /// <summary>
    /// Serializes <paramref name="data"/> to <paramref name="writer"/> in the clipmeta index format.
    /// Each entry is preceded by a <c>---</c> separator line. Field values containing newlines or
    /// backslashes are automatically escaped for safe round-tripping.
    /// </summary>
    /// <param name="data">The index data to serialize.</param>
    /// <param name="writer">The text writer to write to.</param>
    public static void Write(IndexData data, TextWriter writer)
    {
        writer.WriteLine("version 1");
        writer.WriteLine($"built {data.Built:O}");
        writer.WriteLine($"directory {Escape(data.Directory)}");
        foreach (var entry in data.Entries)
        {
            writer.WriteLine("---");
            writer.WriteLine($"path {Escape(entry.FilePath)}");
            writer.WriteLine($"size {entry.FileSizeBytes}");
            writer.WriteLine($"modified {entry.LastModified:O}");
            foreach (var (field, value) in entry.Fields)
                writer.WriteLine($"field {Escape(field)} {Escape(value)}");
        }
    }

    /// <summary>Deserializes an <see cref="IndexData"/> from <paramref name="reader"/>.</summary>
    /// <param name="reader">The text reader to read from.</param>
    /// <returns>The deserialized <see cref="IndexData"/>.</returns>
    public static IndexData Read(TextReader reader)
    {
        string directory = "";
        DateTimeOffset built = DateTimeOffset.UtcNow;
        var entries = new List<IndexEntry>();

        bool inHeader = true;
        string? filePath = null;
        long size = 0;
        DateTimeOffset modified = default;
        var fields = new List<(string, string)>();

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line == "---")
            {
                if (!inHeader && filePath != null)
                    entries.Add(new IndexEntry(filePath, size, modified, fields.ToList()));
                inHeader = false;
                filePath = null;
                size = 0;
                modified = default;
                fields = new List<(string, string)>();
                continue;
            }

            int spaceIdx = line.IndexOf(' ');
            if (spaceIdx < 0) continue;
            string keyword = line[..spaceIdx];
            string value = line[(spaceIdx + 1)..];

            if (inHeader)
            {
                if (keyword == "built") built = DateTimeOffset.Parse(value);
                else if (keyword == "directory") directory = Unescape(value);
            }
            else
            {
                if (keyword == "path") filePath = Unescape(value);
                else if (keyword == "size" && long.TryParse(value, out long parsedSize)) size = parsedSize;
                else if (keyword == "modified") modified = DateTimeOffset.Parse(value);
                else if (keyword == "field")
                {
                    int fieldSpace = value.IndexOf(' ');
                    if (fieldSpace >= 0)
                        fields.Add((Unescape(value[..fieldSpace]), Unescape(value[(fieldSpace + 1)..])));
                }
            }
        }

        // Capture last entry if file ended without a trailing ---
        if (!inHeader && filePath != null)
            entries.Add(new IndexEntry(filePath, size, modified, fields.ToList()));

        return new IndexData(directory, built, entries);
    }

    /// <summary>Writes <paramref name="data"/> to <paramref name="filePath"/> using UTF-8.</summary>
    /// <param name="data">The index data to write.</param>
    /// <param name="filePath">Destination file path.</param>
    public static void WriteToFile(IndexData data, string filePath)
    {
        using var writer = new StreamWriter(filePath, append: false, System.Text.Encoding.UTF8);
        Write(data, writer);
    }

    /// <summary>Reads an <see cref="IndexData"/> from <paramref name="filePath"/> using UTF-8.</summary>
    /// <param name="filePath">Source file path.</param>
    /// <returns>The deserialized <see cref="IndexData"/>.</returns>
    public static IndexData ReadFromFile(string filePath)
    {
        using var reader = new StreamReader(filePath, System.Text.Encoding.UTF8);
        return Read(reader);
    }
}
