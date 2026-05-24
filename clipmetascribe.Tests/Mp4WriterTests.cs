using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class Mp4WriterTests
{
    private string _tempFile = string.Empty;

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        string tmp = _tempFile + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
    }

    // ── Scenario 1: Update existing ---- atom ─────────────────────────────────

    [TestMethod]
    public void Write_UpdateExistingAtom_ValueChanged()
    {
        using var ms = MinimalMp4Builder.BuildMp4WithStco(
            chunkOffset: 9999,
            ClipMetaSchema.Domain, "tags", "old value");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:tags"] = "new value";

        var writer = new Mp4Writer();
        writer.WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(_tempFile);
        var tagsNode = FindFreeformAtom(root, "tags");
        Assert.IsNotNull(tagsNode, "tags atom should still exist after update");
        Assert.IsTrue(tagsNode.DisplayValue?.Contains("new value"),
            $"Expected 'new value', got: {tagsNode.DisplayValue}");
    }

    [TestMethod]
    public void Write_DryRun_FileUnchanged()
    {
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "tags", "original");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);
        byte[] before = File.ReadAllBytes(_tempFile);

        var mutation = new MetadataMutation { DryRun = true };
        mutation.SetFields[$"{ClipMetaSchema.Domain}:tags"] = "changed";

        var writer = new Mp4Writer();
        writer.WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        byte[] after = File.ReadAllBytes(_tempFile);
        CollectionAssert.AreEqual(before, after, "Dry run must not modify the file.");
    }

    [TestMethod]
    public void Write_TempFileCleanedUp_OnSuccess()
    {
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "tags", "v");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:tags"] = "v2";

        new Mp4Writer().WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        Assert.IsFalse(File.Exists(_tempFile + ".tmp"), "Temp file should be deleted after success.");
    }

    // ── Scenario 2: Append to existing ilst ───────────────────────────────────

    [TestMethod]
    public void Write_AppendToExistingIlst_NewAtomPresent()
    {
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "game", "TF2");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:tags"] = "headshot";

        new Mp4Writer().WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(_tempFile);
        var gameNode = FindFreeformAtom(root, "game");
        var tagsNode = FindFreeformAtom(root, "tags");

        Assert.IsNotNull(gameNode, "Original 'game' atom should be preserved");
        Assert.IsTrue(gameNode.DisplayValue?.Contains("TF2"), "game value unchanged");
        Assert.IsNotNull(tagsNode, "New 'tags' atom should be present");
        Assert.IsTrue(tagsNode.DisplayValue?.Contains("headshot"), "tags value correct");
    }

    // ── Scenario 3: Create from scratch ───────────────────────────────────────

    [TestMethod]
    public void Write_CreateFromScratch_IlstAndHdlrCreated()
    {
        byte[] moov = MinimalMp4Builder.MoovBox(null);
        byte[] mdat = MinimalMp4Builder.MdatBox();
        _tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".mp4");
        File.WriteAllBytes(_tempFile, moov.Concat(mdat).ToArray());

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:game"] = "Team Fortress 2";

        new Mp4Writer().WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(_tempFile);
        var moovNode = root.Children.First(c => c.Type == "moov");
        var udtaNode = moovNode.Children.FirstOrDefault(c => c.Type == "udta");
        Assert.IsNotNull(udtaNode, "udta box must be created");

        var metaNode = udtaNode.Children.FirstOrDefault(c => c.Type == "meta");
        Assert.IsNotNull(metaNode, "meta box must be created");

        var hdlrNode = metaNode!.Children.FirstOrDefault(c => c.Type == "hdlr");
        Assert.IsNotNull(hdlrNode, "hdlr box must be created inside meta");

        var ilstNode = metaNode.Children.FirstOrDefault(c => c.Type == "ilst");
        Assert.IsNotNull(ilstNode, "ilst box must be created");

        var gameNode = FindFreeformAtom(root, "game");
        Assert.IsNotNull(gameNode, "game atom should be present");
        Assert.IsTrue(gameNode!.DisplayValue?.Contains("Team Fortress 2"), "game value correct");
    }

    // ── stco/co64 adjustment ──────────────────────────────────────────────────

    [TestMethod]
    public void Write_AfterWrite_FileStillParseable()
    {
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "game", "TF2");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:notes"] = "testing stco paths";

        new Mp4Writer().WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(_tempFile);
        Assert.IsNotNull(root);
        Assert.IsTrue(root.Children.Count > 0);
    }

    [TestMethod]
    public void Write_SchemaVersionStamped_OnEveryWrite()
    {
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, "game", "TF2");
        _tempFile = MinimalMp4Builder.SaveToTempFile(ms);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{ClipMetaSchema.Domain}:tags"] = "headshot";
        new Mp4Writer().WriteMetadata(_tempFile, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(_tempFile);
        var schemaNode = FindFreeformAtom(root, ClipMetaSchema.Schema);
        Assert.IsNotNull(schemaNode, "schema version atom must be present after write");
        Assert.IsTrue(schemaNode!.DisplayValue?.Contains(ClipMetaSchema.SchemaVersion),
            $"schema value should be '1', got: {schemaNode.DisplayValue}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BoxNode? FindFreeformAtom(BoxNode root, string fieldName)
    {
        string key = $"{ClipMetaSchema.Domain}:{fieldName}";
        return FindNode(root, n => n.EditableKey == key);
    }

    private static BoxNode? FindNode(BoxNode node, Func<BoxNode, bool> predicate)
    {
        if (predicate(node)) return node;
        foreach (var child in node.Children)
        {
            var found = FindNode(child, predicate);
            if (found != null) return found;
        }
        return null;
    }
}
