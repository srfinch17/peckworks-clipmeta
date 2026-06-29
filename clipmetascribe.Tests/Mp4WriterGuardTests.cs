using ClipMetaCore.Logging;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Tests for the write engine's concurrency and collision guards (added after the 2026-06-10
/// audit follow-up):
/// <list type="bullet">
///   <item>The source file is held with a deny-writers share for the whole parse+copy, so a
///       file that is still being written (e.g. a capture tool mid-recording) is refused up
///       front instead of producing a torn output whose chunk offsets describe bytes that moved.</item>
///   <item>The temp file uses a unique per-write name, so it can never overwrite a real file
///       the user happens to call <c>clip.mp4.tmp</c>.</item>
///   <item>Appending to an atom whose payload is not text is refused, instead of splicing the
///       parser's display placeholder ("[JPEG image, …]") into the file as if it were data.</item>
/// </list>
/// </summary>
[TestClass]
public class Mp4WriterGuardTests
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
        // Sweep any stray <name>.<guid>.tmp files the unique-temp scheme may have left
        // if an assertion fired mid-test.
        foreach (string stray in _tempFiles.SelectMany(p => Directory.EnumerateFiles(
                     Path.GetDirectoryName(p)!, Path.GetFileName(p) + ".*.tmp")))
        {
            try { File.Delete(stray); } catch { /* best effort */ }
        }
    }

    private string Save(MemoryStream ms)
    {
        string path = MinimalMp4Builder.SaveToTempFile(ms);
        _tempFiles.Add(path);
        return path;
    }

    // ── Source locked by another writer (the "still recording" case) ───────────

    [TestMethod]
    public void SourceHeldByWriter_WriteRefused_OriginalUntouched()
    {
        string path = Save(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(Domain, "tags", "x"));
        byte[] before = File.ReadAllBytes(path);

        // Simulate a recorder: a live handle with WRITE access (sharing Read, as encoders
        // typically do so players can preview the growing file). Our writer opens with
        // FileShare.Read, which denies existing writers, so this must fail immediately.
        using (var recorder = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var mutation = new MetadataMutation();
            mutation.SetFields[$"{Domain}:game"] = "TF2";

            Assert.ThrowsExactly<IOException>(() =>
                new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance),
                "a file open for writing elsewhere must be refused");
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(path),
            "refused write must leave the original byte-identical");
    }

    [TestMethod]
    public void SourceReleasedByWriter_SameWriteThenSucceeds()
    {
        // The flip side: the refusal is purely about the live conflicting handle. Once the
        // "recorder" lets go, the identical write goes through.
        string path = Save(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(Domain, "tags", "x"));

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{Domain}:game"] = "TF2";

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            Assert.ThrowsExactly<IOException>(() =>
                new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance));
        }

        // Handle released, now it must work.
        new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance);
    }

    // ── Unique temp names: a user's .tmp file is never collateral damage ───────

    [TestMethod]
    public void PreexistingTmpFile_NotOverwrittenByWrite()
    {
        // Before the unique-name scheme, the writer used exactly "<file>.tmp" with
        // FileMode.Create, so a real user file named clip.mp4.tmp would be destroyed by
        // tagging clip.mp4. Now the temp name embeds a GUID and can never collide.
        string path = Save(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(Domain, "tags", "x"));
        string usersOwnTmp = path + ".tmp";
        File.WriteAllText(usersOwnTmp, "the user's own precious data");
        _tempFiles.Add(usersOwnTmp);

        var mutation = new MetadataMutation();
        mutation.SetFields[$"{Domain}:game"] = "TF2";
        new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance);

        Assert.AreEqual("the user's own precious data", File.ReadAllText(usersOwnTmp),
            "a pre-existing <file>.tmp must survive a write untouched");
    }

    [TestMethod]
    public void FailedWrite_LeavesNoTempFilesBehind()
    {
        // The unique-name scheme must not weaken cleanup: force a failure (non-text append)
        // and confirm no <file>.<guid>.tmp remains in the directory.
        string path = Save(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(
            Domain, "tags", "binary", seedDataType: 13 /* JPEG */));

        var mutation = new MetadataMutation();
        mutation.AppendFields[$"{Domain}:tags"] = "more";

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance));

        string[] strays = Directory.GetFiles(
            Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*.tmp");
        Assert.AreEqual(0, strays.Length,
            $"failed write left temp files behind: {string.Join(", ", strays)}");
    }

    // ── Appending to non-text data must refuse, not corrupt ────────────────────

    [TestMethod]
    public void AppendToNonTextAtom_Refused()
    {
        // The atom's data box says "type 13 = JPEG". The parser displays it as a placeholder
        // like "[JPEG image, N bytes]". The old append path blindly stripped the first and
        // last character of whatever the display string was and stored the rest as the new
        // value, writing placeholder text over binary data. Now it must refuse.
        string path = Save(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(
            Domain, "tags", "fake-jpeg-bytes", seedDataType: 13));
        byte[] before = File.ReadAllBytes(path);

        var mutation = new MetadataMutation();
        mutation.AppendFields[$"{Domain}:tags"] = "extra";

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance));
        StringAssert.Contains(ex.Message, "not text",
            "the error should explain WHY the append was refused");

        CollectionAssert.AreEqual(before, File.ReadAllBytes(path),
            "refused append must leave the original byte-identical");
    }

    [TestMethod]
    public void AppendToTextAtom_StillWorks()
    {
        // Guard must not over-trigger: appending to a normal quoted text value stays legal.
        string path = Save(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat(Domain, "tags", "first"));

        var mutation = new MetadataMutation();
        mutation.AppendFields[$"{Domain}:tags"] = "second";
        new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance);

        var root = ClipMetaCore.Mp4.Mp4Parser.ParseFile(path);
        var tags = FindNode(root, n => n.EditableKey == $"{Domain}:tags");
        Assert.IsNotNull(tags);
        StringAssert.Contains(tags!.DisplayValue, "first|second",
            "append must merge into the existing pipe list");
    }

    [TestMethod]
    public void AppendToAbsentAtom_BehavesAsSet()
    {
        // Appending where nothing exists yet is just a set, must not throw.
        string path = Save(MinimalMp4Builder.BuildMoovFirstWithPatternedMdat());

        var mutation = new MetadataMutation();
        mutation.AppendFields[$"{Domain}:tags"] = "only";
        new Mp4Writer().WriteMetadata(path, mutation, NullLogger.Instance);

        var root = ClipMetaCore.Mp4.Mp4Parser.ParseFile(path);
        var tags = FindNode(root, n => n.EditableKey == $"{Domain}:tags");
        Assert.IsNotNull(tags);
        StringAssert.Contains(tags!.DisplayValue, "only");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ClipMetaCore.Mp4.BoxNode? FindNode(
        ClipMetaCore.Mp4.BoxNode node, Func<ClipMetaCore.Mp4.BoxNode, bool> predicate)
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
