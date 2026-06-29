using System.Text.Json.Nodes;
using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaMcp.Tests.Helpers;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaMcp.Tests;

/// <summary>
/// End-to-end tests for the backup-management tools (library_list_backups, clip_restore_backup,
/// clip_prune_backups) through the full session pipeline. Each test copies a real clip into its
/// own temp library and creates backups by hand (deterministic timestamps), so list/restore/
/// prune behaviour is exact and independent of wall-clock timing.
/// </summary>
[TestClass]
public class BackupToolsTests
{
    private string _lib = null!;
    private string _clip = null!;

    [TestInitialize]
    public void SetUp()
    {
        _lib = Path.Combine(Path.GetTempPath(), "clipmeta-bak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_lib);
        _clip = Path.Combine(_lib, "clip.mp4");
        File.Copy(TestClipsLocator.SmallestPristine(), _clip);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_lib))
            Directory.Delete(_lib, recursive: true);
    }

    /// <summary>Creates a backup file (a copy of the clip) with a specific timestamp suffix.</summary>
    private string MakeBackup(string stamp, string? content = null)
    {
        string path = $"{_clip}.bak-{stamp}";
        if (content is null) File.Copy(_clip, path);
        else File.WriteAllText(path, content);
        return path;
    }

    private JsonObject Call(string tool, JsonObject arguments, string? libraryRoot = "lib")
    {
        string? root = libraryRoot == "lib" ? _lib : libraryRoot;
        var responses = McpHarness.Run(root,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, tool, arguments));
        return (JsonObject)responses[1]["result"]!;
    }

    private static JsonObject Structured(JsonObject r) => (JsonObject)r["structuredContent"]!;
    private static string ErrorText(JsonObject r) => r["content"]![0]!["text"]!.GetValue<string>();
    private static void AssertOk(JsonObject r) =>
        Assert.IsNull(r["isError"], "expected success but got: " + ErrorText(r));
    private static void AssertRefused(JsonObject r, string fragment)
    {
        Assert.IsTrue(r["isError"]?.GetValue<bool>(), "expected a refusal");
        StringAssert.Contains(ErrorText(r), fragment);
    }

    // ── library_list_backups ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void List_ReturnsBackupsNewestFirst_WithOwningClip()
    {
        MakeBackup("20260101-120000");
        MakeBackup("20260612-153000");

        JsonObject result = Call("library_list_backups", new JsonObject());

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual(2, s["backupCount"]!.GetValue<int>());
        var stamps = s["backups"]!.AsArray().Select(b => b!["backup"]!.GetValue<string>()).ToList();
        StringAssert.Contains(stamps[0], "20260612-153000"); // newest first
        StringAssert.Contains(stamps[1], "20260101-120000");
        Assert.AreEqual(_clip, s["backups"]![0]!["clip"]!.GetValue<string>());
    }

    [TestMethod]
    public void List_IgnoresNonConventionFiles()
    {
        MakeBackup("20260101-120000");
        File.WriteAllText(Path.Combine(_lib, "clip.mp4.bak"), "no timestamp");       // not ours
        File.WriteAllText(Path.Combine(_lib, "notes.bak-readme"), "non-stamp");      // not ours
        File.WriteAllText(Path.Combine(_lib, "other.txt"), "unrelated");

        JsonObject result = Call("library_list_backups", new JsonObject());

        AssertOk(result);
        Assert.AreEqual(1, Structured(result)["backupCount"]!.GetValue<int>(),
            "only .bak-<valid timestamp> files count as backups");
    }

    [TestMethod]
    public void List_ClipFilter_ScopesToOneClip()
    {
        MakeBackup("20260101-120000");
        // A second clip with its own backup.
        string other = Path.Combine(_lib, "other.mp4");
        File.Copy(_clip, other);
        File.Copy(other, $"{other}.bak-20260101-130000");

        JsonObject result = Call("library_list_backups", new JsonObject { ["clip"] = _clip });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual(1, s["backupCount"]!.GetValue<int>());
        Assert.AreEqual(_clip, s["backups"]![0]!["clip"]!.GetValue<string>());
    }

    [TestMethod]
    public void List_NoRootConfigured_IsRefused()
    {
        JsonObject result = Call("library_list_backups", new JsonObject(), libraryRoot: null);
        AssertRefused(result, "No clips library is configured");
    }

    // ── clip_restore_backup ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Restore_OverwritesClipFromBackup_VerifiedByReRead()
    {
        // Backup carries game=Team Fortress 2; then mutate the live clip differently.
        var tagged = new MetadataMutation();
        tagged.SetFields[ClipMetaSchema.AtomName("game")] = "Team Fortress 2";
        new Mp4Writer().WriteMetadata(_clip, tagged, NullLogger.Instance);
        string backup = MakeBackup("20260101-120000"); // snapshot WITH game=TF2

        var changed = new MetadataMutation();
        changed.SetFields[ClipMetaSchema.AtomName("game")] = "Overwatch";
        new Mp4Writer().WriteMetadata(_clip, changed, NullLogger.Instance);

        JsonObject result = Call("clip_restore_backup",
            new JsonObject { ["backup"] = backup, ["confirm"] = true });

        AssertOk(result);
        Assert.AreEqual(backup, Structured(result)["restoredFrom"]!.GetValue<string>());
        Assert.AreEqual("Team Fortress 2",
            Structured(result)["fields"]!["game"]!.GetValue<string>(),
            "the clip must now hold the backup's metadata");
        Assert.IsTrue(File.Exists(backup), "restore must not consume the backup");
    }

    [TestMethod]
    public void Restore_WithoutConfirm_IsRefused_ClipUntouched()
    {
        var tagged = new MetadataMutation();
        tagged.SetFields[ClipMetaSchema.AtomName("game")] = "TF2";
        new Mp4Writer().WriteMetadata(_clip, tagged, NullLogger.Instance);
        string backup = MakeBackup("20260101-120000");
        byte[] before = File.ReadAllBytes(_clip);

        AssertRefused(Call("clip_restore_backup", new JsonObject { ["backup"] = backup }),
            "confirm:true");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(_clip), "clip must be untouched");
    }

    [TestMethod]
    public void Restore_CorruptBackup_IsRefused_ClipUntouched()
    {
        byte[] before = File.ReadAllBytes(_clip);
        // A .bak with a valid-looking name but garbage contents (truncated/tampered backup).
        string corrupt = MakeBackup("20260101-120000", content: "not a real mp4");

        JsonObject result = Call("clip_restore_backup",
            new JsonObject { ["backup"] = corrupt, ["confirm"] = true });

        AssertRefused(result, "not a complete, valid MP4");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(_clip),
            "a corrupt backup must never overwrite a good clip");
    }

    [TestMethod]
    public void Restore_MediaByteIdenticalToBackup()
    {
        // Tag, snapshot, change, then restore and prove the media matches the snapshot exactly.
        var tagged = new MetadataMutation();
        tagged.SetFields[ClipMetaSchema.AtomName("tags")] = "a|b";
        new Mp4Writer().WriteMetadata(_clip, tagged, NullLogger.Instance);
        string backup = MakeBackup("20260101-120000");

        var changed = new MetadataMutation();
        changed.SetFields[ClipMetaSchema.AtomName("notes")] = "different";
        new Mp4Writer().WriteMetadata(_clip, changed, NullLogger.Instance);

        AssertOk(Call("clip_restore_backup",
            new JsonObject { ["backup"] = backup, ["confirm"] = true }));

        MediaIntegrityScanner.AssertMediaUnchanged(backup, _clip);
    }

    [TestMethod]
    public void Restore_NoRootConfigured_IsRefused()
    {
        string backup = MakeBackup("20260101-120000");
        AssertRefused(
            Call("clip_restore_backup", new JsonObject { ["backup"] = backup, ["confirm"] = true },
                libraryRoot: null),
            "No clips library is configured");
    }

    // ── clip_prune_backups ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Prune_KeepNewest_DeletesOlderOnly()
    {
        string oldest = MakeBackup("20260101-120000");
        string middle = MakeBackup("20260301-120000");
        string newest = MakeBackup("20260612-120000");

        JsonObject result = Call("clip_prune_backups",
            new JsonObject { ["clip"] = _clip, ["keep"] = 1, ["confirm"] = true });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual(2, s["deletedCount"]!.GetValue<int>());
        Assert.AreEqual(1, s["keptCount"]!.GetValue<int>());
        Assert.IsTrue(File.Exists(newest), "newest must be kept");
        Assert.IsFalse(File.Exists(oldest), "older must be deleted");
        Assert.IsFalse(File.Exists(middle), "older must be deleted");
    }

    [TestMethod]
    public void Prune_KeepZero_DeletesAll_ButNotTheClipOrForeignFiles()
    {
        MakeBackup("20260101-120000");
        MakeBackup("20260612-120000");
        string foreignBak = Path.Combine(_lib, "clip.mp4.bak");           // no timestamp, not ours
        File.WriteAllText(foreignBak, "user's own backup");

        JsonObject result = Call("clip_prune_backups",
            new JsonObject { ["clip"] = _clip, ["confirm"] = true }); // keep defaults to 0

        AssertOk(result);
        Assert.AreEqual(2, Structured(result)["deletedCount"]!.GetValue<int>());
        Assert.IsTrue(File.Exists(_clip), "the clip itself must never be deleted");
        Assert.IsTrue(File.Exists(foreignBak), "a non-convention .bak must never be deleted");
    }

    [TestMethod]
    public void Prune_OnlyTargetsTheNamedClip()
    {
        MakeBackup("20260101-120000");
        string other = Path.Combine(_lib, "other.mp4");
        File.Copy(_clip, other);
        string otherBak = $"{other}.bak-20260101-130000";
        File.Copy(other, otherBak);

        JsonObject result = Call("clip_prune_backups",
            new JsonObject { ["clip"] = _clip, ["confirm"] = true });

        AssertOk(result);
        Assert.AreEqual(1, Structured(result)["deletedCount"]!.GetValue<int>());
        Assert.IsTrue(File.Exists(otherBak), "another clip's backups must be untouched");
    }

    [TestMethod]
    public void Prune_WithoutConfirm_IsRefused_NothingDeleted()
    {
        string backup = MakeBackup("20260101-120000");

        AssertRefused(Call("clip_prune_backups", new JsonObject { ["clip"] = _clip }), "confirm:true");
        Assert.IsTrue(File.Exists(backup), "nothing may be deleted without confirm");
    }
}
