using ClipMetaCore.Mp4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClipMetaView.Tests;

[TestClass]
public class BoxNodeTests
{
    [TestMethod]
    public void NewNode_TypeAndSize_AreSetCorrectly()
    {
        var node = new BoxNode { Type = "ftyp", Size = 32 };

        Assert.AreEqual("ftyp", node.Type);
        Assert.AreEqual(32UL, node.Size);
    }

    [TestMethod]
    public void NewNode_IsEditable_DefaultsFalse()
    {
        var node = new BoxNode { Type = "moov" };

        Assert.IsFalse(node.IsEditable);
    }

    [TestMethod]
    public void NewNode_Children_IsNeverNull()
    {
        var node = new BoxNode { Type = "moov" };

        Assert.IsNotNull(node.Children);
    }

    [TestMethod]
    public void Children_CanAddAndTraverse()
    {
        var parent = new BoxNode { Type = "moov" };
        var child1 = new BoxNode { Type = "mvhd", Size = 108 };
        var child2 = new BoxNode { Type = "trak", Size = 1024 };

        parent.Children.Add(child1);
        parent.Children.Add(child2);

        Assert.AreEqual(2, parent.Children.Count);
        Assert.AreEqual("mvhd", parent.Children[0].Type);
        Assert.AreEqual("trak", parent.Children[1].Type);
    }

    [TestMethod]
    public void DisplayValue_CanBeSetAndRead()
    {
        var node = new BoxNode { Type = "©nam" };
        node.DisplayValue = "My Title";

        Assert.AreEqual("My Title", node.DisplayValue);
    }

    [TestMethod]
    public void EditableKey_CanBeSetAndRead()
    {
        var node = new BoxNode { Type = "©nam", IsEditable = true, EditableKey = "©nam" };

        Assert.AreEqual("©nam", node.EditableKey);
        Assert.IsTrue(node.IsEditable);
    }

    [TestMethod]
    public void FileOffset_IsAccuratelyStored()
    {
        var node = new BoxNode { Type = "moov", FileOffset = 0x1234 };

        Assert.AreEqual(0x1234L, node.FileOffset);
    }
}
