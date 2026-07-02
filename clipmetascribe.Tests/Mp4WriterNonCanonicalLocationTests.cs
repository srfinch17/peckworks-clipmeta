using ClipMetaCore;
using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Task B2: ISO 14496-12 legally permits a <c>meta</c> box directly under <c>moov</c> (no
/// <c>udta</c> wrapper), but clipmeta's writer only knows the canonical
/// <c>moov.udta.meta.ilst</c> location. Before this fix, tagging a file with metadata at
/// <c>moov.meta.ilst</c> would silently create a SECOND, canonical copy: <c>--set</c> exits 0
/// and produces two values (duplicate-key export, both match on find), <c>--clear</c> reports
/// success while the non-canonical value survives, and <c>--clear-all</c> can never fully clear
/// the file. The writer must refuse rather than risk that divergence; the reader is unaffected,
/// it already walks every <c>ilst</c> anywhere.
/// </summary>
[TestClass]
public class Mp4WriterNonCanonicalLocationTests
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

    private string SaveHostileFixture(string field = "game", string value = "old") =>
        Save(MinimalMp4Builder.BuildMp4WithNonCanonicalMoovMetaIlst(9999, Domain, field, value));

    // ── --set refuses ───────────────────────────────────────────────────────

    [TestMethod]
    public void Set_MetadataAtMoovMetaIlst_Refused_OriginalByteIdentical()
    {
        string path = SaveHostileFixture();
        byte[] before = File.ReadAllBytes(path);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{Domain}:game"] = "new";

        var ex = Assert.ThrowsExactly<UnsupportedFormatException>(() =>
            new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance),
            "a file with editable metadata outside moov.udta.meta.ilst must be refused");
        StringAssert.Contains(ex.Message, "non-canonical location",
            "the refusal message must name the reason, per the writer's refuse-don't-guess philosophy");
        StringAssert.Contains(ex.Message, "moov.udta.meta.ilst",
            "the refusal message must name the canonical location clipmeta edits");

        CollectionAssert.AreEqual(before, File.ReadAllBytes(path),
            "a refused write must leave the original byte-identical");
    }

    // ── --clear refuses ─────────────────────────────────────────────────────

    [TestMethod]
    public void Clear_MetadataAtMoovMetaIlst_Refused_OriginalByteIdentical()
    {
        string path = SaveHostileFixture();
        byte[] before = File.ReadAllBytes(path);

        var mutation = new MetadataMutation();
        mutation.DeleteFields.Add($"{Domain}:game");

        Assert.ThrowsExactly<UnsupportedFormatException>(() =>
            new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance),
            "clearing a single field must also refuse when non-canonical metadata exists");

        CollectionAssert.AreEqual(before, File.ReadAllBytes(path),
            "a refused clear must leave the original byte-identical");
    }

    // ── --clear-all refuses ─────────────────────────────────────────────────

    [TestMethod]
    public void ClearAll_MetadataAtMoovMetaIlst_Refused_OriginalByteIdentical()
    {
        string path = SaveHostileFixture();
        byte[] before = File.ReadAllBytes(path);

        var mutation = new MetadataMutation { ClearAll = true };

        Assert.ThrowsExactly<UnsupportedFormatException>(() =>
            new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance),
            "clear-all must also refuse when non-canonical metadata exists " +
            "(previously this made clear-all permanently impossible: exit 3)");

        CollectionAssert.AreEqual(before, File.ReadAllBytes(path),
            "a refused clear-all must leave the original byte-identical");
    }

    // ── Read paths (--list) are unaffected ──────────────────────────────────

    [TestMethod]
    public void Read_MetadataAtMoovMetaIlst_StillWorks()
    {
        // The reader walks every ilst anywhere by design; this guard is a WRITE-time refusal
        // only, read behavior must not change.
        string path = SaveHostileFixture("game", "old");

        var root = Mp4Parser.ParseFile(path);
        var fields = ClipMetaReader.GetFields(root);

        CollectionAssert.Contains(fields.ToList(), ("game", "old"),
            "reading a file with metadata at moov.meta.ilst must still surface the field");
    }

    // ── The other ISO-legal non-canonical location also refuses ─────────────

    [TestMethod]
    public void Set_MetadataAtTrakUdtaMetaIlst_Refused_OriginalByteIdentical()
    {
        // ISO 14496-12 also legally permits udta directly under trak, a second non-canonical
        // location distinct from the moov-level meta case.
        string path = Save(MinimalMp4Builder.BuildMp4WithNonCanonicalTrakUdtaMetaIlst(
            9999, Domain, "game", "old"));
        byte[] before = File.ReadAllBytes(path);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{Domain}:game"] = "new";

        Assert.ThrowsExactly<UnsupportedFormatException>(() =>
            new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance),
            "metadata at trak.udta.meta.ilst must also be refused");

        CollectionAssert.AreEqual(before, File.ReadAllBytes(path),
            "a refused write must leave the original byte-identical");
    }

    // ── Canonical location is unaffected (no false positives) ───────────────

    [TestMethod]
    public void Set_MetadataAtCanonicalLocation_StillWorks()
    {
        string path = Save(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(Domain, "game", "old"));

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{Domain}:game"] = "new";
        new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(path);
        var fields = ClipMetaReader.GetFields(root);
        CollectionAssert.Contains(fields.ToList(), ("game", "new"),
            "a normal write at the canonical location must be unaffected by this guard");
    }
}
