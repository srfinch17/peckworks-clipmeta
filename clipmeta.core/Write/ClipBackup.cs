using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ClipMetaCore.Abstractions;
using ClipMetaCore.Mp4;

namespace ClipMetaCore.Write;

/// <summary>One backup file found in a library, with the clip it belongs to and its facts.</summary>
public record BackupInfo(
    /// <summary>Full path to the .bak-&lt;timestamp&gt; file.</summary>
    string BackupPath,
    /// <summary>Full path to the clip this backup is a copy of (the name minus the .bak suffix).</summary>
    string ClipPath,
    /// <summary>Size of the backup file in bytes.</summary>
    long SizeBytes,
    /// <summary>When the backup was taken, parsed from its timestamp suffix (local clock, stored UTC).</summary>
    DateTimeOffset TakenUtc);

/// <summary>
/// Owns the metadata-write backup convention end to end: how a backup is named, how to tell a
/// backup from any other file, how to list them, and how to restore one safely. Centralizing it
/// here means the writer (which creates backups) and the management tools (which list/restore/
/// prune them) can never drift apart on the naming scheme.
///
/// Convention: a backup of <c>clip.mp4</c> is <c>clip.mp4.bak-yyyyMMdd-HHmmss</c> — the full clip
/// name (extension included) plus a <c>.bak-</c> marker and a 15-char local timestamp. The clip
/// name is recovered by stripping exactly that suffix, so it round-trips for any clip name.
/// </summary>
public static class ClipBackup
{
    private const string Marker = ".bak-";

    /// <summary>strict timestamp format: 8 date digits, a hyphen, 6 time digits (yyyyMMdd-HHmmss).</summary>
    private const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// The backup path for a clip at the current local time. The writer passes this as the
    /// <see cref="MetadataMutation.BackupPath"/>; File.Replace then saves the pre-write original
    /// there. Seconds-resolution timestamp: two writes within the same second would collide, so
    /// callers needing rapid repeats must serialize (the MCP server's single-flight write lock
    /// already does).
    /// </summary>
    public static string MakeBackupPath(string clipPath) =>
        $"{clipPath}{Marker}{DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Recognizes a backup file made by <see cref="MakeBackupPath"/> and recovers the clip it
    /// backs up. Rejects anything whose suffix is not <c>.bak-</c> followed by a valid
    /// <see cref="StampFormat"/> stamp — so a user's unrelated <c>.bak</c> file, or a
    /// <c>.bak-notes</c>, is never mistaken for one of ours (important: prune deletes these).
    /// </summary>
    public static bool TryGetClipForBackup(string backupPath, [NotNullWhen(true)] out string? clipPath)
    {
        clipPath = null;
        string name = Path.GetFileName(backupPath);

        int markerIndex = name.LastIndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        string stamp = name[(markerIndex + Marker.Length)..];
        if (!DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
        {
            return false; // suffix after .bak- is not our timestamp — not our backup
        }

        string? dir = Path.GetDirectoryName(backupPath);
        string clipName = name[..markerIndex];
        if (clipName.Length == 0)
            return false; // ".bak-..." with no clip name in front

        clipPath = dir is null ? clipName : Path.Combine(dir, clipName);
        return true;
    }

    /// <summary>
    /// Parses the timestamp out of a backup name into a UTC instant. Assumes the name already
    /// passed <see cref="TryGetClipForBackup"/>.
    /// </summary>
    private static DateTimeOffset ParseTaken(string backupPath)
    {
        string name = Path.GetFileName(backupPath);
        string stamp = name[(name.LastIndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..];
        var local = DateTime.ParseExact(stamp, StampFormat, CultureInfo.InvariantCulture);
        return new DateTimeOffset(local.ToUniversalTime(), TimeSpan.Zero);
    }

    /// <summary>
    /// Lists backups under <paramref name="directory"/> (recursively), newest first. When
    /// <paramref name="clipPath"/> is given, only backups of that specific clip are returned.
    /// Files that don't match the backup convention are ignored.
    /// </summary>
    /// <param name="directory">Directory to scan (typically the library root).</param>
    /// <param name="clipPath">Optional clip whose backups to list; null = all backups.</param>
    public static IReadOnlyList<BackupInfo> ListBackups(string directory, string? clipPath = null)
    {
        var results = new List<BackupInfo>();
        // The marker is mid-name, not an extension, so enumerate everything and filter.
        foreach (string path in Directory.EnumerateFiles(directory, "*" + Marker + "*", SearchOption.AllDirectories))
        {
            if (!TryGetClipForBackup(path, out string? owningClip))
                continue;
            if (clipPath is not null &&
                !string.Equals(owningClip, Path.GetFullPath(clipPath), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFullPath(owningClip), Path.GetFullPath(clipPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            results.Add(new BackupInfo(path, owningClip, new FileInfo(path).Length, ParseTaken(path)));
        }
        return results.OrderByDescending(b => b.TakenUtc).ThenByDescending(b => b.BackupPath).ToList();
    }

    /// <summary>
    /// Restores <paramref name="clipPath"/> from <paramref name="backupPath"/>: the backup is
    /// first validated as a complete, parseable MP4 (the same whole-file-accounting gate the
    /// writer applies before any write), then atomically swapped into place via a temp file and
    /// <see cref="File.Replace(string,string,string?)"/> — the writer's golden rule. A backup
    /// that fails validation is refused with the live clip untouched. The backup file itself is
    /// left on disk (restoring does not consume it).
    /// </summary>
    /// <param name="backupPath">The .bak file to restore from.</param>
    /// <param name="clipPath">The clip to overwrite.</param>
    /// <param name="logger">Write logger.</param>
    /// <exception cref="FileNotFoundException">The backup does not exist.</exception>
    /// <exception cref="InvalidDataException">The backup is not a complete, parseable MP4.</exception>
    public static void Restore(string backupPath, string clipPath, IClipMetaLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!File.Exists(backupPath))
            throw new FileNotFoundException($"Backup not found: {backupPath}", backupPath);

        // Validate the backup before it touches the live clip. A backup is just a file; if it was
        // truncated by a crash mid-copy or tampered with, restoring it blindly would destroy a
        // good clip with a bad one. ParseFile + the writer's coverage gate together prove it is a
        // whole, well-formed MP4.
        try
        {
            BoxNode root = Mp4Parser.ParseFile(backupPath);
            Mp4Writer.VerifyWholeFileAccounted(root, backupPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or UnsupportedFormatException)
        {
            throw new InvalidDataException(
                $"Refusing to restore: '{Path.GetFileName(backupPath)}' is not a complete, " +
                $"valid MP4 ({ex.Message}). The current file was left untouched.", ex);
        }

        // Copy the backup to a unique temp sibling of the clip, then atomically replace — the
        // clip is never partially written. (Copy first so a failure mid-copy leaves the clip
        // intact; File.Replace is the single committing step.)
        string tempPath = $"{clipPath}.{Guid.NewGuid():N}.restore.tmp";
        try
        {
            File.Copy(backupPath, tempPath, overwrite: false);
            if (File.Exists(clipPath))
            {
                File.Replace(tempPath, clipPath, destinationBackupFileName: null);
            }
            else
            {
                // The clip is gone (deleted since the backup was made): just move the temp in.
                File.Move(tempPath, clipPath);
            }
            logger.Log($"RESTORE {Path.GetFileName(clipPath)} ← {Path.GetFileName(backupPath)}");
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
            throw;
        }
    }
}
