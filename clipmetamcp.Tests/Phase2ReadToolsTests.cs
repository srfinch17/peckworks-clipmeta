using System.Text.Json.Nodes;
using ClipMetaCore.Logging;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaMcp.Tests.Helpers;

namespace ClipMetaMcp.Tests;

/// <summary>
/// End-to-end tests for the phase-2 read tools (clip_get_stats and the library_* family),
/// driven through the full session → registry → sandbox → Core pipeline.
///
/// One shared library is built ONCE for the whole class (a single ~70 MB pristine copy plus a
/// tiny garbage .mp4) because per-test copies would multiply real-clip I/O by every test here.
/// All tests treat the library as read-only — except the index tests, whose only side effect
/// is the .clipmeta-index cache file, which no other tool reads (everything else enumerates
/// *.mp4 only).
///
/// Library layout:
///   tagged.mp4        — real clip; game + tags + one custom field; lastWrite = now − 1 h
///   sub\noise.mp4     — 8 garbage bytes; parses to an empty tree (lenient parser);
///                       lastWrite = now (the NEWEST file, for ordering assertions)
///   readme.txt        — must never appear in any listing or export
/// </summary>
[TestClass]
public class Phase2ReadToolsTests
{
    private static string _lib = null!;
    private static string _taggedPath = null!;
    private static string _noisePath = null!;

    [ClassInitialize]
    public static void BuildLibrary(TestContext _)
    {
        // Clip-less machine (CI)? Skip building the shared library; [TestInitialize] then skips
        // each test. Throwing Inconclusive from ClassInitialize would FAIL the class, not skip
        // it, so the guard must short-circuit here rather than via the locator's skip path.
        if (!TestClipsLocator.PristineClipsPresent())
            return;

        _lib = Path.Combine(Path.GetTempPath(), "clipmeta-p2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_lib, "sub"));

        _taggedPath = Path.Combine(_lib, "tagged.mp4");
        File.Copy(TestClipsLocator.SmallestPristine(), _taggedPath);
        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "alpha|beta";
        mutation.SetFields[ClipMetaSchema.AtomName("map")] = "2fort"; // custom field
        new Mp4Writer().WriteMetadata(_taggedPath, mutation, NullLogger.Instance);

        _noisePath = Path.Combine(_lib, "sub", "noise.mp4");
        File.WriteAllBytes(_noisePath, [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03]);

        File.WriteAllText(Path.Combine(_lib, "readme.txt"), "not a clip");

        // Deterministic newest-first ordering: copy/tag timestamps land within the same tick
        // range, so pin them explicitly instead of asserting on filesystem race outcomes.
        File.SetLastWriteTimeUtc(_taggedPath, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(_noisePath, DateTime.UtcNow);
    }

    [ClassCleanup]
    public static void DeleteLibrary()
    {
        if (_lib != null && Directory.Exists(_lib))
            Directory.Delete(_lib, recursive: true);
    }

    // Skips every test in this class when no clips are present (CI). Unlike ClassInitialize,
    // an Inconclusive thrown here cleanly skips the individual test rather than failing it.
    [TestInitialize]
    public void RequireClips()
    {
        if (!TestClipsLocator.PristineClipsPresent())
            Assert.Inconclusive("No test clips in testclips/pristine — skipped (e.g. CI).");
    }

    /// <summary>Runs one tool call against the shared library and returns the call result.</summary>
    private static JsonObject Call(string tool, JsonObject arguments, string? libraryRoot = "lib")
    {
        // Default sentinel "lib" → the shared library; explicit null → unconfigured sandbox.
        string? root = libraryRoot == "lib" ? _lib : libraryRoot;
        var responses = McpHarness.Run(root,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, tool, arguments));
        return (JsonObject)responses[1]["result"]!;
    }

    private static JsonObject Structured(JsonObject result) => (JsonObject)result["structuredContent"]!;

    private static string ErrorText(JsonObject result) =>
        result["content"]![0]!["text"]!.GetValue<string>();

    private static void AssertOk(JsonObject result) =>
        Assert.IsNull(result["isError"], "expected success but got: " + ErrorText(result));

    private static void AssertRefused(JsonObject result, string messageFragment)
    {
        Assert.IsTrue(result["isError"]?.GetValue<bool>(), "expected a tool refusal");
        StringAssert.Contains(ErrorText(result), messageFragment);
    }

    // ── tools/list surface ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void ToolsList_ContainsTheFullToolSurface()
    {
        var responses = McpHarness.Run(_lib,
            McpHarness.InitializeRequest,
            McpHarness.Request(2, "tools/list"));
        var names = responses[1]["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())
            .ToList();

        CollectionAssert.AreEqual(
            new[]
            {
                "clip_get_metadata", "library_list", "library_find",
                "library_vocab", "library_export", "library_search_index",
                "library_watching",
                "clip_set_fields", "clip_append_field", "clip_clear_fields", "clip_clear_all",
                "library_list_backups", "clip_restore_backup", "clip_prune_backups",
                "library_queue_tag", "library_flush_queue", "library_queue_status",
            },
            names,
            "tools/list must expose the read + write + backup + queue surface in registration order");
    }

    // ── clip_get_metadata enrichment (phase-3 field report: one call, whole picture) ──────

    [TestMethod]
    public void GetMetadata_IncludesSizeAndFieldCategorization()
    {
        JsonObject result = Call("clip_get_metadata", new JsonObject { ["path"] = _taggedPath });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.IsTrue(s["sizeBytes"]!.GetValue<long>() > 0);
        Assert.AreEqual("Team Fortress 2", s["fields"]!["game"]!.GetValue<string>(),
            "values and categorization must come from ONE call");

        var knownUnset = s["knownUnset"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        CollectionAssert.IsSubsetOf(new[] { "players", "timecode", "rating", "notes" }, knownUnset);
        CollectionAssert.DoesNotContain(knownUnset, "game");

        var custom = s["customFields"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        CollectionAssert.AreEqual(new[] { "map" }, custom);
    }

    // ── library_list ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void List_ReturnsAllMp4s_NewestFirst_IgnoresNonMp4()
    {
        JsonObject result = Call("library_list", new JsonObject());

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual(2, s["totalMatches"]!.GetValue<int>(), "readme.txt must not be listed");
        Assert.IsFalse(s["truncated"]!.GetValue<bool>());

        var names = s["clips"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
        CollectionAssert.AreEqual(new[] { "noise.mp4", "tagged.mp4" }, names,
            "clips must be ordered newest first (noise.mp4 has the pinned-newest timestamp)");

        JsonObject first = (JsonObject)s["clips"]![0]!;
        Assert.AreEqual(_noisePath, first["path"]!.GetValue<string>());
        Assert.AreEqual(8, first["sizeBytes"]!.GetValue<long>());
        Assert.IsNotNull(first["lastModified"]);
    }

    [TestMethod]
    public void List_Pattern_FiltersByFileName()
    {
        JsonObject result = Call("library_list", new JsonObject { ["pattern"] = "tag*" });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual(1, s["totalMatches"]!.GetValue<int>());
        Assert.AreEqual("tagged.mp4", s["clips"]![0]!["name"]!.GetValue<string>());
    }

    [TestMethod]
    public void List_Subfolder_RestrictsScope()
    {
        JsonObject result = Call("library_list", new JsonObject { ["subfolder"] = "sub" });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual(1, s["totalMatches"]!.GetValue<int>());
        Assert.AreEqual("noise.mp4", s["clips"]![0]!["name"]!.GetValue<string>());
    }

    [TestMethod]
    public void List_NonRecursive_TopLevelOnly()
    {
        JsonObject result = Call("library_list", new JsonObject { ["recursive"] = false });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual(1, s["totalMatches"]!.GetValue<int>());
        Assert.AreEqual("tagged.mp4", s["clips"]![0]!["name"]!.GetValue<string>());
    }

    [TestMethod]
    public void List_Limit_TruncatesAndSaysSo()
    {
        JsonObject result = Call("library_list", new JsonObject { ["limit"] = 1 });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual(2, s["totalMatches"]!.GetValue<int>());
        Assert.AreEqual(1, s["returned"]!.GetValue<int>());
        Assert.IsTrue(s["truncated"]!.GetValue<bool>(),
            "the model must be told the listing is incomplete");
    }

    [TestMethod]
    public void List_SubfolderTraversalEscape_IsRefused()
    {
        JsonObject result = Call("library_list", new JsonObject { ["subfolder"] = ".." });

        AssertRefused(result, "outside the configured clips library");
    }

    [TestMethod]
    public void List_NoRootConfigured_IsRefused()
    {
        JsonObject result = Call("library_list", new JsonObject(), libraryRoot: null);

        AssertRefused(result, "No clips library is configured");
    }

    // ── library_find ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Find_CaseInsensitiveSubstring_ReturnsMatchingPath()
    {
        JsonObject result = Call("library_find",
            new JsonObject { ["field"] = "game", ["value"] = "fortress" });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual(1, s["matchCount"]!.GetValue<int>());
        Assert.AreEqual(_taggedPath, s["paths"]![0]!.GetValue<string>());
    }

    [TestMethod]
    public void Find_NoMatches_EmptyResultNotError()
    {
        JsonObject result = Call("library_find",
            new JsonObject { ["field"] = "game", ["value"] = "zzz-no-such-game" });

        AssertOk(result);
        Assert.AreEqual(0, Structured(result)["matchCount"]!.GetValue<int>());
    }

    [TestMethod]
    public void Find_NoRootConfigured_IsRefused()
    {
        JsonObject result = Call("library_find",
            new JsonObject { ["field"] = "game", ["value"] = "x" }, libraryRoot: null);

        AssertRefused(result, "No clips library is configured");
    }

    // ── library_vocab ────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Vocab_PipeField_SplitsItemsAndCounts()
    {
        JsonObject result = Call("library_vocab", new JsonObject { ["field"] = "tags" });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual(1, s["clipsWithField"]!.GetValue<int>());
        Assert.AreEqual(2, s["distinctValues"]!.GetValue<int>());
        Assert.AreEqual(1, s["values"]!["alpha"]!.GetValue<int>());
        Assert.AreEqual(1, s["values"]!["beta"]!.GetValue<int>());
    }

    [TestMethod]
    public void Vocab_NoRootConfigured_IsRefused()
    {
        JsonObject result = Call("library_vocab",
            new JsonObject { ["field"] = "tags" }, libraryRoot: null);

        AssertRefused(result, "No clips library is configured");
    }

    // ── library_export ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Export_Json_ReturnsRecordsWithFields()
    {
        JsonObject result = Call("library_export", new JsonObject());

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual("json", s["format"]!.GetValue<string>());
        Assert.AreEqual(2, s["clipCount"]!.GetValue<int>(), "garbage mp4 still yields an (empty) record");

        JsonObject? taggedRecord = s["records"]!.AsArray()
            .Select(r => (JsonObject)r!)
            .FirstOrDefault(r => r["file"]!.GetValue<string>() == _taggedPath);
        Assert.IsNotNull(taggedRecord);
        Assert.AreEqual("Team Fortress 2", taggedRecord["fields"]!["game"]!.GetValue<string>());
        Assert.IsNull(taggedRecord["fields"]![ClipMetaSchema.Schema]);
    }

    [TestMethod]
    public void Export_Csv_IsCoreWriterOutput_ByteForByte()
    {
        JsonObject result = Call("library_export", new JsonObject { ["format"] = "csv" });

        AssertOk(result);
        string csv = Structured(result)["csv"]!.GetValue<string>();

        // The tool must not have its own CSV dialect: regenerate via the same Core writer the
        // CLI uses and require identical bytes (column order, quoting, everything).
        var records = ClipMetaExporter.GetRecords(
            Directory.EnumerateFiles(_lib, "*.mp4", SearchOption.AllDirectories));
        using var expected = new StringWriter();
        ClipMetaExporter.WriteCsv(records, expected);
        Assert.AreEqual(expected.ToString(), csv);

        StringAssert.StartsWith(csv, "file,game,players,tags,timecode,rating,notes",
            "well-known columns must lead in schema order");
    }

    [TestMethod]
    public void Export_UnknownFormat_IsToolError()
    {
        JsonObject result = Call("library_export", new JsonObject { ["format"] = "xml" });

        AssertRefused(result, "Unknown format");
    }

    [TestMethod]
    public void Export_NoRootConfigured_IsRefused()
    {
        JsonObject result = Call("library_export", new JsonObject(), libraryRoot: null);

        AssertRefused(result, "No clips library is configured");
    }

    // ── library_search_index ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void SearchIndex_RebuildAndQuery_FindsTaggedClip()
    {
        JsonObject result = Call("library_search_index",
            new JsonObject { ["rebuild"] = true, ["field"] = "game", ["value"] = "fortress" });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.IsTrue(s["rebuilt"]!.GetValue<bool>());
        Assert.AreEqual(2, s["clipCount"]!.GetValue<int>());
        Assert.AreEqual(1, s["matchCount"]!.GetValue<int>());
        Assert.AreEqual(_taggedPath, s["matches"]![0]!["path"]!.GetValue<string>());
        Assert.IsTrue(File.Exists(Path.Combine(_lib, ClipMetaIndex.IndexFileName)),
            "the index must be persisted into the library root");
    }

    [TestMethod]
    public void SearchIndex_SummaryWithoutField_NoMatchesKey()
    {
        JsonObject result = Call("library_search_index", new JsonObject { ["rebuild"] = true });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.AreEqual(2, s["clipCount"]!.GetValue<int>());
        Assert.IsNull(s["matches"], "no query → summary only");
    }

    [TestMethod]
    public void SearchIndex_SecondCall_ReusesIndexWithoutRebuild()
    {
        Call("library_search_index", new JsonObject { ["rebuild"] = true });

        JsonObject result = Call("library_search_index",
            new JsonObject { ["field"] = "game", ["value"] = "fortress" });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.IsFalse(s["rebuilt"]!.GetValue<bool>(), "an existing index must be reused");
        Assert.AreEqual(1, s["matchCount"]!.GetValue<int>());
    }

    [TestMethod]
    public void SearchIndex_CorruptIndexFile_SelfHealsByRebuilding()
    {
        // A truncated/garbled cache must trigger a rescan, not a dead tool — the index is a
        // cache, never the source of truth. "built NOTADATE" makes DateTimeOffset.Parse throw.
        File.WriteAllText(Path.Combine(_lib, ClipMetaIndex.IndexFileName),
            "version 1\nbuilt NOTADATE\n");

        JsonObject result = Call("library_search_index",
            new JsonObject { ["field"] = "game", ["value"] = "fortress" });

        AssertOk(result);
        JsonObject s = Structured(result);
        Assert.IsTrue(s["rebuilt"]!.GetValue<bool>(), "corrupt index must self-heal");
        Assert.AreEqual(1, s["matchCount"]!.GetValue<int>());
    }

    [TestMethod]
    public void SearchIndex_StaleClipCount_TracksFilesystemDrift()
    {
        // Fresh rebuild → in sync.
        JsonObject fresh = Call("library_search_index", new JsonObject { ["rebuild"] = true });
        AssertOk(fresh);
        Assert.AreEqual(0, Structured(fresh)["staleClipCount"]!.GetValue<int>());

        // Touch one clip (newer mtime, still the library's newest so the list-ordering test
        // stays valid in any execution order) and ask again WITHOUT rebuilding.
        File.SetLastWriteTimeUtc(_noisePath, DateTime.UtcNow.AddMinutes(5));
        JsonObject stale = Call("library_search_index", new JsonObject());
        AssertOk(stale);
        Assert.AreEqual(1, Structured(stale)["staleClipCount"]!.GetValue<int>(),
            "a modified file must register as stale so the model knows to rebuild");
    }

    [TestMethod]
    public void SearchIndex_NoRootConfigured_IsRefused()
    {
        JsonObject result = Call("library_search_index", new JsonObject(), libraryRoot: null);

        AssertRefused(result, "No clips library is configured");
    }

    // ── empty library ────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void EmptyLibrary_AllDirectoryTools_ReturnEmptyResultsNotErrors()
    {
        string empty = Path.Combine(Path.GetTempPath(), "clipmeta-p2-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var responses = McpHarness.Run(empty,
                McpHarness.InitializeRequest,
                McpHarness.ToolCall(2, "library_list", new JsonObject()),
                McpHarness.ToolCall(3, "library_find", new JsonObject { ["field"] = "game", ["value"] = "x" }),
                McpHarness.ToolCall(4, "library_vocab", new JsonObject { ["field"] = "tags" }),
                McpHarness.ToolCall(5, "library_export", new JsonObject()),
                McpHarness.ToolCall(6, "library_search_index", new JsonObject { ["rebuild"] = true }));

            foreach (JsonObject response in responses.Skip(1).Cast<JsonObject>())
            {
                var result = (JsonObject)response["result"]!;
                Assert.IsNull(result["isError"],
                    $"response {response["id"]} errored: " +
                    result["content"]?[0]?["text"]?.GetValue<string>());
            }

            Assert.AreEqual(0,
                ((JsonObject)responses[1]["result"]!)["structuredContent"]!["totalMatches"]!.GetValue<int>());
            Assert.AreEqual(0,
                ((JsonObject)responses[4]["result"]!)["structuredContent"]!["clipCount"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
