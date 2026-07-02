using ClipMetaCore.Mp4;
using ClipMetaView.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClipMetaView.Tests;

[TestClass]
public class TreeRendererTests
{
    // Pass a StringWriter directly, no Console.SetOut needed, safe for parallel execution.
    private static string CaptureRender(BoxNode root, string filePath = "test.mp4")
    {
        var sw = new StringWriter();
        TreeRenderer.Render(root, filePath, sw);
        return sw.ToString();
    }

    [TestMethod]
    public void Render_SingleChildNode_OutputsCorrectBranchCharacter()
    {
        var root = new BoxNode { Type = "root", Size = 100 };
        root.Children.Add(new BoxNode { Type = "ftyp", Size = 32, FileOffset = 0, HeaderSize = 8 });

        string output = CaptureRender(root);

        // Single child should use the "last" branch connector
        Assert.IsTrue(output.Contains("└── "), $"Expected '└── ' in output:\n{output}");
        Assert.IsTrue(output.Contains("ftyp"), "Expected FourCC in output");
    }

    [TestMethod]
    public void Render_MultipleChildren_UsesMidAndLastConnectors()
    {
        var root = new BoxNode { Type = "root", Size = 200 };
        root.Children.Add(new BoxNode { Type = "ftyp", Size = 32, FileOffset = 0, HeaderSize = 8 });
        root.Children.Add(new BoxNode { Type = "moov", Size = 100, FileOffset = 32, HeaderSize = 8 });
        root.Children.Add(new BoxNode { Type = "mdat", Size = 68, FileOffset = 132, HeaderSize = 8 });

        string output = CaptureRender(root);

        Assert.IsTrue(output.Contains("├── "), $"Expected mid-branch '├── ' in:\n{output}");
        Assert.IsTrue(output.Contains("└── "), $"Expected last-branch '└── ' in:\n{output}");
    }

    [TestMethod]
    public void Render_EditableNode_ContainsEditableMarker()
    {
        var root = new BoxNode { Type = "root", Size = 100 };
        var metaItem = new BoxNode
        {
            Type = "©nam",
            Size = 50,
            FileOffset = 0,
            HeaderSize = 8,
            IsEditable = true,
            DisplayValue = "My Title",
        };
        root.Children.Add(metaItem);

        string output = CaptureRender(root);

        Assert.IsTrue(output.Contains("[EDITABLE]"), $"Expected '[EDITABLE]' in output:\n{output}");
    }

    [TestMethod]
    public void Render_NodeWithDisplayValue_ShowsValueInOutput()
    {
        var root = new BoxNode { Type = "root", Size = 100 };
        root.Children.Add(new BoxNode
        {
            Type = "©nam",
            Size = 50,
            FileOffset = 0,
            HeaderSize = 8,
            DisplayValue = "My Vacation 2024",
        });

        string output = CaptureRender(root);

        // Renderer displays DisplayValue as-is; quote-wrapping is the parser's responsibility.
        Assert.IsTrue(output.Contains("My Vacation 2024"), $"Expected display value in output:\n{output}");
    }

    [TestMethod]
    public void Render_NestedChildren_OutputsCorrectIndentation()
    {
        var root = new BoxNode { Type = "root", Size = 500 };
        var moov = new BoxNode { Type = "moov", Size = 400, FileOffset = 32, HeaderSize = 8 };
        var mvhd = new BoxNode { Type = "mvhd", Size = 108, FileOffset = 40, HeaderSize = 8 };
        moov.Children.Add(mvhd);
        root.Children.Add(new BoxNode { Type = "ftyp", Size = 32, FileOffset = 0, HeaderSize = 8 });
        root.Children.Add(moov);
        // Add mdat after moov so moov is NOT the last child, this forces the │ continuation bar.
        root.Children.Add(new BoxNode { Type = "mdat", Size = 100, FileOffset = 432, HeaderSize = 8 });

        string output = CaptureRender(root);

        // moov is now a middle child, so its children use │ as the continuation prefix.
        Assert.IsTrue(output.Contains("│"), $"Expected continuation bar '│' for nested child in:\n{output}");
        Assert.IsTrue(output.Contains("mvhd"), $"Expected nested mvhd in output:\n{output}");
    }

    [TestMethod]
    public void Render_MdatNode_ShowsNotExpandedNote()
    {
        var root = new BoxNode { Type = "root", Size = 500 };
        root.Children.Add(new BoxNode { Type = "mdat", Size = 400, FileOffset = 100, HeaderSize = 8 });

        string output = CaptureRender(root);

        Assert.IsTrue(output.Contains("mdat"), $"Expected mdat in output:\n{output}");
        Assert.IsTrue(output.Contains("raw media"), $"Expected 'raw media' note for mdat in:\n{output}");
    }

    [TestMethod]
    public void Render_OutputContainsLegend()
    {
        var root = new BoxNode { Type = "root", Size = 100 };
        root.Children.Add(new BoxNode { Type = "ftyp", Size = 32 });

        string output = CaptureRender(root);

        Assert.IsTrue(output.Contains("LEGEND"), $"Expected legend section in:\n{output}");
        Assert.IsTrue(output.Contains("clipmetascribe"), $"Expected clipmetascribe mention in legend:\n{output}");
        Assert.IsFalse(output.Contains("coming soon"), $"Expected no 'coming soon' claim in legend (the editor shipped):\n{output}");
    }

    [TestMethod]
    public void Render_FriendlyNameShown_WhenKnownFourCC()
    {
        var root = new BoxNode { Type = "root", Size = 100 };
        root.Children.Add(new BoxNode { Type = "ftyp", Size = 32, FileOffset = 0, HeaderSize = 8 });

        string output = CaptureRender(root);

        Assert.IsTrue(output.Contains("File Type"), $"Expected friendly name 'File Type' for ftyp in:\n{output}");
    }

    [TestMethod]
    public void RenderSummary_NoMetadataNodes_ProducesNoOutput()
    {
        var root = new BoxNode { Type = "root", Size = 100 };
        root.Children.Add(new BoxNode { Type = "ftyp", Size = 32, FileOffset = 0, HeaderSize = 8 });

        var sw = new StringWriter();
        TreeRenderer.RenderSummary(root, sw);

        Assert.AreEqual(string.Empty, sw.ToString(),
            "RenderSummary should emit nothing when no nodes have DisplayValue");
    }

    [TestMethod]
    public void RenderSummary_iTunesNode_AppearsinSummary()
    {
        var root = new BoxNode { Type = "root", Size = 200 };
        root.Children.Add(new BoxNode
        {
            Type = "©nam",
            Size = 50,
            FileOffset = 0,
            HeaderSize = 8,
            IsEditable = true,
            DisplayValue = "\"My Vacation 2024\"",
        });

        var sw = new StringWriter();
        TreeRenderer.RenderSummary(root, sw);
        string output = sw.ToString();

        Assert.IsTrue(output.Contains("METADATA SUMMARY"), $"Expected summary header:\n{output}");
        Assert.IsTrue(output.Contains("iTunes Metadata"), $"Expected iTunes section:\n{output}");
        Assert.IsTrue(output.Contains("My Vacation 2024"), $"Expected value in summary:\n{output}");
    }

    [TestMethod]
    public void RenderSummary_WindowsMediaNode_AppearsInWindowsSection()
    {
        var root = new BoxNode { Type = "root", Size = 200 };
        root.Children.Add(new BoxNode
        {
            Type = "WM/Category",
            Size = 40,
            FileOffset = 0,
            HeaderSize = 0,
            IsEditable = true,
            DisplayValue = "\"Action\"",
        });

        var sw = new StringWriter();
        TreeRenderer.RenderSummary(root, sw);
        string output = sw.ToString();

        Assert.IsTrue(output.Contains("Windows Media Metadata"), $"Expected Windows Media section:\n{output}");
        Assert.IsTrue(output.Contains("Tags"), $"Expected 'Tags' friendly name for WM/Category:\n{output}");
        Assert.IsTrue(output.Contains("Action"), $"Expected value in output:\n{output}");
    }

    [TestMethod]
    public void RenderSummary_EditableNode_ShowsEditableMarker()
    {
        var root = new BoxNode { Type = "root", Size = 200 };
        root.Children.Add(new BoxNode
        {
            Type = "WM/Director",
            Size = 40,
            FileOffset = 0,
            HeaderSize = 0,
            IsEditable = true,
            DisplayValue = "\"Jane Smith\"",
        });

        var sw = new StringWriter();
        TreeRenderer.RenderSummary(root, sw);
        string output = sw.ToString();

        Assert.IsTrue(output.Contains("[EDITABLE]"), $"Expected [EDITABLE] marker in summary:\n{output}");
    }
}
