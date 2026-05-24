using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Read;

/// <summary>Result of a vocabulary enumeration over a directory of MP4 files.</summary>
public record VocabResult(
    /// <summary>Distinct values mapped to the number of clips that contain each value.</summary>
    IReadOnlyDictionary<string, int> Counts,
    /// <summary>Number of clips that had at least one value for the queried field.</summary>
    int ClipsWithField);

/// <summary>Enumerates distinct values for a metadata field across a directory of MP4 files.</summary>
public static class ClipMetaVocab
{
    /// <summary>
    /// Scans .mp4 files in <paramref name="directory"/> and returns distinct values for
    /// <paramref name="field"/> with per-value clip counts.
    /// Pipe-separated fields (tags, players, timecode) are split on '|'; each item is counted
    /// individually. Malformed or unreadable files are silently skipped.
    /// </summary>
    /// <param name="directory">Directory to search.</param>
    /// <param name="field">Bare field name (e.g. "tags"), matched case-insensitively.</param>
    /// <param name="recursive">When true, searches subdirectories. Default: true.</param>
    /// <returns>A <see cref="VocabResult"/> with value counts and total clips with the field.</returns>
    public static VocabResult Enumerate(string directory, string field, bool recursive = true)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int clipsWithField = 0;
        bool isPipeField = ClipMetaSchema.PipeFields.Any(f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (string path in Directory.EnumerateFiles(directory, "*.mp4", option))
        {
            BoxNode root;
            try { root = Mp4Parser.ParseFile(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            catch (InvalidDataException) { continue; }

            bool clipHasField = false;
            foreach (var (f, v) in ClipMetaReader.GetFields(root))
            {
                if (!f.Equals(field, StringComparison.OrdinalIgnoreCase)) continue;
                clipHasField = true;

                if (isPipeField)
                {
                    foreach (string item in v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        counts[item] = counts.TryGetValue(item, out int c) ? c + 1 : 1;
                }
                else
                {
                    counts[v] = counts.TryGetValue(v, out int c) ? c + 1 : 1;
                }
            }

            if (clipHasField) clipsWithField++;
        }

        return new VocabResult(counts, clipsWithField);
    }
}
