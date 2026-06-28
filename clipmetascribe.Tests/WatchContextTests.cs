using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class WatchContextTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string Touch(string relativePath)
    {
        string path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    [TestMethod]
    public void Build_EnumeratesClipsRecursively_AndIndexesByNameAndPath()
    {
        string a = Touch("a.mp4");
        string b = Touch(Path.Combine("sub", "b.mp4"));
        Touch("notes.txt"); // must be ignored

        WatchContext ctx = WatchContext.Build(_tempDir, EmptyProcessWindowSource.Instance, MediaPlayers.KnownProcessNames);

        Assert.AreEqual(2, ctx.LibraryClips.Count);
        Assert.IsTrue(ctx.ByFullPath.ContainsKey(a));
        Assert.IsTrue(ctx.ByFullPath.ContainsKey(b));
        Assert.AreEqual(1, ctx.ByFileName["a.mp4"].Count);
        Assert.AreEqual(b, ctx.ByFileName["b.mp4"].Single().FullPath);
    }

    [TestMethod]
    public void Build_DuplicateFileNames_GroupedUnderOneNameKey()
    {
        Touch("dup.mp4");
        Touch(Path.Combine("sub", "dup.mp4"));

        WatchContext ctx = WatchContext.Build(_tempDir, EmptyProcessWindowSource.Instance, MediaPlayers.KnownProcessNames);

        Assert.AreEqual(2, ctx.ByFileName["dup.mp4"].Count);
    }

    [TestMethod]
    public void Build_PopulatesPlayerWindowsFromSource()
    {
        var source = new FakeProcessWindowSource(new ProcessWindow("vlc", "x.mp4 - VLC media player"));

        WatchContext ctx = WatchContext.Build(_tempDir, source, MediaPlayers.KnownProcessNames);

        Assert.AreEqual(1, ctx.PlayerWindows.Count);
        Assert.AreEqual("vlc", ctx.PlayerWindows[0].ProcessName);
    }

    [TestMethod]
    public void WithPlayerWindows_ReusesLibrary_SwapsWindows()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.mp4"), Array.Empty<byte>());
            WatchContext baseCtx = WatchContext.Build(dir, Array.Empty<ProcessWindow>());
            var win = new[] { new ProcessWindow("vlc", "a.mp4 - VLC media player") };

            WatchContext swapped = baseCtx.WithPlayerWindows(win);

            Assert.AreSame(baseCtx.ByFullPath, swapped.ByFullPath, "library lookups are reused, not re-enumerated");
            Assert.AreSame(baseCtx.ByFileName, swapped.ByFileName);
            CollectionAssert.AreEqual(win, swapped.PlayerWindows.ToList(), "windows are replaced");
            Assert.AreEqual(0, baseCtx.PlayerWindows.Count, "the original is unchanged");
        }
        finally { Directory.Delete(dir, true); }
    }
}
