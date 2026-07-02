using ClipMetaCore.Logging;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Reproduces the nemesis-reported drift: clipmetascribe/Program.cs built backup paths as a
/// literal "&lt;clip&gt;.bak" at its three --backup call sites, instead of delegating to
/// <see cref="ClipBackup.MakeBackupPath"/>'s timestamped "&lt;clip&gt;.bak-yyyyMMdd-HHmmss"
/// convention, the one clipmetamcp's WriteTools already uses (WriteTools.cs, around line 595).
/// Two consecutive CLI writes with --backup therefore collided on the same literal ".bak" name:
/// the second write's File.Replace silently destroyed the first backup, and neither backup was
/// discoverable by library_list_backups / clip_restore_backup / clip_prune_backups (which only
/// recognize the timestamped form).
/// </summary>
[TestClass]
public class ProgramCliBackupIntegrationTests
{
    private static readonly List<string> _scratchFiles = new();

    [ClassCleanup]
    public static void CleanupScratch()
    {
        foreach (string path in _scratchFiles)
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null)
            {
                foreach (BackupInfo b in ClipBackup.ListBackups(dir, path))
                {
                    try { File.Delete(b.BackupPath); } catch { /* best effort */ }
                }
            }
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void TwoConsecutiveBackupWrites_ProduceTwoDistinctTimestampedBackups()
    {
        if (!TestClipsLocator.PristineClipsPresent())
        {
            Assert.Inconclusive(
                "No test clips found in testclips/pristine, integration test skipped.");
        }

        string pristinePath = TestClipsLocator.SmallestPristine();
        string scratchPath = ScratchClips.Prepare(pristinePath);
        _scratchFiles.Add(scratchPath);

        var mutation1 = Program.BuildMutation(
            new[] { "--set", "notes", "first" }, scratchPath, dryRun: false, backup: true);
        WriteCommand.Run(scratchPath, mutation1, NullLogger.Instance);

        // ClipBackup.MakeBackupPath is second-resolution by design (see its doc comment): two
        // writes inside the same wall-clock second are documented to collide, and callers needing
        // rapid repeats must serialize. A human running --backup twice in a row is not that case,
        // so force a new second here to isolate the naming-convention bug under test from that
        // separately-documented, separately-owned resolution limit.
        Thread.Sleep(1100);

        var mutation2 = Program.BuildMutation(
            new[] { "--set", "notes", "second" }, scratchPath, dryRun: false, backup: true);
        WriteCommand.Run(scratchPath, mutation2, NullLogger.Instance);

        Assert.IsNotNull(mutation1.BackupPath);
        Assert.IsNotNull(mutation2.BackupPath);
        Assert.AreNotEqual(mutation1.BackupPath, mutation2.BackupPath,
            "two consecutive --backup writes must not collide on the same backup file name");

        Assert.IsTrue(ClipBackup.TryGetClipForBackup(mutation1.BackupPath!, out _),
            $"'{mutation1.BackupPath}' does not match the timestamped .bak-<stamp> convention");
        Assert.IsTrue(ClipBackup.TryGetClipForBackup(mutation2.BackupPath!, out _),
            $"'{mutation2.BackupPath}' does not match the timestamped .bak-<stamp> convention");

        Assert.IsTrue(File.Exists(mutation1.BackupPath),
            "the first backup must survive the second write, not be destroyed by it");
        Assert.IsTrue(File.Exists(mutation2.BackupPath));
    }
}
