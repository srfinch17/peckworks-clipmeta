using System.Text.Json.Nodes;
using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaMcp.Tests.Helpers;
using ClipMetaMcp.Tools;

namespace ClipMetaMcp.Tests;

/// <summary>
/// Enforces THE IRON RULE (spec §2, risk R1): nothing in the serve path may write to
/// Console.Out. One stray byte on stdout corrupts the protocol channel and produces the classic
/// "Failed to connect". The invariant is enforced by this test, not by code-review vigilance.
/// </summary>
[TestClass]
public class StdoutPurityTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void EveryRegisteredTool_WritesNothingToConsoleOut()
    {
        // Arrange: a real clip with metadata, and the full registry.
        string source = TestClipsLocator.SmallestPristine();
        string clip = Path.Combine(_tempDir, "clip.mp4");
        File.Copy(source, clip);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName("game")] = "TF2";
        new Mp4Writer().WriteMetadata(clip, mutation, NullLogger.Instance);

        // A real backup with the exact name clip_restore_backup's ExampleArguments references,
        // so the backup tools (restore/prune) execute their happy path under stdout capture
        // rather than short-circuiting on a missing file.
        File.Copy(clip, clip + ".bak-20200101-000000");

        var registry = new ToolRegistry();
        var sandbox = new LibrarySandbox(_tempDir);
        ReadTools.RegisterAll(registry, sandbox);
        WriteTools.RegisterAll(registry, sandbox);
        QueueTools.RegisterAll(registry, sandbox);

        // Each tool supplies its own runnable example arguments (a ToolDefinition member), so
        // this test covers every registered tool with no per-tool mapping to maintain here.
        var requests = new List<string> { McpHarness.InitializeRequest };
        int id = 2;
        foreach (ToolDefinition tool in registry.All)
            requests.Add(McpHarness.ToolCall(id++, tool.Name, tool.ExampleArguments(clip)));

        // Act: run the session with Console.Out captured. The harness writes protocol output to
        // its own StringWriter, so anything landing on Console.Out is a stray by definition.
        TextWriter originalOut = Console.Out;
        string stray;
        IReadOnlyList<JsonObject> responses;
        try
        {
            using var capture = new StringWriter();
            Console.SetOut(capture);
            responses = McpHarness.Run(_tempDir, requests.ToArray());
            stray = capture.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Assert: every request answered, zero bytes on Console.Out, no tool errored
        // (an early error would mean the happy path never actually executed).
        Assert.AreEqual(requests.Count, responses.Count, "every request must get a response");
        foreach (JsonObject response in responses.Skip(1))
            Assert.IsNull(response["result"]?["isError"], "tool errored, happy path not exercised");
        Assert.AreEqual(string.Empty, stray,
            "a serve-path component wrote to Console.Out, this would corrupt the MCP channel");
    }
}
