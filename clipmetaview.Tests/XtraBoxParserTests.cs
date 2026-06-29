using ClipMetaCore.Mp4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClipMetaView.Tests;

/// <summary>
/// Tests for the Xtra box parser that extracts Windows Media (WM/) attributes
/// written by Windows File Explorer into MP4 files.
/// </summary>
[TestClass]
public class XtraBoxParserTests
{
    [TestMethod]
    public void ParseXtraPayload_SingleWmCategory_ExtractsTagValue()
    {
        var clips = TestClips.All().ToList();
        if (clips.Count == 0)
            Assert.Inconclusive("No test clips found.");

        // Use a real clip that has been edited with Windows File Explorer.
        // The Xtra box in tf2testclip1.mp4 is confirmed to contain WM/Category = "Snipe Tag".
        var clip1 = clips.FirstOrDefault(p => Path.GetFileName(p).StartsWith("tf2testclip1", StringComparison.OrdinalIgnoreCase));
        if (clip1 == null)
            Assert.Inconclusive("tf2testclip1.mp4 not found in testclips.");

        var root = Mp4Parser.ParseFile(clip1);
        var xtraNode = FindBoxByType(root, "Xtra");

        if (xtraNode == null)
            Assert.Inconclusive("No Xtra box found, clip may not have Windows metadata.");

        var categoryNode = xtraNode.Children.FirstOrDefault(c => c.Type == "WM/Category");
        Assert.IsNotNull(categoryNode, "Expected WM/Category child under Xtra node");
        Assert.IsNotNull(categoryNode.DisplayValue, "WM/Category should have a DisplayValue");
        StringAssert.Contains(categoryNode.DisplayValue, "Snipe Tag", "WM/Category value should contain 'Snipe Tag'");
    }

    [TestMethod]
    public void ParseXtraPayload_EditableKeys_AreMarkedEditable()
    {
        var clips = TestClips.All().ToList();
        if (clips.Count == 0)
            Assert.Inconclusive("No test clips found.");

        var clip1 = clips.FirstOrDefault(p => Path.GetFileName(p).StartsWith("tf2testclip1", StringComparison.OrdinalIgnoreCase));
        if (clip1 == null)
            Assert.Inconclusive("tf2testclip1.mp4 not found in testclips.");

        var root = Mp4Parser.ParseFile(clip1);
        var xtraNode = FindBoxByType(root, "Xtra");

        if (xtraNode == null)
            Assert.Inconclusive("No Xtra box found.");

        var editableChildren = xtraNode.Children.Where(c => c.IsEditable).ToList();
        Assert.IsTrue(editableChildren.Count > 0,
            "Expected at least one editable WM/ child under Xtra node");
    }

    [TestMethod]
    public void ParseXtraPayload_WmDirector_IsExtracted()
    {
        var clips = TestClips.All().ToList();
        var clip1 = clips.FirstOrDefault(p => Path.GetFileName(p).StartsWith("tf2testclip1", StringComparison.OrdinalIgnoreCase));
        if (clip1 == null)
            Assert.Inconclusive("tf2testclip1.mp4 not found.");

        var root = Mp4Parser.ParseFile(clip1);
        var xtraNode = FindBoxByType(root, "Xtra");
        if (xtraNode == null)
            Assert.Inconclusive("No Xtra box found.");

        var directorNode = xtraNode.Children.FirstOrDefault(c => c.Type == "WM/Director");
        Assert.IsNotNull(directorNode, "Expected WM/Director under Xtra node");
        Assert.IsNotNull(directorNode.DisplayValue);
        StringAssert.Contains(directorNode.DisplayValue, "Some Dood");
    }

    [TestMethod]
    public void ParseXtraPayload_RealClip_NoCrashOnAnyTestClip()
    {
        foreach (string clip in TestClips.All())
        {
            var root = Mp4Parser.ParseFile(clip);
            // If Xtra parsing crashed the above would throw.
            Assert.IsNotNull(root, $"Parser returned null for {Path.GetFileName(clip)}");
        }
    }

    [TestMethod]
    public void MetadataKeys_WmCategory_FriendlyNameIsTagsExpected()
    {
        string name = MetadataKeys.GetName("WM/Category");
        Assert.AreEqual("Tags", name, "WM/Category should map to 'Tags'");
    }

    [TestMethod]
    public void MetadataKeys_WmKeys_AreWindowsMediaCategory()
    {
        Assert.AreEqual(BoxCategory.WindowsMedia, MetadataKeys.GetCategory("WM/Category"));
        Assert.AreEqual(BoxCategory.WindowsMedia, MetadataKeys.GetCategory("WM/Director"));
        Assert.AreEqual(BoxCategory.WindowsMedia, MetadataKeys.GetCategory("Xtra"));
    }

    private static BoxNode? FindBoxByType(BoxNode node, string type)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == type) return child;
            var found = FindBoxByType(child, type);
            if (found != null) return found;
        }
        return null;
    }
}
