using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class PlayerTitleResolutionTests
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

    private string Touch(string name)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private WatchContext Build(params ProcessWindow[] windows) =>
        WatchContext.Build(_tempDir, new FakeProcessWindowSource(windows), MediaPlayers.KnownProcessNames);

    [TestMethod]
    public void For_BareNameInLibrary_ResolvesWithBareNameKind()
    {
        string clip = Touch("clip.mp4");
        IReadOnlyList<PlayerMatch> matches =
            PlayerTitleResolution.For(Build(new ProcessWindow("vlc", "clip.mp4 - VLC media player")));

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual(TitleExtractionKind.BareName, matches[0].Kind);
        Assert.AreEqual(clip, matches[0].Matches.Single().FullPath);
    }

    [TestMethod]
    public void For_FullPathInLibrary_ResolvesWithFullPathKind()
    {
        string clip = Touch("clip.mp4");
        IReadOnlyList<PlayerMatch> matches =
            PlayerTitleResolution.For(Build(new ProcessWindow("mpc-hc64", $"{clip} - MPC-HC")));

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual(TitleExtractionKind.FullPath, matches[0].Kind);
        Assert.AreEqual(clip, matches[0].Matches.Single().FullPath);
    }

    [TestMethod]
    public void For_NamedFileNotInLibrary_ReturnsEntryWithNoMatches()
    {
        Touch("present.mp4");
        IReadOnlyList<PlayerMatch> matches =
            PlayerTitleResolution.For(Build(new ProcessWindow("vlc", "absent.mp4 - VLC media player")));

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual("absent.mp4", matches[0].ReferencedValue);
        Assert.AreEqual(0, matches[0].Matches.Count);
    }

    [TestMethod]
    public void For_TitleWithoutMp4_IsOmitted()
    {
        Touch("clip.mp4");
        IReadOnlyList<PlayerMatch> matches =
            PlayerTitleResolution.For(Build(new ProcessWindow("vlc", "My Montage - VLC media player")));

        Assert.AreEqual(0, matches.Count);
    }
}
