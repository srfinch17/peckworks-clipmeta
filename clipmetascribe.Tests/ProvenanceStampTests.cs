using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Provenance stamp (spec B §2): every write that stores a user field also stamps
/// <c>tagged_by: Peckworks ClipMeta</c>. It lives in the file (and the raw atom list) but is
/// internal — excluded from the curated user-field surfaces — and can be opted out of.
/// Clip-less: uses a synthetic writable .mp4 so no pristine corpus is needed.
/// </summary>
[TestClass]
public class ProvenanceStampTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cmprov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string MakeClip(string seedField = "game", string seedValue = "TF2")
    {
        string path = Path.Combine(_dir, "clip.mp4");
        using var ms = MinimalMp4Builder.BuildMp4WithStco(9999, ClipMetaSchema.Domain, seedField, seedValue);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static IReadOnlyList<(string Field, string Value)> RawFields(string clip) =>
        ClipMetaReader.GetFields(Mp4Parser.ParseFile(clip));

    [TestMethod]
    public void Write_WithUserField_StampsTaggedBy()
    {
        string clip = MakeClip();
        var m = new MetadataMutation();
        m.SetFields[ClipMetaSchema.AtomName("tags")] = "headshot";
        new Mp4Writer().WriteMetadata(clip, m, NullLogger.Instance);

        Assert.AreEqual(ClipMetaSchema.ProvenanceValue,
            RawFields(clip).Single(f => f.Field == ClipMetaSchema.TaggedBy).Value);
    }

    [TestMethod]
    public void Write_TaggedBy_IsExcludedFromUserFields()
    {
        string clip = MakeClip();
        var m = new MetadataMutation();
        m.SetFields[ClipMetaSchema.AtomName("tags")] = "headshot";
        new Mp4Writer().WriteMetadata(clip, m, NullLogger.Instance);

        var user = ClipMetaReader.GetUserFields(Mp4Parser.ParseFile(clip));
        Assert.IsFalse(user.Any(f => f.Field == ClipMetaSchema.TaggedBy),
            "provenance is internal — present in the file but not a curated user field");
    }

    [TestMethod]
    public void Write_StampProvenanceFalse_DoesNotStamp()
    {
        string clip = MakeClip();
        var m = new MetadataMutation { StampProvenance = false };
        m.SetFields[ClipMetaSchema.AtomName("tags")] = "headshot";
        new Mp4Writer().WriteMetadata(clip, m, NullLogger.Instance);

        Assert.IsFalse(RawFields(clip).Any(f => f.Field == ClipMetaSchema.TaggedBy));
    }

    [TestMethod]
    public void Write_CallerSuppliedTaggedBy_NotOverwritten()
    {
        string clip = MakeClip();
        var m = new MetadataMutation();
        m.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.TaggedBy)] = "Custom Tool";
        m.SetFields[ClipMetaSchema.AtomName("tags")] = "headshot";
        new Mp4Writer().WriteMetadata(clip, m, NullLogger.Instance);

        Assert.AreEqual("Custom Tool",
            RawFields(clip).Single(f => f.Field == ClipMetaSchema.TaggedBy).Value);
    }
}
