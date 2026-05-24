using ClipMetaCore;
using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class Mp4WriterIntegrationTests
{
    // Track scratch files created by this class so we can clean up after all tests complete.
    private static readonly System.Collections.Concurrent.ConcurrentBag<string> _scratchFiles = new();

    [ClassCleanup]
    public static void CleanupScratch()
    {
        foreach (string path in _scratchFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
            string tmp = path + ".tmp";
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            string bak = path + ".bak";
            try { if (File.Exists(bak)) File.Delete(bak); } catch { /* best effort */ }
        }
    }

    /// <summary>Prepares a unique scratch copy and registers it for cleanup.</summary>
    private static string PrepareScratch(string pristinePath)
    {
        string path = ScratchClips.Prepare(pristinePath);
        _scratchFiles.Add(path);
        return path;
    }

    public static IEnumerable<object[]> PristineClips()
        => TestClipsLocator.AllPristine().Select(p => new object[] { p });

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_SetGameField_RoundTrips(string pristinePath)
    {
        string scratchPath = PrepareScratch(pristinePath);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2";

        new Mp4Writer().WriteMetadata(scratchPath, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(scratchPath);
        var gameNode = FindFreeformAtom(root, ClipMetaSchema.Game);
        Assert.IsNotNull(gameNode, $"game atom not found after write in {pristinePath}");
        Assert.IsTrue(gameNode!.DisplayValue?.Contains("Team Fortress 2"),
            $"game value wrong in {pristinePath}: {gameNode.DisplayValue}");
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_SetTagsField_RoundTrips(string pristinePath)
    {
        string scratchPath = PrepareScratch(pristinePath);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "rocket jump|headshot";

        new Mp4Writer().WriteMetadata(scratchPath, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(scratchPath);
        var tagsNode = FindFreeformAtom(root, ClipMetaSchema.Tags);
        Assert.IsNotNull(tagsNode, $"tags atom not found in {pristinePath}");
        Assert.IsTrue(tagsNode!.DisplayValue?.Contains("rocket jump"),
            $"tags value wrong: {tagsNode.DisplayValue}");
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_WriteAllFields_AllRoundTrip(string pristinePath)
    {
        string scratchPath = PrepareScratch(pristinePath);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Players)] = "Ben|Scott";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "market garden|funny";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Timecode)] = "00:00:45";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Rating)] = "4";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Notes)] = "Ben gets the kill";

        new Mp4Writer().WriteMetadata(scratchPath, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(scratchPath);
        foreach (string field in new[] { ClipMetaSchema.Game, ClipMetaSchema.Players,
                                          ClipMetaSchema.Tags, ClipMetaSchema.Rating, ClipMetaSchema.Notes })
        {
            var node = FindFreeformAtom(root, field);
            Assert.IsNotNull(node, $"Field '{field}' not found after write in {pristinePath}");
        }
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_ForeignAtoms_Preserved(string pristinePath)
    {
        var rootBefore = Mp4Parser.ParseFile(pristinePath);
        var ilst = FindNode(rootBefore, n => n.Type == "ilst");
        var foreignAtomsBefore = ilst?.Children
            .Where(c => c.Type != "----" || !c.EditableKey!.StartsWith(ClipMetaSchema.Domain))
            .ToList() ?? new();

        if (foreignAtomsBefore.Count == 0) return;

        string scratchPath = PrepareScratch(pristinePath);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "test";

        new Mp4Writer().WriteMetadata(scratchPath, mutation, NullLogger.Instance);

        var rootAfter = Mp4Parser.ParseFile(scratchPath);
        var ilstAfter = FindNode(rootAfter, n => n.Type == "ilst");
        var foreignAtomsAfter = ilstAfter?.Children
            .Where(c => c.Type != "----" || !c.EditableKey!.StartsWith(ClipMetaSchema.Domain))
            .ToList() ?? new();

        Assert.AreEqual(foreignAtomsBefore.Count, foreignAtomsAfter.Count,
            $"Foreign atom count changed. Before: {foreignAtomsBefore.Count}, After: {foreignAtomsAfter.Count}");
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_OriginalUnchanged_WhenDryRun(string pristinePath)
    {
        byte[] before = File.ReadAllBytes(pristinePath);
        var mutation = new MetadataMutation { DryRun = true };
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";

        new Mp4Writer().WriteMetadata(pristinePath, mutation, NullLogger.Instance);

        byte[] after = File.ReadAllBytes(pristinePath);
        CollectionAssert.AreEqual(before, after, $"Dry run modified {pristinePath}");
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Write_NoTempFileLeft_AfterSuccess(string pristinePath)
    {
        string scratchPath = PrepareScratch(pristinePath);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";

        new Mp4Writer().WriteMetadata(scratchPath, mutation, NullLogger.Instance);

        Assert.IsFalse(File.Exists(scratchPath + ".tmp"),
            $"Temp file not cleaned up for {Path.GetFileName(pristinePath)}");
    }

    [TestMethod]
    public void Write_FragmentedMp4_ThrowsUnsupportedFormatException()
    {
        string fragPath = Path.ChangeExtension(Path.GetTempFileName(), ".mp4");
        try
        {
            byte[] moov = MinimalMp4Builder.MoovBox(null);
            using var ms = new MemoryStream();
            ms.Write(moov);
            byte[] moofBox = new byte[16];
            moofBox[0] = 0; moofBox[1] = 0; moofBox[2] = 0; moofBox[3] = 16;
            System.Text.Encoding.Latin1.GetBytes("moof").CopyTo(moofBox, 4);
            ms.Write(moofBox);
            ms.Write(MinimalMp4Builder.MdatBox());
            File.WriteAllBytes(fragPath, ms.ToArray());

            var mutation = new MetadataMutation();
            mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";

            Assert.ThrowsExactly<UnsupportedFormatException>(() =>
                new Mp4Writer().WriteMetadata(fragPath, mutation, NullLogger.Instance));
        }
        finally
        {
            if (File.Exists(fragPath)) File.Delete(fragPath);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static BoxNode? FindFreeformAtom(BoxNode root, string fieldName)
        => FindNode(root, n => n.EditableKey == ClipMetaSchema.AtomName(fieldName));

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
