using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaReaderTests
{
    // ── Unit tests (manually constructed BoxNode trees) ──────────────────────

    [TestMethod]
    public void GetFields_NoIlst_ReturnsEmpty()
    {
        var root = new BoxNode { Type = "root" };
        var moov = new BoxNode { Type = "moov" };
        root.Children.Add(moov);

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(0, fields.Count);
    }

    [TestMethod]
    public void GetFields_EmptyIlst_ReturnsEmpty()
    {
        var root = BuildTreeWithIlst(new List<BoxNode>());

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(0, fields.Count);
    }

    [TestMethod]
    public void GetFields_SingleClipmetaField_ReturnsField()
    {
        var atom = MakeFreeformAtom("game", "Team Fortress 2");
        var root = BuildTreeWithIlst(new List<BoxNode> { atom });

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(1, fields.Count);
        Assert.AreEqual("game", fields[0].Field);
        Assert.AreEqual("Team Fortress 2", fields[0].Value);
    }

    [TestMethod]
    public void GetFields_SkipsForeignFreeformAtom()
    {
        var foreign = new BoxNode
        {
            Type = "----",
            IsEditable = true,
            EditableKey = "com.other.domain:field",
            DisplayValue = "something",
        };
        var root = BuildTreeWithIlst(new List<BoxNode> { foreign });

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(0, fields.Count);
    }

    [TestMethod]
    public void GetFields_SkipsNonFreeformAtom()
    {
        var nam = new BoxNode
        {
            Type = "©nam",
            IsEditable = true,
            EditableKey = "©nam",
            DisplayValue = "My Video",
        };
        var root = BuildTreeWithIlst(new List<BoxNode> { nam });

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(0, fields.Count);
    }

    [TestMethod]
    public void GetFields_SkipsAtomWithNullDisplayValue()
    {
        var atom = new BoxNode
        {
            Type = "----",
            IsEditable = true,
            EditableKey = ClipMetaSchema.AtomName("game"),
            DisplayValue = null,
        };
        var root = BuildTreeWithIlst(new List<BoxNode> { atom });

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(0, fields.Count);
    }

    [TestMethod]
    public void GetFields_MultipleFields_ReturnsAllInOrder()
    {
        var atoms = new List<BoxNode>
        {
            MakeFreeformAtom("game",   "Team Fortress 2"),
            MakeFreeformAtom("tags",   "rocket jump|headshot"),
            MakeFreeformAtom("rating", "4"),
        };
        var root = BuildTreeWithIlst(atoms);

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(3, fields.Count);
        Assert.AreEqual("game",   fields[0].Field);
        Assert.AreEqual("tags",   fields[1].Field);
        Assert.AreEqual("rating", fields[2].Field);
        Assert.AreEqual("Team Fortress 2",      fields[0].Value);
        Assert.AreEqual("rocket jump|headshot", fields[1].Value);
        Assert.AreEqual("4",                    fields[2].Value);
    }

    [TestMethod]
    public void GetFields_ParserQuotedDisplayValue_StripsQuotes()
    {
        var atom = new BoxNode
        {
            Type = "----",
            IsEditable = true,
            EditableKey = ClipMetaSchema.AtomName("game"),
            DisplayValue = "\"Team Fortress 2\"",  // parser wraps UTF-8 values in double-quotes
        };
        var root = BuildTreeWithIlst(new List<BoxNode> { atom });

        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(1, fields.Count);
        Assert.AreEqual("Team Fortress 2", fields[0].Value);
    }

    // ── Integration tests (real MP4 files written by Mp4Writer) ──────────────

    public static IEnumerable<object[]> PristineClips()
        => TestClipsLocator.AllPristine().Select(p => new object[] { p });

    private static readonly System.Collections.Concurrent.ConcurrentBag<string> _scratchFiles = new();

    [ClassCleanup]
    public static void CleanupScratch()
    {
        foreach (string path in _scratchFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
        }
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void GetFields_PristineClip_DoesNotThrow(string pristinePath)
    {
        var root   = Mp4Parser.ParseFile(pristinePath);
        var fields = ClipMetaReader.GetFields(root);
        Assert.IsNotNull(fields);
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void GetFields_AfterWriteAllFields_ReturnsAllFields(string pristinePath)
    {
        string scratch = ScratchClips.Prepare(pristinePath);
        _scratchFiles.Add(scratch);

        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)]   = "Team Fortress 2";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)]   = "rocket jump|headshot";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Rating)] = "4";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Notes)]  = "great clip";
        new Mp4Writer().WriteMetadata(scratch, mutation, NullLogger.Instance);

        var root   = Mp4Parser.ParseFile(scratch);
        var fields = ClipMetaReader.GetFields(root);

        var dict = fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);
        Assert.IsTrue(dict.ContainsKey(ClipMetaSchema.Game),   "game missing");
        Assert.IsTrue(dict.ContainsKey(ClipMetaSchema.Tags),   "tags missing");
        Assert.IsTrue(dict.ContainsKey(ClipMetaSchema.Rating), "rating missing");
        Assert.IsTrue(dict.ContainsKey(ClipMetaSchema.Notes),  "notes missing");
        Assert.AreEqual("Team Fortress 2",      dict[ClipMetaSchema.Game]);
        Assert.AreEqual("rocket jump|headshot", dict[ClipMetaSchema.Tags]);
        Assert.AreEqual("4",                    dict[ClipMetaSchema.Rating]);
        Assert.AreEqual("great clip",           dict[ClipMetaSchema.Notes]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static BoxNode MakeFreeformAtom(string field, string value) => new BoxNode
    {
        Type = "----",
        IsEditable = true,
        EditableKey = ClipMetaSchema.AtomName(field),
        DisplayValue = value,
    };

    private static BoxNode BuildTreeWithIlst(List<BoxNode> ilstChildren)
    {
        var ilst = new BoxNode { Type = "ilst" };
        ilst.Children.AddRange(ilstChildren);
        var meta = new BoxNode { Type = "meta" };
        meta.Children.Add(ilst);
        var udta = new BoxNode { Type = "udta" };
        udta.Children.Add(meta);
        var moov = new BoxNode { Type = "moov" };
        moov.Children.Add(udta);
        var root = new BoxNode { Type = "root" };
        root.Children.Add(moov);
        return root;
    }
}
