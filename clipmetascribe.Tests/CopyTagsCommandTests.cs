using ClipMetaCore.Logging;
using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using ClipMetaScribe.Commands;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Integration + edge tests for <see cref="CopyTagsCommand"/> over real clips. Proves the copy
/// merges source fields onto the destination, preserves the destination's own fields and media,
/// and refuses the no-op / nonsensical cases. Graceful-skips clip-less.
/// </summary>
[TestClass]
public class CopyTagsCommandTests
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<string> _scratch = new();

    [ClassCleanup]
    public static void Cleanup()
    {
        foreach (var p in _scratch)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
            try { if (File.Exists(p + ".tmp")) File.Delete(p + ".tmp"); } catch { /* best effort */ }
        }
    }

    public static IEnumerable<object[]> PristineClips() => TestClipsLocator.PristineClipRows();

    private static string Smallest() =>
        TestClipsLocator.AllPristine().OrderBy(p => new FileInfo(p).Length).First();

    private static string Scratch(string pristine)
    {
        string s = ScratchClips.Prepare(pristine);
        _scratch.Add(s);
        return s;
    }

    private static string Tagged(string pristine, params (string field, string value)[] fields)
    {
        string s = Scratch(pristine);
        var m = new MetadataMutation();
        foreach (var (f, v) in fields) m.SetFields[ClipMetaSchema.AtomName(f)] = v;
        new Mp4Writer().WriteMetadata(s, m, NullLogger.Instance);
        return s;
    }

    private static Dictionary<string, string> ReadFields(string path) =>
        ClipMetaReader.GetUserFields(Mp4Parser.ParseFile(path))
                      .ToDictionary(f => f.Field, f => f.Value, StringComparer.Ordinal);

    [TestMethod]
    public void Copy_MergesSourceFieldsAndPreservesDestOwnFields()
    {
        string pristine = Smallest();
        string source = Tagged(pristine, ("game", "Team Fortress 2"), ("tags", "rocket jump|headshot"));
        string dest = Tagged(pristine, ("rating", "5"));

        int code = CopyTagsCommand.Run(dest, source, new MetadataMutation(), NullLogger.Instance);

        Assert.AreEqual(0, code);
        var f = ReadFields(dest);
        Assert.AreEqual("Team Fortress 2", f["game"], "source's game must be copied");
        Assert.AreEqual("rocket jump|headshot", f["tags"], "source's tags must be copied");
        Assert.AreEqual("5", f["rating"], "dest's own non-overlapping field must survive (merge, not replace)");
    }

    [DataTestMethod]
    [DynamicData(nameof(PristineClips), DynamicDataSourceType.Method)]
    public void Copy_LeavesDestMediaByteIdentical(string pristinePath)
    {
        TestClipsLocator.SkipIfMissing(pristinePath);
        string source = Tagged(pristinePath, ("game", "TF2"), ("notes", "copied"));
        string dest = Scratch(pristinePath);   // dest media baseline == the pristine original

        CopyTagsCommand.Run(dest, source, new MetadataMutation(), NullLogger.Instance);

        MediaIntegrityScanner.AssertMediaUnchanged(pristinePath, dest);
        Assert.AreEqual("TF2", ReadFields(dest)["game"], "copy must actually land the field");
    }

    [TestMethod]
    public void Copy_SourceWithNoClipmetaFields_WritesNothing_ReturnsZero()
    {
        string pristine = Smallest();
        string source = Scratch(pristine);   // a pristine copy carries no clipmeta user fields
        string dest = Scratch(pristine);
        byte[] before = File.ReadAllBytes(dest);

        int code = RunQuiet(() => CopyTagsCommand.Run(dest, source, new MetadataMutation(), NullLogger.Instance));

        Assert.AreEqual(0, code);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(dest), "an empty-source copy must not modify dest");
    }

    [TestMethod]
    public void Copy_SourceEqualsDest_Rejected()
    {
        string clip = Scratch(Smallest());
        int code = RunQuiet(() => CopyTagsCommand.Run(clip, clip, new MetadataMutation(), NullLogger.Instance));
        Assert.AreEqual(1, code);
    }

    [TestMethod]
    public void Copy_MissingSource_Rejected()
    {
        string dest = Scratch(Smallest());
        string missing = Path.Combine(Path.GetDirectoryName(dest)!, "does_not_exist.mp4");
        int code = RunQuiet(() => CopyTagsCommand.Run(dest, missing, new MetadataMutation(), NullLogger.Instance));
        Assert.AreEqual(1, code);
    }

    /// <summary>Runs an action with Console.Out/Error suppressed (these paths print user messages).</summary>
    private static int RunQuiet(Func<int> action)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        try { return action(); }
        finally { Console.SetOut(origOut); Console.SetError(origErr); }
    }
}
