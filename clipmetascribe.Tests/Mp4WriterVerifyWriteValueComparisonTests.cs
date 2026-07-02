using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Task B3: <c>VerifyWrite</c>'s comment claims "every field this mutation stored must read
/// back," but the old implementation only checked <c>FindEditableNode(root, key) != null</c>,
/// a whole-tree existence search. It never compared the read-back value against what the
/// mutation actually asked to be written, and it never constrained the search to the
/// canonical <c>moov.udta.meta.ilst</c> location the writer edits. A value-corrupting bug in
/// <see cref="FreeformAtomWriter"/> or <see cref="Normalizer"/>, or a stale atom sitting
/// anywhere else in the tree with a matching key, would verify clean.
/// </summary>
/// <remarks>
/// These tests call <see cref="Mp4Writer.VerifyWrite"/> directly against hand-built
/// <see cref="BoxNode"/> trees rather than going through the public write API. Task B2
/// (<c>DetectNonCanonicalMetadata</c>) already refuses, pre-write, any file carrying a
/// clipmeta-namespaced atom outside the canonical path, so a wrong-value or wrong-location
/// tree can no longer reach <c>VerifyWrite</c> via a real write; these are unit tests of
/// <c>VerifyWrite</c>'s own logic, exercising it as the defense-in-depth layer it is.
/// </remarks>
[TestClass]
public class Mp4WriterVerifyWriteValueComparisonTests
{
    private const string Domain = ClipMetaSchema.Domain;
    private const string GameKey = Domain + ":game";

    // ── Tree builders ────────────────────────────────────────────────────────

    /// <summary>A tree with one mdat box and (optionally) a canonical moov.udta.meta.ilst
    /// containing a single freeform "----" atom whose EditableKey/DisplayValue are given.</summary>
    private static BoxNode BuildTree(string? canonicalKey, string? canonicalQuotedValue,
        BoxNode? nonCanonicalIlstAtom = null)
    {
        var ilstChildren = new List<BoxNode>();
        if (canonicalKey != null)
        {
            ilstChildren.Add(new BoxNode
            {
                Type = "----",
                EditableKey = canonicalKey,
                IsEditable = true,
                DisplayValue = canonicalQuotedValue,
            });
        }

        var ilst = new BoxNode { Type = "ilst", Children = ilstChildren };
        var meta = new BoxNode { Type = "meta", Children = new List<BoxNode> { ilst } };
        var udta = new BoxNode { Type = "udta", Children = new List<BoxNode> { meta } };

        var moovChildren = new List<BoxNode> { udta };
        var moov = new BoxNode { Type = "moov", Children = moovChildren };

        var rootChildren = new List<BoxNode> { moov, new BoxNode { Type = "mdat" } };
        if (nonCanonicalIlstAtom != null)
        {
            // A second ilst directly under a moov-level meta box (no udta wrapper), the
            // non-canonical shape Task B2 refuses on the SOURCE file, used here purely to
            // prove VerifyWrite itself no longer trusts a match found there.
            var strayIlst = new BoxNode { Type = "ilst", Children = new List<BoxNode> { nonCanonicalIlstAtom } };
            var strayMeta = new BoxNode { Type = "meta", Children = new List<BoxNode> { strayIlst } };
            moovChildren.Add(strayMeta);
        }

        return new BoxNode { Type = "root", Children = rootChildren };
    }

    private static BoxNode BuildOriginalRootWithOneMdat() =>
        new() { Type = "root", Children = new List<BoxNode> { new BoxNode { Type = "mdat" } } };

    // ── Wrong value at the canonical path ───────────────────────────────────

    [TestMethod]
    public void SetField_WrongValueAtCanonicalPath_Throws()
    {
        // The atom exists at moov.udta.meta.ilst under the right key, but its stored value
        // does not match what the mutation asked to be written, exactly finding B2's proof
        // point: an existence-only check would call this clean.
        var written = BuildTree(GameKey, "\"wrong-value\"");
        var mutation = new MetadataMutation();
        mutation.SetFields[GameKey] = "expected-value";

        var ex = Assert.ThrowsExactly<InvalidDataException>(() =>
            Mp4Writer.VerifyWrite(written, BuildOriginalRootWithOneMdat(), mutation, "clip.mp4"),
            "a read-back value that does not match what was set must fail verification");
        StringAssert.Contains(ex.Message, "expected-value",
            "the failure message should name the expected value");
        StringAssert.Contains(ex.Message, "wrong-value",
            "the failure message should name the actual (wrong) value read back");
    }

    [TestMethod]
    public void SetField_CorrectValueAtCanonicalPath_DoesNotThrow()
    {
        // Positive control: a correctly-written value at the canonical path passes.
        var written = BuildTree(GameKey, "\"expected-value\"");
        var mutation = new MetadataMutation();
        mutation.SetFields[GameKey] = "expected-value";

        Mp4Writer.VerifyWrite(written, BuildOriginalRootWithOneMdat(), mutation, "clip.mp4");
    }

    // ── Matching atom sits outside the canonical path ───────────────────────

    [TestMethod]
    public void SetField_OnlyMatchAtNonCanonicalPath_Throws()
    {
        // No atom at all under moov.udta.meta.ilst, but a same-key, same-value atom sits at
        // a non-canonical moov.meta.ilst (ISO-legal, but not where this writer edits). The old
        // whole-tree FindEditableNode search would find it and call the write verified; the
        // fix must look ONLY under the canonical ilst and therefore report it missing.
        var strayAtom = new BoxNode
        {
            Type = "----",
            EditableKey = GameKey,
            IsEditable = true,
            DisplayValue = "\"expected-value\"",
        };
        var written = BuildTree(canonicalKey: null, canonicalQuotedValue: null, nonCanonicalIlstAtom: strayAtom);
        var mutation = new MetadataMutation();
        mutation.SetFields[GameKey] = "expected-value";

        var ex = Assert.ThrowsExactly<InvalidDataException>(() =>
            Mp4Writer.VerifyWrite(written, BuildOriginalRootWithOneMdat(), mutation, "clip.mp4"),
            "a matching atom outside moov.udta.meta.ilst must not satisfy verification");
        StringAssert.Contains(ex.Message, GameKey,
            "the failure message should name the missing key");
    }

    // ── Missing atom altogether (existing behavior, preserved) ─────────────

    [TestMethod]
    public void SetField_AtomAbsentEverywhere_Throws()
    {
        var written = BuildTree(canonicalKey: null, canonicalQuotedValue: null);
        var mutation = new MetadataMutation();
        mutation.SetFields[GameKey] = "expected-value";

        Assert.ThrowsExactly<InvalidDataException>(() =>
            Mp4Writer.VerifyWrite(written, BuildOriginalRootWithOneMdat(), mutation, "clip.mp4"));
    }

    // ── ClearAll leftover check, scoped to the canonical path ───────────────

    [TestMethod]
    public void ClearAll_LeftoverAtCanonicalPath_Throws()
    {
        // A clipmeta atom survives under the canonical ilst despite ClearAll, must still be
        // caught (this is the pre-existing behavior, must not regress).
        var written = BuildTree(GameKey, "\"still-here\"");
        var mutation = new MetadataMutation { ClearAll = true };

        var ex = Assert.ThrowsExactly<InvalidDataException>(() =>
            Mp4Writer.VerifyWrite(written, BuildOriginalRootWithOneMdat(), mutation, "clip.mp4"));
        StringAssert.Contains(ex.Message, GameKey);
    }

    [TestMethod]
    public void ClearAll_NoLeftoverAtCanonicalPath_DoesNotThrow()
    {
        var written = BuildTree(canonicalKey: null, canonicalQuotedValue: null);
        var mutation = new MetadataMutation { ClearAll = true };

        Mp4Writer.VerifyWrite(written, BuildOriginalRootWithOneMdat(), mutation, "clip.mp4");
    }

    // ── Explicit field delete, scoped to the canonical path ─────────────────

    [TestMethod]
    public void DeleteField_StillPresentAtCanonicalPath_Throws()
    {
        // A field the mutation asked to delete is still readable back from the canonical ilst,
        // an existence-only / whole-tree check has no way to catch this at all today.
        var written = BuildTree(GameKey, "\"should-have-been-deleted\"");
        var mutation = new MetadataMutation();
        mutation.DeleteFields.Add(GameKey);

        var ex = Assert.ThrowsExactly<InvalidDataException>(() =>
            Mp4Writer.VerifyWrite(written, BuildOriginalRootWithOneMdat(), mutation, "clip.mp4"));
        StringAssert.Contains(ex.Message, GameKey);
    }

    [TestMethod]
    public void DeleteField_TrulyGone_DoesNotThrow()
    {
        var written = BuildTree(canonicalKey: null, canonicalQuotedValue: null);
        var mutation = new MetadataMutation();
        mutation.DeleteFields.Add(GameKey);

        Mp4Writer.VerifyWrite(written, BuildOriginalRootWithOneMdat(), mutation, "clip.mp4");
    }
}
