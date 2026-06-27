using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ReviewFlagResolverTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Done() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private void Touch(string name) => File.WriteAllBytes(Path.Combine(_dir, name), Array.Empty<byte>());

    private WatchContext Context() =>
        WatchContext.Build(_dir, Array.Empty<ProcessWindow>());

    [TestMethod]
    public void Resolve_RawTitle_BecomesLibraryBasename()
    {
        Touch("_3.mp4");
        var flags = new[]
        {
            new ReviewFlag(ReviewFlag.TypeSequenceSkip, new[] { "00:12 / 01:00 - _3.mp4 - MPC-HC" }),
        };

        IReadOnlyList<ReviewFlag> resolved = ReviewFlagResolver.Resolve(flags, Context());

        CollectionAssert.AreEqual(new[] { "_3.mp4" }, resolved[0].Clips.ToList());
    }

    [TestMethod]
    public void Resolve_UnresolvableEntries_AreDropped()
    {
        Touch("_3.mp4");
        var flags = new[]
        {
            new ReviewFlag(ReviewFlag.TypeMultiplePlayersActive, new[] { "vlc", "_3.mp4 - VLC media player" }),
        };

        IReadOnlyList<ReviewFlag> resolved = ReviewFlagResolver.Resolve(flags, Context());

        CollectionAssert.AreEqual(new[] { "_3.mp4" }, resolved[0].Clips.ToList(),
            "the bare 'vlc' token resolves to no library clip and is dropped");
    }

    [TestMethod]
    public void Resolve_DuplicateClip_IsDeduped()
    {
        Touch("DVR_5.mp4");
        var flags = new[]
        {
            new ReviewFlag(ReviewFlag.TypeSequenceSkip, new[]
            {
                "DVR_5.mp4 - VLC media player",
                "DVR_5.mp4 - VLC media player",
                "DVR_5.mp4 - VLC media player",
            }),
        };

        IReadOnlyList<ReviewFlag> resolved = ReviewFlagResolver.Resolve(flags, Context());

        Assert.AreEqual(1, resolved[0].Clips.Count, "repeated clip collapses to one entry");
        Assert.AreEqual("DVR_5.mp4", resolved[0].Clips[0]);
    }

    [TestMethod]
    public void Resolve_PreservesTypeAndStableSeconds()
    {
        Touch("_3.mp4");
        var flags = new[]
        {
            new ReviewFlag(ReviewFlag.TypeAutoCorrected, new[] { "_3.mp4 - MPC-HC" }, StableSeconds: 4.2),
        };

        ReviewFlag resolved = ReviewFlagResolver.Resolve(flags, Context())[0];

        Assert.AreEqual(ReviewFlag.TypeAutoCorrected, resolved.Type);
        Assert.AreEqual(4.2, resolved.StableSeconds, 0.001);
    }
}
