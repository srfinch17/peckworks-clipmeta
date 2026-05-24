using ClipMetaCore.Abstractions;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Commands;

/// <summary>Handles all metadata write operations for a single file.</summary>
internal static class WriteCommand
{
    /// <summary>Applies the mutation to the file. Returns exit code 0 on success.</summary>
    internal static int Run(string filePath, MetadataMutation mutation, IClipMetaLogger logger)
    {
        new Mp4Writer().WriteMetadata(filePath, mutation, logger);
        return 0;
    }

    /// <summary>
    /// Removes all com.peckworkslab.clipmeta atoms from the file.
    /// Requires explicit --yes or interactive confirmation.
    /// Returns exit code 0 on success or user-cancelled.
    /// </summary>
    internal static int RunClearAll(string filePath, bool dryRun, bool yes, string? backupPath, IClipMetaLogger logger)
    {
        if (!yes && !dryRun)
        {
            Console.Write($"This will remove ALL clipmeta metadata from '{Path.GetFileName(filePath)}'. Type YES to confirm: ");
            string? input = Console.ReadLine();
            if (input?.Trim() != "YES")
            {
                Console.WriteLine("Cancelled.");
                return 0;
            }
        }

        var mutation = new MetadataMutation { ClearAll = true, DryRun = dryRun, BackupPath = backupPath };
        new Mp4Writer().WriteMetadata(filePath, mutation, logger);
        return 0;
    }
}
