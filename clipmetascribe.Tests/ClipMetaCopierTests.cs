using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Unit tests for <see cref="ClipMetaCopier.BuildCopyMutation"/>, the pure read→mutation core of
/// the CopyTags command. Drives manually-built BoxNode trees (no filesystem) per the project's
/// "core logic is testable without the CLI" convention.
/// </summary>
[TestClass]
public class ClipMetaCopierTests
{
    [TestMethod]
    public void BuildCopyMutation_CopiesAllUserFieldsAsDomainQualifiedSets()
    {
        var root = BuildTreeWithIlst(
            MakeFreeformAtom("game", "Team Fortress 2"),
            MakeFreeformAtom("tags", "rocket jump|headshot"),
            MakeFreeformAtom("customField", "hi"));

        var mutation = ClipMetaCopier.BuildCopyMutation(root);

        Assert.AreEqual(3, mutation.SetFields.Count);
        Assert.AreEqual("Team Fortress 2", mutation.SetFields[ClipMetaSchema.AtomName("game")]);
        Assert.AreEqual("rocket jump|headshot", mutation.SetFields[ClipMetaSchema.AtomName("tags")]);
        Assert.AreEqual("hi", mutation.SetFields[ClipMetaSchema.AtomName("customField")]);
        Assert.AreEqual(0, mutation.AppendFields.Count);
        Assert.AreEqual(0, mutation.DeleteFields.Count);
        Assert.IsFalse(mutation.ClearAll);
    }

    [TestMethod]
    public void BuildCopyMutation_ExcludesInternalSchemaField()
    {
        var root = BuildTreeWithIlst(
            MakeFreeformAtom("game", "TF2"),
            MakeFreeformAtom(ClipMetaSchema.Schema, "1"));   // internal bookkeeping field

        var mutation = ClipMetaCopier.BuildCopyMutation(root);

        Assert.AreEqual(1, mutation.SetFields.Count);
        Assert.IsTrue(mutation.SetFields.ContainsKey(ClipMetaSchema.AtomName("game")));
        Assert.IsFalse(mutation.SetFields.ContainsKey(ClipMetaSchema.AtomName(ClipMetaSchema.Schema)),
            "the internal schema field must never be copied");
    }

    [TestMethod]
    public void BuildCopyMutation_NoUserFields_ReturnsEmptyMutation()
    {
        var root = BuildTreeWithIlst();   // ilst present but no atoms

        var mutation = ClipMetaCopier.BuildCopyMutation(root);

        Assert.AreEqual(0, mutation.SetFields.Count);
    }

    // ── Helpers (mirror ClipMetaReaderTests) ──────────────────────────────────

    private static BoxNode MakeFreeformAtom(string field, string value) => new()
    {
        Type = "----",
        IsEditable = true,
        EditableKey = ClipMetaSchema.AtomName(field),
        DisplayValue = value,
    };

    private static BoxNode BuildTreeWithIlst(params BoxNode[] ilstChildren)
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
