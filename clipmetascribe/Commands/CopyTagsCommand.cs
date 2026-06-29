using ClipMetaCore.Abstractions;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Commands;

/// <summary>
/// Copies clipmeta metadata from a source clip onto a destination clip using MERGE semantics:
/// the source's user fields are set on the destination; fields the source does not carry are left
/// untouched. Reads the source read-only and routes the destination through the unchanged
/// write-safety chain (temp → re-parse verify → atomic swap).
/// </summary>
internal static class CopyTagsCommand
{
    /// <summary>
    /// Sets <paramref name="sourcePath"/>'s clipmeta user fields onto <paramref name="destPath"/>,
    /// layering any explicit operations in <paramref name="extra"/> (its <c>--set</c> overrides a
    /// copied field; its <c>--append</c>/<c>--clear</c> apply too) and carrying its DryRun /
    /// BackupPath. Returns the process exit code.
    /// </summary>
    internal static int Run(string destPath, string sourcePath, MetadataMutation extra, IClipMetaLogger logger)
    {
        if (PathsEqual(destPath, sourcePath))
        {
            Console.Error.WriteLine("Error: source and destination are the same file.");
            return 1;
        }
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Error: source file not found: {sourcePath}");
            return 1;
        }
        if (!Path.GetExtension(sourcePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Error: source must be an .mp4 file: {sourcePath}");
            return 1;
        }

        // May throw UnsupportedFormatException/InvalidDataException, Program maps those to exit 1.
        BoxNode source = Mp4Parser.ParseFile(sourcePath);
        MetadataMutation mutation = ClipMetaCopier.BuildCopyMutation(source);
        MergeExplicit(mutation, extra);

        if (mutation.SetFields.Count == 0 && mutation.AppendFields.Count == 0 &&
            mutation.DeleteFields.Count == 0 && !mutation.ClearAll)
        {
            Console.WriteLine(
                $"Source '{Path.GetFileName(sourcePath)}' has no clipmeta fields to copy; nothing written.");
            return 0;
        }

        new Mp4Writer().WriteMetadata(destPath, mutation, logger);
        return 0;
    }

    /// <summary>Layers explicit command-line operations over the copied fields and carries the
    /// write options (DryRun / BackupPath). An explicit <c>--set</c> wins over a copied field.</summary>
    private static void MergeExplicit(MetadataMutation into, MetadataMutation extra)
    {
        foreach (var (key, value) in extra.SetFields) into.SetFields[key] = value;
        foreach (var (key, value) in extra.AppendFields) into.AppendFields[key] = value;
        foreach (var key in extra.DeleteFields) into.DeleteFields.Add(key);
        into.DryRun = extra.DryRun;
        into.BackupPath = extra.BackupPath;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
