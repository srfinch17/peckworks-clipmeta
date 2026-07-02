using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;

namespace ClipMetaScribe.Tests;

[TestClass]
public class BoxTreeMapperTests
{
    private static BoxNode Root(params BoxNode[] children) =>
        new() { Type = "ROOT", Children = children.ToList() };

    [TestMethod]
    public void Map_CopiesGeometryAndFriendlyName()
    {
        var ftyp = new BoxNode { Type = "ftyp", Size = 32, FileOffset = 0, HeaderSize = 8 };
        BoxTree tree = BoxTreeMapper.Map(Root(ftyp), @"C:\clips\a.mp4", 32);

        Assert.AreEqual(@"C:\clips\a.mp4", tree.Path);
        Assert.AreEqual(32, tree.FileSize);
        BoxTreeNode n = tree.Boxes.Single();
        Assert.AreEqual("ftyp", n.Type);
        Assert.AreEqual(0, n.Offset);
        Assert.AreEqual(32ul, n.Size);
        Assert.AreEqual(8, n.HeaderSize);
        Assert.AreEqual("File Type", n.FriendlyName);
        Assert.AreEqual(BoxCategory.Header, n.Category);
    }

    [TestMethod]
    public void Map_UnquotesStringDisplayValue()
    {
        var brand = new BoxNode { Type = "ftyp", DisplayValue = "\"isom\"" };
        BoxTreeNode n = BoxTreeMapper.Map(Root(brand), "p", 0).Boxes.Single();
        Assert.AreEqual("isom", n.DisplayValue);
    }

    [TestMethod]
    public void Map_ClipmetaFreeformAtom_IsFlaggedAndEditableAwareCategory()
    {
        var atom = new BoxNode
        {
            Type = "----",
            EditableKey = ClipMetaSchema.Domain + ":game",
            DisplayValue = "\"TF2\"",
            IsEditable = true,
        };
        BoxTreeNode n = BoxTreeMapper.Map(Root(atom), "p", 0).Boxes.Single();
        Assert.IsTrue(n.IsClipmetaContainer);
        Assert.AreEqual(BoxCategory.EditableMeta, n.Category);
        Assert.AreEqual(ClipMetaSchema.Domain + ":game", n.EditableKey);
    }

    [TestMethod]
    public void Map_ForeignFreeformAtom_NotFlagged()
    {
        var atom = new BoxNode { Type = "----", EditableKey = "com.apple.iTunes:X", DisplayValue = "\"y\"" };
        BoxTreeNode n = BoxTreeMapper.Map(Root(atom), "p", 0).Boxes.Single();
        Assert.IsFalse(n.IsClipmetaContainer);
    }

    [TestMethod]
    public void Map_RecursesChildrenAndLeavesAreEmpty()
    {
        var leaf = new BoxNode { Type = "mvhd" };
        var moov = new BoxNode { Type = "moov", Children = { leaf } };
        BoxTreeNode m = BoxTreeMapper.Map(Root(moov), "p", 0).Boxes.Single();
        Assert.AreEqual(1, m.Children.Count);
        Assert.AreEqual(0, m.Children.Single().Children.Count);
    }
}
