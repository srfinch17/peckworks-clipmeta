using System.Text.Json.Nodes;
using ClipMetaMcp.Tests.Helpers;

namespace ClipMetaMcp.Tests;

/// <summary>
/// library_watching works on filenames + access times, so these tests need only empty .mp4 files
/// (no real clips, no CI skip). They assert result shape and the unconfigured-library refusal.
/// The MCP session wraps a tool's JsonObject under result["structuredContent"] on success and sets
/// result["isError"] on refusal, so success assertions drill through Structured(...) exactly like
/// Phase2ReadToolsTests.
/// </summary>
[TestClass]
public class LibraryWatchingToolTests
{
    private string _lib = null!;

    [TestInitialize]
    public void SetUp()
    {
        _lib = Path.Combine(Path.GetTempPath(), "clipmeta-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_lib);
        File.WriteAllBytes(Path.Combine(_lib, "a.mp4"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(_lib, "b.mp4"), Array.Empty<byte>());
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_lib))
            Directory.Delete(_lib, recursive: true);
    }

    private JsonObject Call(JsonObject args, string? root)
    {
        var responses = McpHarness.Run(root,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, "library_watching", args));
        return (JsonObject)responses[1]["result"]!;
    }

    private static JsonObject Structured(JsonObject result) => (JsonObject)result["structuredContent"]!;

    [TestMethod]
    public void Watching_WithAccessFallback_ReturnsShapedCandidates()
    {
        JsonObject result = Call(new JsonObject { ["include_access_fallback"] = true }, _lib);

        Assert.IsNull(result["isError"]);
        var candidates = (JsonArray)Structured(result)["candidates"]!;
        Assert.IsTrue(candidates.Count >= 1, "access fallback should surface the temp clips");

        JsonObject first = (JsonObject)candidates[0]!;
        foreach (string key in new[]
                 { "path", "name", "source", "lastAccessTimeUtc", "secondsSinceAccess", "inUse", "confidence" })
            Assert.IsTrue(first.ContainsKey(key), $"candidate missing '{key}'");

        string confidence = first["confidence"]!.GetValue<string>();
        Assert.IsTrue(confidence is "high" or "low");
    }

    [TestMethod]
    public void Watching_RespectsLimit()
    {
        JsonObject result = Call(new JsonObject { ["limit"] = 1 }, _lib);
        Assert.AreEqual(1, ((JsonArray)Structured(result)["candidates"]!).Count);
    }

    [TestMethod]
    public void Watching_NoLibraryConfigured_Refuses()
    {
        JsonObject result = Call(new JsonObject(), root: null);
        Assert.IsTrue(result["isError"]?.GetValue<bool>() == true, "must refuse with no library configured");
    }

    [TestMethod]
    public void Watching_StillReturnsAnyLiveTarget_WithNoWatcherWired()
    {
        // The harness wires no ReviewWatcher, so library_watching uses the live-poll fallback — the
        // existing contract (incl. anyLiveTarget) must stay intact.
        JsonObject result = Call(new JsonObject { ["include_access_fallback"] = true }, _lib);
        Assert.IsNull(result["isError"]);
        Assert.IsTrue(Structured(result).ContainsKey("anyLiveTarget"));
    }

    [TestMethod]
    public void Watching_ReviewArray_AbsentWhenNoFlags()
    {
        // No watcher, no segments → no review flags → the 'review' key is omitted (additive, opt-in).
        JsonObject result = Call(new JsonObject { ["include_access_fallback"] = true }, _lib);
        Assert.IsNull(Structured(result)["review"]);
    }

    [TestMethod]
    public void Watching_AlwaysEchoesQueueDepthAndDrainOutcome()
    {
        // The queue is empty here, but the fields must be present (and zero) so a caller can always
        // confirm whether a queued write landed — no more "silent flush".
        JsonObject s = Structured(Call(new JsonObject { ["include_access_fallback"] = true }, _lib));

        Assert.AreEqual(0, s["queuePending"]!.GetValue<int>());
        var drained = (JsonObject)s["drainedFromQueue"]!;
        foreach (string key in new[] { "written", "dropped", "stillQueued" })
            Assert.IsTrue(drained.ContainsKey(key), $"drainedFromQueue missing '{key}'");
    }

    [TestMethod]
    public void Watching_NormalCall_HasNoWarning()
    {
        JsonObject result = Call(new JsonObject { ["include_access_fallback"] = true }, _lib);
        Assert.IsNull(result["isError"]);
        // No player is playing our temp clips, so there is no wrong-directory warning.
        Assert.IsNull(Structured(result)["warning"], "warning must be absent when no foreign player is detected");
    }

    [TestMethod]
    public void Watching_NothingLive_ReportsAnyLiveTargetFalse()
    {
        // No player open and the temp clips aren't locked: access-time candidates may surface, but
        // none is actually live, so the caller must be told not to auto-tag.
        JsonObject result = Call(new JsonObject { ["include_access_fallback"] = true }, _lib);
        Assert.IsNull(result["isError"]);

        JsonObject structured = Structured(result);
        Assert.IsTrue(structured.ContainsKey("anyLiveTarget"), "library_watching must report anyLiveTarget");
        Assert.IsFalse(structured["anyLiveTarget"]!.GetValue<bool>(),
            "nothing open or locked → anyLiveTarget false");
    }
}
