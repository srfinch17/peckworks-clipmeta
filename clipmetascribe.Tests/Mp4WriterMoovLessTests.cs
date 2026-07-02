using ClipMetaCore;
using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Task B5: two convergent findings on the same missing guard. (a) Correctness reviewer: a
/// well-formed <c>ftyp</c>+<c>mdat</c> file with no <c>moov</c> (not fragmented) fell through
/// <see cref="Mp4Writer"/>'s <c>DetermineScenario</c> as <c>Create</c>, never emitted a moov, and
/// died at the internal temp-length check with the baffling message "temp file is X bytes but Y
/// were expected". (b) Nemesis: a to-EOF (<c>size=0</c>) box that is not actually last
/// (<see cref="BigEndianReader.ReadBoxHeader"/> resolves size=0 unconditionally to end-of-stream)
/// silently swallows everything after it, including a real <c>moov</c>, producing the exact same
/// moov-less parse tree and the same internal death on write. Both scenarios must now refuse
/// cleanly with a first-class error before ever reaching that internal check, and must leave the
/// original file byte-identical.
/// </summary>
[TestClass]
public class Mp4WriterMoovLessTests
{
    private const string Domain = ClipMetaSchema.Domain;

    private readonly List<string> _tempFiles = new();

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string path in _tempFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
        _tempFiles.Clear();
    }

    private string Save(MemoryStream ms)
    {
        string path = MinimalMp4Builder.SaveToTempFile(ms);
        _tempFiles.Add(path);
        return path;
    }

    // ── Scenario (a): well-formed ftyp+mdat, no moov at all ─────────────────

    [TestMethod]
    public void Set_FtypMdatNoMoov_Refused_OriginalByteIdentical()
    {
        string path = Save(MinimalMp4Builder.BuildFtypMdatNoMoov());
        byte[] before = File.ReadAllBytes(path);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{Domain}:game"] = "TF2";

        var ex = Assert.ThrowsExactly<UnsupportedFormatException>(() =>
            new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance),
            "a moov-less file must be refused with a first-class error, not die at the internal " +
            "temp-length verification check");
        StringAssert.Contains(ex.Message, "moov",
            "the refusal message must name the missing box");
        StringAssert.Contains(ex.Message, "truncat",
            "the message should mention the truncated/unfinalized-recording possibility");

        CollectionAssert.AreEqual(before, File.ReadAllBytes(path),
            "a refused write must leave the original byte-identical");
    }

    [TestMethod]
    public void ClearAll_FtypMdatNoMoov_Refused_OriginalByteIdentical()
    {
        string path = Save(MinimalMp4Builder.BuildFtypMdatNoMoov());
        byte[] before = File.ReadAllBytes(path);

        var mutation = new MetadataMutation { ClearAll = true };

        Assert.ThrowsExactly<UnsupportedFormatException>(() =>
            new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance),
            "clear-all against a moov-less file must also refuse cleanly");

        CollectionAssert.AreEqual(before, File.ReadAllBytes(path),
            "a refused clear-all must leave the original byte-identical");
    }

    // ── Scenario (b): size=0 mdat swallows a real moov that physically follows it ──

    [TestMethod]
    public void Set_MdatSizeZeroSwallowsMoov_Refused_OriginalByteIdentical()
    {
        string path = Save(MinimalMp4Builder.BuildMdatSizeZeroSwallowingMoov(Domain, "game", "old"));
        byte[] before = File.ReadAllBytes(path);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{Domain}:game"] = "new";

        var ex = Assert.ThrowsExactly<UnsupportedFormatException>(() =>
            new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance),
            "a size=0 box that swallows a following moov must be refused, not die at the internal " +
            "temp-length verification check");
        StringAssert.Contains(ex.Message, "moov",
            "the refusal message must name the missing box even though bytes that look like " +
            "moov are physically present in the file");

        CollectionAssert.AreEqual(before, File.ReadAllBytes(path),
            "a refused write must leave the original byte-identical");
    }

    [TestMethod]
    public void Read_MdatSizeZeroSwallowsMoov_ReportsNoMetadataRatherThanThrowing()
    {
        // Documents the read-side symptom the nemesis observed (false-confidence "no clipmeta
        // metadata"): out of scope to change here (reads stay lenient by design), but the parse
        // must still succeed and simply find nothing, since moov's bytes are gone from the tree.
        string path = Save(MinimalMp4Builder.BuildMdatSizeZeroSwallowingMoov(Domain, "game", "old"));

        var root = Mp4Parser.ParseFile(path);
        var fields = ClipMetaReader.GetFields(root);

        Assert.AreEqual(0, fields.Count(),
            "the swallowed moov's fields must not be visible, proving the fixture actually " +
            "swallows moov rather than merely refusing for an unrelated reason");
        Assert.IsFalse(root.Children.Any(c => c.Type == "moov"),
            "the parse tree must contain no moov box; its bytes were consumed as mdat's to-EOF payload");
    }

    // ── Sanity: a normal moov-bearing write is unaffected by this guard ─────

    [TestMethod]
    public void Set_NormalFileWithMoov_StillWorks()
    {
        string path = Save(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(Domain, "game", "old"));

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{Domain}:game"] = "new";
        new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(path);
        var fields = ClipMetaReader.GetFields(root);
        CollectionAssert.Contains(fields.ToList(), ("game", "new"),
            "a normal write against a file that has a moov box must be unaffected by this guard");
    }
}
