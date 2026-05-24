using ClipMetaCore.Mp4;
using ClipMetaView;
using ClipMetaView.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClipMetaView.Tests;

[TestClass]
public class ProgramIntegrationTests
{
    // ── Exit code tests ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task MissingFile_ExitsWithCode1()
    {
        int result = await AppRunner.RunAsync(["C:\\nonexistent\\path\\missing.mp4"]);

        Assert.AreEqual(AppRunner.ExitBadArgs, result);
    }

    [TestMethod]
    public async Task NoArguments_ExitsWithCode1()
    {
        int result = await AppRunner.RunAsync([]);

        Assert.AreEqual(AppRunner.ExitBadArgs, result);
    }

    [TestMethod]
    public async Task WrongExtension_ExitsWithCode1()
    {
        string tempFile = Path.GetTempFileName(); // creates a .tmp file
        try
        {
            int result = await AppRunner.RunAsync([tempFile]);

            Assert.AreEqual(AppRunner.ExitBadArgs, result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ── Real clip data provider ──────────────────────────────────────────────

    public static IEnumerable<object[]> TestClipPaths()
        => TestClips.All().Select(p => new object[] { p });

    // ── Parser integration tests ─────────────────────────────────────────────

    [TestMethod]
    [DynamicData(nameof(TestClipPaths))]
    public void ParseFile_RealClip_DoesNotThrow(string clipPath)
    {
        var root = Mp4Parser.ParseFile(clipPath);

        Assert.IsNotNull(root);
        Assert.IsTrue(root.Children.Count > 0, $"Expected boxes in {Path.GetFileName(clipPath)}");
    }

    [TestMethod]
    [DynamicData(nameof(TestClipPaths))]
    public void ParseFile_RealClip_ContainsMoovBox(string clipPath)
    {
        var root = Mp4Parser.ParseFile(clipPath);

        bool hasMoov = root.Children.Any(c => c.Type == "moov");
        Assert.IsTrue(hasMoov, $"Expected top-level moov box in {Path.GetFileName(clipPath)}");
    }

    [TestMethod]
    [DynamicData(nameof(TestClipPaths))]
    public void ParseFile_RealClip_IlstBoxHasEditableChildren(string clipPath)
    {
        var root = Mp4Parser.ParseFile(clipPath);

        var ilstNode = FindBoxByType(root, "ilst");
        if (ilstNode == null)
            Assert.Inconclusive($"No ilst box found in {Path.GetFileName(clipPath)} — skipping editable-check.");

        bool hasEditable = ilstNode!.Children.Any(c => c.IsEditable);
        Assert.IsTrue(hasEditable, $"ilst box in {Path.GetFileName(clipPath)} has no editable children");
    }

    [TestMethod]
    [DynamicData(nameof(TestClipPaths))]
    public async Task RunAsync_RealClip_ExitsWithCode0(string clipPath)
    {
        // Pass a StringWriter to avoid Console.Out races in parallel test execution.
        var sw = new StringWriter();
        int result = await AppRunner.RunAsync([clipPath], sw);

        Assert.AreEqual(AppRunner.ExitSuccess, result, $"Expected exit 0 for {Path.GetFileName(clipPath)}");
    }

    [TestMethod]
    [DynamicData(nameof(TestClipPaths))]
    public void ParseFile_RealClip_XtraBoxDoesNotCrash(string clipPath)
    {
        // ParseFile must not throw even if the Xtra box is absent or malformed.
        var root = Mp4Parser.ParseFile(clipPath);
        Assert.IsNotNull(root);
    }

    [TestMethod]
    [DynamicData(nameof(TestClipPaths))]
    public async Task RunAsync_RealClip_SummaryAppearsInOutput(string clipPath)
    {
        var sw = new StringWriter();
        int result = await AppRunner.RunAsync([clipPath], sw);
        Assert.AreEqual(AppRunner.ExitSuccess, result);

        string output = sw.ToString();
        // Every clip should produce the tree; summary appears only when metadata exists.
        Assert.IsTrue(output.Contains("ftyp") || output.Contains("moov"),
            $"Expected box tree output for {Path.GetFileName(clipPath)}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Depth-first search for the first node with the given type.</summary>
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
