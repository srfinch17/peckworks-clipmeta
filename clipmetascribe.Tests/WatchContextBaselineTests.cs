using ClipMetaCore.Read;
using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class WatchContextBaselineTests
{
    private static string NewLibrary()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cmwatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void Build_ReadsCreationTime_AndEmptyBaseline_WhenNoIndex()
    {
        string dir = NewLibrary();
        try
        {
            string clip = Path.Combine(dir, "a.mp4");
            File.WriteAllBytes(clip, new byte[] { 1, 2, 3 });

            WatchContext ctx = WatchContext.Build(dir, Array.Empty<ProcessWindow>());

            Assert.AreEqual(1, ctx.LibraryClips.Count);
            Assert.AreNotEqual(default, ctx.LibraryClips[0].CreationTimeUtc);
            Assert.AreEqual(0, ctx.KnownBaselinePaths.Count); // no index file
            Assert.IsNull(ctx.Ledger);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [TestMethod]
    public void Build_LoadsBaselinePaths_FromIndex()
    {
        string dir = NewLibrary();
        try
        {
            string clip = Path.Combine(dir, "a.mp4");
            File.WriteAllBytes(clip, new byte[] { 1, 2, 3 });
            // Build a real index file in the library so the baseline picks it up.
            ClipMetaIndex.WriteToFile(ClipMetaIndex.Build(dir), Path.Combine(dir, ClipMetaIndex.IndexFileName));

            WatchContext ctx = WatchContext.Build(dir, Array.Empty<ProcessWindow>());

            Assert.IsTrue(ctx.KnownBaselinePaths.Contains(clip)); // case-insensitive set
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
