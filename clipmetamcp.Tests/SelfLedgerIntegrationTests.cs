using System.Text.Json.Nodes;
using ClipMetaCore.Watching;
using ClipMetaMcp.Tests.Helpers;

namespace ClipMetaMcp.Tests;

/// <summary>
/// Integration tests verifying that a shared <see cref="SelfActionLedger"/> between write tools
/// and <c>library_watching</c> causes clips ClipMeta just tagged to be excluded from gaming-mode
/// (<c>recent_write</c>) detection — they are self-writes, not fresh user game-saves.
///
/// These tests require a real .mp4 clip; they skip gracefully on CI where <c>testclips/pristine</c>
/// is absent (the same graceful-skip pattern used by <see cref="WriteToolsTests"/>).
/// </summary>
[TestClass]
public class SelfLedgerIntegrationTests
{
    private string _lib = null!;

    [TestInitialize]
    public void SetUp()
    {
        _lib = Path.Combine(Path.GetTempPath(), "clipmeta-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_lib);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_lib))
            Directory.Delete(_lib, recursive: true);
    }

    /// <summary>Copies the smallest pristine clip into the test library and returns its path.</summary>
    private string PrepareClip(string fileName = "clip.mp4")
    {
        string dest = Path.Combine(_lib, fileName);
        File.Copy(TestClipsLocator.SmallestPristine(), dest);
        return dest;
    }

    /// <summary>
    /// A clip ClipMeta just wrote (via clip_set_fields) must NOT surface as a
    /// <c>recent_write</c> candidate in <c>library_watching</c>. Without the
    /// <see cref="SelfActionLedger"/> fix the clip has a fresh creation time, is absent from the
    /// empty-library baseline, and passes every <c>RecentWriteSignal</c> condition — so it
    /// would (incorrectly) appear as a high-confidence gaming-mode live target.
    ///
    /// The SAME <see cref="SelfActionLedger"/> instance is passed to both sessions here,
    /// mirroring how <c>Program.cs</c> builds one process-wide ledger shared by both
    /// <c>WriteTools.RegisterAll</c> and <c>ReadTools.RegisterAll</c>.
    /// </summary>
    [TestMethod]
    public void Watching_DoesNotSurface_AClipThisSessionWrote_AsRecentWrite()
    {
        // One shared ledger — exactly as wired in Program.cs.
        var ledger = new SelfActionLedger();
        string clip = PrepareClip();

        // Write a tag via clip_set_fields. The ledger is wired into this session so a successful
        // write calls ledger.MarkWritten(clip).
        var writeResponses = McpHarness.RunWithLedger(_lib, ledger,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, "clip_set_fields", new JsonObject
            {
                ["path"] = clip,
                ["fields"] = new JsonObject { ["game"] = "TF2" },
                ["backup"] = false,
            }));
        var writeResult = (JsonObject)writeResponses[1]["result"]!;
        Assert.IsNull(writeResult["isError"], "clip_set_fields must succeed before the watching assertion");

        // Call library_watching with the SAME ledger. The clip has a fresh creation time and is
        // not in the baseline index (empty temp library), so without the ledger it would surface
        // as recent_write. With MarkWritten in the ledger, RecentWriteSignal must exclude it.
        var watchResponses = McpHarness.RunWithLedger(_lib, ledger,
            McpHarness.InitializeRequest,
            McpHarness.ToolCall(2, "library_watching", new JsonObject
            {
                ["include_access_fallback"] = true,
            }));
        var watchResult = (JsonObject)watchResponses[1]["result"]!;
        Assert.IsNull(watchResult["isError"], "library_watching must succeed");

        var structured = (JsonObject)watchResult["structuredContent"]!;
        var candidates = (JsonArray)structured["candidates"]!;

        bool selfWrittenSurfacedAsRecentWrite = candidates
            .Cast<JsonObject>()
            .Any(c =>
                c["source"]?.GetValue<string>() == "recent_write" &&
                string.Equals(c["path"]?.GetValue<string>(), clip, StringComparison.OrdinalIgnoreCase));

        Assert.IsFalse(selfWrittenSurfacedAsRecentWrite,
            "A clip ClipMeta just tagged must not appear as a recent_write live target; " +
            "the SelfActionLedger must exclude it from gaming-mode detection.");
    }
}
