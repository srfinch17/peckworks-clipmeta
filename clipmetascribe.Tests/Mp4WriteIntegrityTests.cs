using ClipMetaCore;
using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Media-integrity tests for the write engine. These exist because "the metadata reads back"
/// and "the file still parses" — what the original tests checked — are NOT proof that the
/// video survived: a file with every chunk offset corrupted still parses and still returns
/// its metadata. Every test here instead proves one of two things:
/// <list type="number">
///   <item>After a successful write, the media is byte-identical and every chunk offset still
///       points at the same data (<see cref="MediaIntegrityScanner.AssertMediaUnchanged"/>).</item>
///   <item>When a file CANNOT be rewritten safely, the writer refuses up front, leaves the
///       original untouched, and cleans up its temp file.</item>
/// </list>
/// The moov-first fixtures matter most: the real test clips that happen to be on hand are all
/// mdat-first (ftyp, mdat, moov — a layout many screen recorders produce), where chunk offsets
/// never need adjusting. But layout is a per-file fact, NOT a property of the source — clips
/// from any tool may be either layout — so the moov-first path (which forces the writer to
/// patch every stco entry, the single most corruption-prone operation in the codebase) is
/// exercised here by synthetic moov-first fixtures rather than left to chance on real clips.
/// </summary>
[TestClass]
public class Mp4WriteIntegrityTests
{
    private const string Domain = ClipMetaSchema.Domain;

    private readonly List<string> _tempFiles = new();

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string path in _tempFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
            try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { /* best effort */ }
        }
        _tempFiles.Clear();
    }

    /// <summary>Saves the stream to a tracked temp file plus a pristine "before" copy, so a
    /// test can mutate one and compare against the other.</summary>
    private (string original, string working) SaveWithBackup(MemoryStream ms)
    {
        string working = MinimalMp4Builder.SaveToTempFile(ms);
        string original = working + ".orig.mp4";
        File.Copy(working, original);
        _tempFiles.Add(working);
        _tempFiles.Add(original);
        return (original, working);
    }

    // ── Moov-first offset adjustment: the dangerous path, proven byte-for-byte ──

    [TestMethod]
    public void MoovFirst_CreateScenario_Grow_AllChunkOffsetsPointAtSameData()
    {
        // No seed metadata → the write must synthesize udta/meta/hdlr/ilst, the largest
        // possible moov growth, shifting mdat the furthest.
        var (original, working) = SaveWithBackup(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat());

        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Notes)] = "moov must grow a lot here";
        new Mp4Writer().WriteMetadata(working, mutation, NullLogger.Instance);

        // Both tracks' stco tables must now point at the same marker bytes as before the write.
        MediaIntegrityScanner.AssertMediaUnchanged(original, working);
    }

    [TestMethod]
    public void MoovFirst_UpdateScenario_GrowAndShrink_AllChunkOffsetsPointAtSameData()
    {
        // Seeded with one atom so the write takes the Update path (rewrite existing ilst).
        var (original, working) = SaveWithBackup(
            MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(Domain, "tags", "short"));

        // Grow: replace the value with a much longer one (positive delta).
        var grow = new MetadataMutation();
        grow.SetFields[$"{Domain}:tags"] = "a substantially longer value than before|second|third";
        new Mp4Writer().WriteMetadata(working, grow, NullLogger.Instance);
        MediaIntegrityScanner.AssertMediaUnchanged(original, working);

        // Shrink: delete the field (negative delta — offsets must shift backwards correctly).
        string afterGrow = working + ".grown.mp4";
        File.Copy(working, afterGrow);
        _tempFiles.Add(afterGrow);

        var shrink = new MetadataMutation();
        shrink.DeleteFields.Add($"{Domain}:tags");
        new Mp4Writer().WriteMetadata(working, shrink, NullLogger.Instance);
        MediaIntegrityScanner.AssertMediaUnchanged(afterGrow, working);

        // And transitively: after grow-then-shrink the media still matches the very original.
        MediaIntegrityScanner.AssertMediaUnchanged(original, working);
    }

    // ── Refusal cases: damaged files must be rejected, not quietly truncated ────

    [TestMethod]
    public void CorruptBoxBetweenMoovAndMdat_WriteRefused_OriginalUntouched()
    {
        // Reproduces the bug found in the 2026-06 audit: an unparseable box between moov and
        // mdat made the parser stop early, and the writer then emitted a file WITHOUT the mdat
        // — the entire video silently deleted, exit code 0. The writer must now refuse.
        using var ms = MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(Domain, "tags", "v");
        byte[] clean = ms.ToArray();

        // Splice 8 junk bytes between moov and mdat. The junk claims to be a box of size 5,
        // which is impossible (smaller than the 8-byte header) — exactly the kind of damage
        // that made the parser give up mid-file.
        int moovLength = (clean[0] << 24) | (clean[1] << 16) | (clean[2] << 8) | clean[3];
        byte[] junk = { 0, 0, 0, 5, (byte)'j', (byte)'u', (byte)'n', (byte)'k' };
        byte[] damaged = clean[..moovLength].Concat(junk).Concat(clean[moovLength..]).ToArray();

        string working = Path.ChangeExtension(Path.GetTempFileName(), ".mp4");
        File.WriteAllBytes(working, damaged);
        _tempFiles.Add(working);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{Domain}:game"] = "TF2";

        Assert.ThrowsExactly<UnsupportedFormatException>(() =>
            new Mp4Writer().WriteMetadata(working, mutation, NullLogger.Instance),
            "write into a file with unparseable bytes must be refused");

        // The refusal must be clean: original bytes untouched, no temp file left behind.
        CollectionAssert.AreEqual(damaged, File.ReadAllBytes(working),
            "refused write must not modify the original file");
        Assert.IsFalse(File.Exists(working + ".tmp"), "refused write must clean up its temp file");
    }

    [TestMethod]
    public void TrailingUnparseableBytes_WriteRefused()
    {
        // Garbage after the last real box. Harmless to a player, but the rewritten file would
        // silently lose it — and we promised to never write a file we can't fully account for.
        using var ms = MinimalMp4Builder.BuildMoovFirstWithPatternedMdat();
        byte[] withTail = ms.ToArray().Concat(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x99 }).ToArray();

        string working = Path.ChangeExtension(Path.GetTempFileName(), ".mp4");
        File.WriteAllBytes(working, withTail);
        _tempFiles.Add(working);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{Domain}:game"] = "TF2";

        Assert.ThrowsExactly<UnsupportedFormatException>(() =>
            new Mp4Writer().WriteMetadata(working, mutation, NullLogger.Instance));
    }

    [TestMethod]
    public void TruncatedFile_ClampedBox_WriteRefused()
    {
        // Chop bytes off the end so mdat's size field claims more than the file holds —
        // what a torn download or interrupted copy looks like. The box header on disk is
        // lying about its length; rewriting would reproduce the lie. Refuse.
        using var ms = MinimalMp4Builder.BuildMoovFirstWithPatternedMdat();
        byte[] full = ms.ToArray();
        byte[] truncated = full[..(full.Length - 40)];

        string working = Path.ChangeExtension(Path.GetTempFileName(), ".mp4");
        File.WriteAllBytes(working, truncated);
        _tempFiles.Add(working);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{Domain}:game"] = "TF2";

        Assert.ThrowsExactly<UnsupportedFormatException>(() =>
            new Mp4Writer().WriteMetadata(working, mutation, NullLogger.Instance));
    }

    // ── clear-all and schema-stamp semantics ────────────────────────────────────

    [TestMethod]
    public void ClearAll_RemovesEveryClipmetaAtom_IncludingSchema()
    {
        // Audit bug #2: --clear-all used to re-stamp the schema atom it had just removed,
        // because the stamp ran unconditionally on every write. "Remove ALL clipmeta
        // metadata" must leave zero clipmeta atoms — schema included.
        var (_, working) = SaveWithBackup(
            MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(Domain, "tags", "x"));

        // A normal write first, so the file genuinely contains a schema atom to clear.
        var set = new MetadataMutation();
        set.SetFields[$"{Domain}:game"] = "TF2";
        new Mp4Writer().WriteMetadata(working, set, NullLogger.Instance);
        Assert.IsNotNull(FindClipmetaAtom(working, ClipMetaSchema.Schema),
            "precondition: schema atom should exist after a normal write");

        var clearAll = new MetadataMutation { ClearAll = true };
        new Mp4Writer().WriteMetadata(working, clearAll, NullLogger.Instance);

        var leftover = FindAnyClipmetaAtom(working);
        Assert.IsNull(leftover,
            $"clear-all must remove every clipmeta atom but left '{leftover?.EditableKey}'");
    }

    [TestMethod]
    public void DeleteOnlyMutation_DoesNotAddSchemaAtom()
    {
        // Deleting a field must not sneak new atoms into the file. (Previously a delete-only
        // write would ADD a schema atom to a file that never had one.)
        var (_, working) = SaveWithBackup(
            MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(Domain, "tags", "x"));

        var deleteOnly = new MetadataMutation();
        deleteOnly.DeleteFields.Add($"{Domain}:tags");
        new Mp4Writer().WriteMetadata(working, deleteOnly, NullLogger.Instance);

        Assert.IsNull(FindClipmetaAtom(working, "tags"), "deleted field must be gone");
        Assert.IsNull(FindClipmetaAtom(working, ClipMetaSchema.Schema),
            "a delete-only write must not add a schema atom");
    }

    [TestMethod]
    public void SetWrite_StillStampsSchemaAtom()
    {
        // The flip side: writes that DO store values must keep stamping the schema version
        // (it is what enables future format migrations).
        var (_, working) = SaveWithBackup(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat());

        var set = new MetadataMutation();
        set.SetFields[$"{Domain}:game"] = "TF2";
        new Mp4Writer().WriteMetadata(working, set, NullLogger.Instance);

        Assert.IsNotNull(FindClipmetaAtom(working, ClipMetaSchema.Schema),
            "a value-storing write must stamp the schema atom");
    }

    // ── Empty-container removal: clearing returns a file to pristine ────────────

    [TestMethod]
    public void WriteThenClearAll_ReturnsFileToBytePristine()
    {
        // The whole point of the empty-chain removal (field-discovered 2026-06-12): a clip that
        // was bare, got tagged, then fully cleared must be byte-for-byte what it started as —
        // no ~80-byte schema/container husk left behind. moov-first fixture, so the mdat offsets
        // also have to round-trip exactly (grow on write, shrink back on clear).
        var (original, working) = SaveWithBackup(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat());

        var set = new MetadataMutation();
        set.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2";
        set.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "a|b|c";
        new Mp4Writer().WriteMetadata(working, set, NullLogger.Instance);
        Assert.AreNotEqual(new FileInfo(original).Length, new FileInfo(working).Length,
            "precondition: tagging must have grown the file");

        new Mp4Writer().WriteMetadata(working, new MetadataMutation { ClearAll = true }, NullLogger.Instance);

        CollectionAssert.AreEqual(File.ReadAllBytes(original), File.ReadAllBytes(working),
            "a tag-then-clear round-trip must leave the file byte-identical to its original");
    }

    [TestMethod]
    public void ClearAll_RemovesEmptyContainerChain_NoUdtaLeftBehind()
    {
        // After clearing the only metadata, the udta→meta→hdlr→ilst chain that held it is
        // useless — it must be gone, not left as an empty husk.
        var (_, working) = SaveWithBackup(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat());

        var set = new MetadataMutation();
        set.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";
        new Mp4Writer().WriteMetadata(working, set, NullLogger.Instance);
        Assert.IsNotNull(FindNode(Mp4Parser.ParseFile(working), n => n.Type == "udta"),
            "precondition: tagging a chain-less fixture created a udta");

        new Mp4Writer().WriteMetadata(working, new MetadataMutation { ClearAll = true }, NullLogger.Instance);

        Assert.IsNull(FindNode(Mp4Parser.ParseFile(working), n => n.Type == "udta"),
            "clearing the last metadata must drop the empty udta chain, not leave a husk");
    }

    [TestMethod]
    public void ClearLastFieldByDelete_DropsOrphanedSchemaAndChain()
    {
        // Reproduces the exact field report: clip_clear_fields (a delete-only mutation, NOT
        // clear-all) that removes the last user field must also take the orphaned schema stamp
        // and the now-empty chain — otherwise the schema atom lingers invisibly.
        var (original, working) = SaveWithBackup(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat());

        var set = new MetadataMutation();
        set.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";
        new Mp4Writer().WriteMetadata(working, set, NullLogger.Instance);

        var delete = new MetadataMutation();
        delete.DeleteFields.Add(ClipMetaSchema.AtomName(ClipMetaSchema.Game));
        new Mp4Writer().WriteMetadata(working, delete, NullLogger.Instance);

        Assert.IsNull(FindAnyClipmetaAtom(working),
            "deleting the last user field must also remove the orphaned schema stamp");
        CollectionAssert.AreEqual(File.ReadAllBytes(original), File.ReadAllBytes(working),
            "delete-of-last-field must return the file to pristine too");
    }

    [TestMethod]
    public void PartialClear_KeepsSurvivingFieldAndSchemaAndChain()
    {
        // The flip side: clearing ONE of several fields must keep the others, the schema stamp,
        // and the container. Only an emptied-out ilst triggers removal.
        var (_, working) = SaveWithBackup(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat());

        var set = new MetadataMutation();
        set.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";
        set.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "a|b";
        new Mp4Writer().WriteMetadata(working, set, NullLogger.Instance);

        var delete = new MetadataMutation();
        delete.DeleteFields.Add(ClipMetaSchema.AtomName(ClipMetaSchema.Tags));
        new Mp4Writer().WriteMetadata(working, delete, NullLogger.Instance);

        Assert.IsNotNull(FindClipmetaAtom(working, ClipMetaSchema.Game), "surviving field must remain");
        Assert.IsNull(FindClipmetaAtom(working, ClipMetaSchema.Tags), "cleared field must be gone");
        Assert.IsNotNull(FindClipmetaAtom(working, ClipMetaSchema.Schema),
            "the schema stamp must stay while any user field remains");
    }

    [TestMethod]
    public void ClearAll_WithForeignAtomPresent_KeepsChainAndForeignAtom()
    {
        // Spec hazard #5, via the removal path: a non-clipmeta freeform atom (an iTunes-style
        // foreign atom) keeps the container alive — clear-all strips OUR atoms only and must
        // never drop a chain that still holds someone else's data.
        const string foreignDomain = "com.apple.iTunes";
        var (_, working) = SaveWithBackup(
            MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(foreignDomain, "rating", "5"));

        var set = new MetadataMutation();
        set.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "TF2";
        new Mp4Writer().WriteMetadata(working, set, NullLogger.Instance);

        new Mp4Writer().WriteMetadata(working, new MetadataMutation { ClearAll = true }, NullLogger.Instance);

        var root = Mp4Parser.ParseFile(working);
        Assert.IsNull(FindAnyClipmetaAtom(working), "clear-all must remove every clipmeta atom");
        Assert.IsNotNull(FindNode(root, n => n.EditableKey == $"{foreignDomain}:rating"),
            "a foreign atom must survive clear-all");
        Assert.IsNotNull(FindNode(root, n => n.Type == "udta"),
            "the container must NOT be dropped while a foreign atom still needs it");
    }

    // ── Real clips: every pristine clip survives a write byte-for-byte ──────────

    public static IEnumerable<object[]> PristineClips()
        => TestClipsLocator.AllPristine().Select(p => new object[] { p });

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void RealClip_MultiFieldWrite_MediaByteIdentical(string pristinePath)
    {
        string scratch = ScratchClips.Prepare(pristinePath);
        _tempFiles.Add(scratch);

        var mutation = new MetadataMutation();
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "rocket jump|headshot";
        mutation.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Rating)] = "4";
        new Mp4Writer().WriteMetadata(scratch, mutation, NullLogger.Instance);

        // SHA-256 of every mdat payload plus a byte-compare at every chunk offset.
        MediaIntegrityScanner.AssertMediaUnchanged(pristinePath, scratch);
    }

    [TestMethod]
    public void RealClip_FullLifecycle_MediaByteIdenticalAtEveryStage()
    {
        // The whole user journey on one real clip (the smallest, to keep the suite fast):
        // tag → append → clear one field → clear-all. The media must be byte-identical to the
        // pristine original after EVERY stage, not just at the end.
        string pristine = TestClipsLocator.AllPristine()
            .OrderBy(p => new FileInfo(p).Length)
            .First();
        string scratch = ScratchClips.Prepare(pristine);
        _tempFiles.Add(scratch);
        var writer = new Mp4Writer();

        var set = new MetadataMutation();
        set.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Game)] = "Team Fortress 2";
        set.SetFields[ClipMetaSchema.AtomName(ClipMetaSchema.Notes)] = "lifecycle stage 1";
        writer.WriteMetadata(scratch, set, NullLogger.Instance);
        MediaIntegrityScanner.AssertMediaUnchanged(pristine, scratch);

        var append = new MetadataMutation();
        append.AppendFields[ClipMetaSchema.AtomName(ClipMetaSchema.Tags)] = "market garden";
        writer.WriteMetadata(scratch, append, NullLogger.Instance);
        MediaIntegrityScanner.AssertMediaUnchanged(pristine, scratch);

        var clear = new MetadataMutation();
        clear.DeleteFields.Add(ClipMetaSchema.AtomName(ClipMetaSchema.Notes));
        writer.WriteMetadata(scratch, clear, NullLogger.Instance);
        MediaIntegrityScanner.AssertMediaUnchanged(pristine, scratch);

        var clearAll = new MetadataMutation { ClearAll = true };
        writer.WriteMetadata(scratch, clearAll, NullLogger.Instance);
        MediaIntegrityScanner.AssertMediaUnchanged(pristine, scratch);

        Assert.IsNull(FindAnyClipmetaAtom(scratch), "clear-all must leave no clipmeta atoms");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Parses the file and returns the clipmeta atom for <paramref name="field"/>, or null.</summary>
    private static BoxNode? FindClipmetaAtom(string path, string field)
    {
        var root = Mp4Parser.ParseFile(path);
        return FindNode(root, n => n.EditableKey == ClipMetaSchema.AtomName(field));
    }

    /// <summary>Parses the file and returns ANY atom in the clipmeta domain, or null.</summary>
    private static BoxNode? FindAnyClipmetaAtom(string path)
    {
        var root = Mp4Parser.ParseFile(path);
        return FindNode(root, n =>
            n.EditableKey is { } k && k.StartsWith(Domain + ":", StringComparison.Ordinal));
    }

    private static BoxNode? FindNode(BoxNode node, Func<BoxNode, bool> predicate)
    {
        if (predicate(node)) return node;
        foreach (var child in node.Children)
        {
            var found = FindNode(child, predicate);
            if (found != null) return found;
        }
        return null;
    }
}
