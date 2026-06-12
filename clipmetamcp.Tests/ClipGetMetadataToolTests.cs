using System.Text.Json.Nodes;
using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaMcp.Tests.Helpers;

namespace ClipMetaMcp.Tests;

/// <summary>
/// End-to-end tool tests: a real clip (copied from pristine) read back through the full
/// session → registry → sandbox → Core pipeline.
/// </summary>
[TestClass]
public class ClipGetMetadataToolTests
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

    /// <summary>Copies a pristine clip into the temp library and writes one metadata field to it.</summary>
    private string PrepareClip(string fileName, string field, string value)
    {
        string source = TestClipsLocator.SmallestPristine();
        string dest   = Path.Combine(_tempDir, fileName);
        File.Copy(source, dest);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        new Mp4Writer().WriteMetadata(dest, mutation, NullLogger.Instance);
        return dest;
    }

    /// <summary>Runs one clip_get_metadata call and returns the tools/call result object.</summary>
    private static JsonObject CallGetMetadata(string? libraryRoot, string path)
    {
        var responses = McpHarness.Run(libraryRoot,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, "clip_get_metadata", new JsonObject { ["path"] = path }));
        return (JsonObject)responses[1]["result"]!;
    }

    private static string ErrorText(JsonObject result) =>
        result["content"]![0]!["text"]!.GetValue<string>();

    [TestMethod]
    public void GetMetadata_ReturnsWrittenField()
    {
        string clip = PrepareClip("clip.mp4", "game", "Team Fortress 2");

        JsonObject result = CallGetMetadata(_tempDir, clip);

        Assert.IsNull(result["isError"]);
        Assert.AreEqual("Team Fortress 2",
            result["structuredContent"]?["fields"]?["game"]?.GetValue<string>());
    }

    [TestMethod]
    public void GetMetadata_TextContentMatchesStructuredContent()
    {
        string clip = PrepareClip("clip.mp4", "game", "TF2");

        JsonObject result = CallGetMetadata(_tempDir, clip);

        Assert.AreEqual(result["structuredContent"]!.ToJsonString(), ErrorText(result));
    }

    [TestMethod]
    public void GetMetadata_ExcludesInternalSchemaField()
    {
        string clip = PrepareClip("clip.mp4", "game", "TF2");

        JsonObject result = CallGetMetadata(_tempDir, clip);

        Assert.IsNull(result["structuredContent"]?["fields"]?[ClipMetaSchema.Schema],
            "the internal schema field must never appear in tool output");
    }

    [TestMethod]
    public void GetMetadata_RelativePath_ResolvesAgainstLibraryRoot()
    {
        PrepareClip("clip.mp4", "game", "TF2");

        JsonObject result = CallGetMetadata(_tempDir, "clip.mp4");

        Assert.IsNull(result["isError"]);
        Assert.AreEqual("TF2", result["structuredContent"]?["fields"]?["game"]?.GetValue<string>());
    }

    [TestMethod]
    public void GetMetadata_NoRootConfigured_AbsolutePathStillWorks()
    {
        // Manual/dev installs have no CLIPMETA_LIBRARY_ROOT: reads are allowed anywhere (spec §3
        // confines only writes to refusal in that state).
        string clip = PrepareClip("clip.mp4", "game", "TF2");

        JsonObject result = CallGetMetadata(null, clip);

        Assert.IsNull(result["isError"]);
    }

    [TestMethod]
    public void GetMetadata_MissingFile_IsToolError()
    {
        JsonObject result = CallGetMetadata(_tempDir, "nope.mp4");

        Assert.IsTrue(result["isError"]?.GetValue<bool>());
        StringAssert.Contains(ErrorText(result), "No file exists");
    }

    [TestMethod]
    public void GetMetadata_NonMp4_IsToolError()
    {
        string textFile = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(textFile, "not a clip");

        JsonObject result = CallGetMetadata(_tempDir, textFile);

        Assert.IsTrue(result["isError"]?.GetValue<bool>());
        StringAssert.Contains(ErrorText(result), "not an .mp4");
    }

    [TestMethod]
    public void GetMetadata_PathOutsideRoot_IsRefused()
    {
        // Clip lives in _tempDir, but the sandbox root is a subdirectory — the absolute path
        // points outside the library and must be refused, not read.
        string clip = PrepareClip("clip.mp4", "game", "TF2");
        string innerRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(innerRoot);

        JsonObject result = CallGetMetadata(innerRoot, clip);

        Assert.IsTrue(result["isError"]?.GetValue<bool>());
        StringAssert.Contains(ErrorText(result), "outside the configured clips library");
    }

    [TestMethod]
    public void GetMetadata_TraversalEscape_IsRefused()
    {
        // A relative path that ".."-escapes the root must be caught after resolution.
        PrepareClip("clip.mp4", "game", "TF2");
        string innerRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(innerRoot);

        JsonObject result = CallGetMetadata(innerRoot, @"..\clip.mp4");

        Assert.IsTrue(result["isError"]?.GetValue<bool>());
        StringAssert.Contains(ErrorText(result), "outside the configured clips library");
    }

    [TestMethod]
    public void GetMetadata_MissingPathArgument_IsToolError()
    {
        var responses = McpHarness.Run(_tempDir,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, "clip_get_metadata", new JsonObject()));
        var result = (JsonObject)responses[1]["result"]!;

        Assert.IsTrue(result["isError"]?.GetValue<bool>());
        StringAssert.Contains(ErrorText(result), "'path' argument is required");
    }

    [TestMethod]
    public void GetMetadata_GarbageMp4_NeverProtocolError_SessionSurvives()
    {
        // A file with an .mp4 name but garbage bytes. Mp4Parser is deliberately lenient
        // (clamps oversized boxes, stops at damage) so this parses to a tree with no metadata
        // rather than throwing — the contract at the MCP layer is: a result (here: empty
        // fields), never a JSON-RPC error, never a dead session.
        string fake = Path.Combine(_tempDir, "fake.mp4");
        File.WriteAllBytes(fake, [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03]);

        var responses = McpHarness.Run(_tempDir,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, "clip_get_metadata", new JsonObject { ["path"] = fake }),
            McpHarness.Request(3, "ping"));

        Assert.IsNull(responses[1]["error"], "tool failures must not be JSON-RPC errors");
        var result = (JsonObject)responses[1]["result"]!;
        var fields = result["structuredContent"]?["fields"] as JsonObject;
        Assert.IsNotNull(fields);
        Assert.AreEqual(0, fields.Count, "garbage bytes cannot contain clipmeta fields");
        // And the session survived to answer the next request.
        Assert.AreEqual(3, responses[2]["id"]?.GetValue<int>());
    }
}
