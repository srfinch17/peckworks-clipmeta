using System.Text.Json.Nodes;
using ClipMetaCore.Schema;
using ClipMetaCore.Watching;
using ClipMetaMcp.Tests.Helpers;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaMcp.Tests;

/// <summary>
/// MCP-shape tests for the deferred-tag queue tools (library_queue_tag, library_flush_queue,
/// library_queue_status). These drive the full session → registry → sandbox → TagQueue pipeline
/// and assert MCP protocol shape: isError on refusals, structured content on success.
///
/// The Core drain/lock behavior is already proven in clipmetascribe.Tests; here we test that the
/// MCP surface wires up correctly: enqueue persists to the queue file, status reflects pending
/// entries, and tools refuse cleanly when no library is configured.
/// </summary>
[TestClass]
public class QueueToolsTests
{
    private string _lib = null!;

    [TestInitialize]
    public void SetUp()
    {
        _lib = Path.Combine(Path.GetTempPath(), "clipmeta-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_lib);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_lib))
            Directory.Delete(_lib, recursive: true);
    }

    /// <summary>Copies the smallest pristine clip into the library and returns its path.</summary>
    private string PrepareClip(string fileName = "clip.mp4")
    {
        if (!TestClipsLocator.PristineClipsPresent())
            Assert.Inconclusive("No test clips in testclips/pristine — skipped (e.g. CI).");

        string dest = Path.Combine(_lib, fileName);
        File.Copy(TestClipsLocator.SmallestPristine(), dest);
        return dest;
    }

    private JsonObject Call(string tool, JsonObject arguments, string? libraryRoot = "lib")
    {
        string? root = libraryRoot == "lib" ? _lib : libraryRoot;
        var responses = McpHarness.Run(root,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, tool, arguments));
        return (JsonObject)responses[1]["result"]!;
    }

    private static JsonObject Structured(JsonObject result) => (JsonObject)result["structuredContent"]!;

    private static string ErrorText(JsonObject result) =>
        result["content"]![0]!["text"]!.GetValue<string>();

    private static void AssertRefused(JsonObject result, string messageFragment)
    {
        Assert.IsTrue(result["isError"]?.GetValue<bool>(), "expected a tool refusal");
        StringAssert.Contains(ErrorText(result), messageFragment);
    }

    // ── library_queue_tag ────────────────────────────────────────────────────────────────

    /// <summary>
    /// After a queue_tag call the queue file must exist and contain exactly one entry for the
    /// clip, and the tool response must report the clip as queued with pending >= 1.
    /// We don't hold a real OS lock here — the brief says to assert queue persistence via
    /// TagQueue.Load when an in-process lock is awkward; drain/lock is proven in Core tests.
    /// </summary>
    [TestMethod]
    public void QueueTag_EnqueuesClip_PersistsToQueueFile()
    {
        string clip = PrepareClip();

        JsonObject result = Call("library_queue_tag", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["tags"] = "headshot" },
        });

        // Tool must not report an error.
        Assert.IsNull(result["isError"], "expected success but got: " + ErrorText(result));

        JsonObject s = Structured(result);
        Assert.AreEqual(clip, s["queued"]!.GetValue<string>(), "'queued' must echo the resolved path");
        Assert.IsTrue(s["pending"]!.GetValue<int>() >= 1, "pending must be >= 1 after enqueue");

        // Verify via Core: the queue file must contain one entry for the clip.
        TagQueueData data = TagQueue.Load(_lib);
        Assert.AreEqual(1, data.Entries.Count, "queue must have exactly one entry");
        Assert.AreEqual(clip, data.Entries[0].ClipPath, StringComparer.OrdinalIgnoreCase,
            "the queued clip path must match the resolved path");
        // 'tags' is an accumulate field, so it routes to AppendFields (not SetFields) — re-tagging
        // the same clip merges instead of overwriting.
        Assert.IsTrue(data.Entries[0].Mutation.AppendFields.Values.Contains("headshot"),
            "the queued mutation must append the 'tags' value");
    }

    [TestMethod]
    public void QueueTag_RoutesNotesTagsPlayersToAppend_RestToSet()
    {
        // The P0 fix at the tool layer: free-text/list fields accumulate (AppendFields), scalar
        // fields replace (SetFields). An empty .mp4 is enough — queue_tag stores the path, never parses.
        string clip = Path.Combine(_lib, "clip.mp4");
        File.WriteAllBytes(clip, Array.Empty<byte>());

        JsonObject result = Call("library_queue_tag", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject
            {
                ["notes"] = "creepy demon lady",
                ["players"] = "chuck|chicken",
                ["game"] = "Sons of the Forest",
            },
        });
        Assert.IsNull(result["isError"], "expected success");

        QueuedMutation m = TagQueue.Load(_lib).Entries.Single().Mutation;
        Assert.IsTrue(m.AppendFields.ContainsKey(ClipMetaSchema.AtomName("notes")), "notes → append");
        Assert.IsTrue(m.AppendFields.ContainsKey(ClipMetaSchema.AtomName("players")), "players → append");
        Assert.IsTrue(m.SetFields.ContainsKey(ClipMetaSchema.AtomName("game")), "game → set");
    }

    // ── library_queue_status ──────────────────────────────────────────────────────────────

    /// <summary>
    /// After enqueueing a tag, library_queue_status must report pending >= 1 and an entry whose
    /// changedFields includes the display name of the field we set ("tags").
    /// </summary>
    [TestMethod]
    public void QueueStatus_ReflectsPendingEntries()
    {
        string clip = PrepareClip();

        // Enqueue a tag.
        JsonObject queueResult = Call("library_queue_tag", new JsonObject
        {
            ["path"] = clip,
            ["fields"] = new JsonObject { ["tags"] = "headshot" },
        });
        Assert.IsNull(queueResult["isError"], "enqueue must succeed: " + ErrorText(queueResult));

        // Now check status.
        JsonObject statusResult = Call("library_queue_status", new JsonObject());

        Assert.IsNull(statusResult["isError"], "status must succeed: " + ErrorText(statusResult));
        JsonObject s = Structured(statusResult);
        int pending = s["pending"]!.GetValue<int>();
        Assert.IsTrue(pending >= 1, $"pending must be >= 1, got {pending}");

        JsonArray entries = s["entries"]!.AsArray();
        Assert.IsTrue(entries.Count >= 1, "entries array must have at least one item");

        // Find the entry for our clip.
        JsonObject? entry = entries
            .Select(e => (JsonObject)e!)
            .FirstOrDefault(e => e["path"]!.GetValue<string>()
                .Equals(clip, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(entry, "status entries must include our queued clip");

        var changedFields = entry["changedFields"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToList();
        CollectionAssert.Contains(changedFields, "tags",
            "changedFields must include 'tags' (the display name, not the qualified atom)");

        // Shape checks.
        Assert.IsTrue(entry.ContainsKey("ageSeconds"), "entry must have ageSeconds");
        Assert.IsTrue(entry.ContainsKey("locked"), "entry must have locked");
    }

    // ── library_flush_queue ───────────────────────────────────────────────────────────────

    /// <summary>
    /// With NO library configured, library_flush_queue must return isError:true and the message
    /// must name the env var (RequireRoot refusal). Mirrors how existing tests assert no-library
    /// refusals.
    /// </summary>
    [TestMethod]
    public void FlushQueue_NoLibrary_RefusesCleanly()
    {
        JsonObject result = Call("library_flush_queue", new JsonObject(), libraryRoot: null);

        AssertRefused(result, "CLIPMETA_LIBRARY_ROOT");
    }

    // ── no-library refusals ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void QueueTag_NoLibrary_RefusesCleanly()
    {
        // Path doesn't matter — ResolveWritePath fires first when Root is null.
        JsonObject result = Call("library_queue_tag", new JsonObject
        {
            ["path"] = "clip.mp4",
            ["fields"] = new JsonObject { ["tags"] = "test" },
        }, libraryRoot: null);

        AssertRefused(result, "Writing is disabled");
    }

    [TestMethod]
    public void QueueStatus_NoLibrary_RefusesCleanly()
    {
        JsonObject result = Call("library_queue_status", new JsonObject(), libraryRoot: null);

        AssertRefused(result, "CLIPMETA_LIBRARY_ROOT");
    }
}
