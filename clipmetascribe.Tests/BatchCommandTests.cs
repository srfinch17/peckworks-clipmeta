using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Tests for <see cref="BatchCommand"/> — the iterate + isolate + report engine behind directory
/// write commands. The skip/isolation/summary behavior is proven clip-less; correctness over real
/// clips (fields land, media survives) is proven with the pristine corpus and graceful-skips.
/// </summary>
[TestClass]
public class BatchCommandTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Clean()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MetadataMutation SetGame(string value)
    {
        var m = new MetadataMutation();
        m.SetFields[ClipMetaSchema.AtomName("game")] = value;
        return m;
    }

    private static Dictionary<string, string> Fields(string path) =>
        ClipMetaReader.GetUserFields(Mp4Parser.ParseFile(path))
                      .ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);

    // ── Clip-less: skip / isolation / summary ─────────────────────────────────

    [TestMethod]
    public void Run_AllMutationsNull_AllSkipped_ReturnsZero()
    {
        var files = new[] { "a.mp4", "b.mp4", "c.mp4" }.Select(n => Path.Combine(_dir, n)).ToArray();
        foreach (var f in files) File.WriteAllBytes(f, new byte[] { 0 });
        var sw = new StringWriter();

        int code = BatchCommand.Run(files, _ => null, NullLogger.Instance, sw);

        Assert.AreEqual(0, code);
        StringAssert.Contains(sw.ToString(), "0 updated, 0 failed, 3 skipped");
    }

    [TestMethod]
    public void Run_WritesFail_IsolatedAndCounted_ReturnsTwo()
    {
        // Nonexistent .mp4 paths → each WriteMetadata throws a file-not-found (IOException family).
        // The batch must isolate each per-file, not abort, and report the failures.
        var files = new[] { Path.Combine(_dir, "x.mp4"), Path.Combine(_dir, "y.mp4") };
        var sw = new StringWriter();

        int code = BatchCommand.Run(files, _ => SetGame("TF2"), NullLogger.Instance, sw);

        Assert.AreEqual(2, code);
        StringAssert.Contains(sw.ToString(), "0 updated, 2 failed");
    }

    [TestMethod]
    public void Run_DryRun_ReportsWouldUpdate_NotCompletion()
    {
        // Dry-run mutations short-circuit in Mp4Writer before opening the file, so each "succeeds"
        // without writing. The summary must say nothing was modified — not claim N were updated.
        var files = new[] { Path.Combine(_dir, "a.mp4"), Path.Combine(_dir, "b.mp4") };
        var sw = new StringWriter();

        int code = BatchCommand.Run(files,
            _ => { var m = SetGame("X"); m.DryRun = true; return m; },
            NullLogger.Instance, sw, dryRun: true);

        Assert.AreEqual(0, code);
        string output = sw.ToString();
        StringAssert.Contains(output, "dry-run");
        StringAssert.Contains(output, "No files modified");
        Assert.IsFalse(output.Contains("Batch complete"), "dry-run must not claim writes completed");
    }

    // ── Integration over real clips ───────────────────────────────────────────

    [TestMethod]
    public void Run_OverRealClips_AllUpdated_MediaByteIdentical()
    {
        if (!TestClipsLocator.PristineClipsPresent())
            Assert.Inconclusive("no pristine clips — batch integration skipped (CI runs clip-less).");

        var map = new Dictionary<string, string>();   // dest → pristine source (for integrity)
        foreach (var p in TestClipsLocator.AllPristine().OrderBy(p => new FileInfo(p).Length).Take(2))
        {
            string dest = Path.Combine(_dir, Path.GetFileName(p));
            File.Copy(p, dest);
            map[dest] = p;
        }
        var sw = new StringWriter();

        int code = BatchCommand.Run(map.Keys.ToList(), _ => SetGame("Team Fortress 2"), NullLogger.Instance, sw);

        Assert.AreEqual(0, code);
        foreach (var (dest, src) in map)
        {
            Assert.AreEqual("Team Fortress 2", Fields(dest)["game"], $"{dest} was not tagged");
            MediaIntegrityScanner.AssertMediaUnchanged(src, dest);
        }
    }

    [TestMethod]
    public void Run_CorruptClipMixedIn_IsolatedReturnsTwo()
    {
        if (!TestClipsLocator.PristineClipsPresent())
            Assert.Inconclusive("no pristine clips — batch integration skipped (CI runs clip-less).");

        string good = TestClipsLocator.AllPristine().OrderBy(p => new FileInfo(p).Length).First();
        string goodDest = Path.Combine(_dir, "good.mp4");
        File.Copy(good, goodDest);
        File.WriteAllBytes(Path.Combine(_dir, "corrupt.mp4"), new byte[] { 1, 2, 3, 4, 5 });
        var sw = new StringWriter();

        int code = BatchCommand.Run(
            new[] { goodDest, Path.Combine(_dir, "corrupt.mp4") }, _ => SetGame("X"), NullLogger.Instance, sw);

        Assert.AreEqual(2, code);
        StringAssert.Contains(sw.ToString(), "1 updated, 1 failed");
        Assert.AreEqual("X", Fields(goodDest)["game"], "the good clip must still be tagged despite the corrupt one");
    }
}
