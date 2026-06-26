using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ReviewWatcherTests
{
    /// <summary>A source whose returned windows can be swapped between polls.</summary>
    private sealed class MutableSource : IProcessWindowSource
    {
        public volatile IReadOnlyList<ProcessWindow> Windows = Array.Empty<ProcessWindow>();
        public bool Throw;
        public IReadOnlyList<ProcessWindow> GetPlayerWindows(IReadOnlyCollection<string> names)
        {
            if (Throw) throw new InvalidOperationException("boom");
            return Windows;
        }
    }

    private DateTimeOffset _now;
    private ReviewWatcher Make(MutableSource src) =>
        new(src, () => _now, TimeSpan.FromMilliseconds(10));

    [TestInitialize]
    public void Init() => _now = new DateTimeOffset(2026, 6, 26, 6, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void PollOnce_TitleChange_OpensAndClosesSegments()
    {
        var src = new MutableSource();
        using var w = Make(src);

        src.Windows = new[] { new ProcessWindow("vlc", "_1.mp4 - VLC media player") };
        w.PollOnce();
        _now = _now.AddSeconds(5);
        src.Windows = new[] { new ProcessWindow("vlc", "_2.mp4 - VLC media player") };
        w.PollOnce();

        IReadOnlyList<TitleSegment> segs = w.Snapshot();
        Assert.AreEqual(2, segs.Count);
        Assert.IsNotNull(segs[0].EndedAt, "first segment should be closed when the title changed");
        Assert.IsNull(segs[1].EndedAt, "current segment stays open");
        Assert.IsTrue(segs[1].Id > segs[0].Id, "ids are monotonic");
    }

    [TestMethod]
    public void PollOnce_PlayerVanished_ClosesOpenSegment()
    {
        var src = new MutableSource { Windows = new[] { new ProcessWindow("vlc", "_1.mp4 - VLC media player") } };
        using var w = Make(src);
        w.PollOnce();
        _now = _now.AddSeconds(3);
        src.Windows = Array.Empty<ProcessWindow>();
        w.PollOnce();

        Assert.IsNotNull(w.Snapshot()[0].EndedAt);
    }

    [TestMethod]
    public void PollOnce_SameTitle_DoesNotOpenNewSegment()
    {
        var src = new MutableSource { Windows = new[] { new ProcessWindow("vlc", "_1.mp4 - VLC media player") } };
        using var w = Make(src);
        w.PollOnce();
        w.PollOnce();
        Assert.AreEqual(1, w.Snapshot().Count);
    }

    [TestMethod]
    public void PollOnce_ThrowingSource_IsSwallowed()
    {
        var src = new MutableSource { Throw = true };
        using var w = Make(src);
        w.PollOnce(); // must not throw
        Assert.AreEqual(0, w.Snapshot().Count);
    }

    [TestMethod]
    public void RingBuffer_CapsSegmentCount()
    {
        var src = new MutableSource();
        using var w = new ReviewWatcher(src, () => _now, TimeSpan.FromMilliseconds(10), maxSegments: 3);
        for (int i = 0; i < 6; i++)
        {
            src.Windows = new[] { new ProcessWindow("vlc", $"clip{i}.mp4 - VLC media player") };
            _now = _now.AddSeconds(5);
            w.PollOnce();
        }
        Assert.IsTrue(w.Snapshot().Count <= 3);
    }

    [TestMethod]
    public void MarkBound_RecordsLastBoundId()
    {
        using var w = Make(new MutableSource());
        w.MarkBound(42);
        Assert.AreEqual(42, w.LastBoundId);
    }

    [TestMethod]
    public void Snapshot_IsIsolatedCopy()
    {
        var src = new MutableSource { Windows = new[] { new ProcessWindow("vlc", "_1.mp4 - VLC media player") } };
        using var w = Make(src);
        w.PollOnce();
        IReadOnlyList<TitleSegment> first = w.Snapshot();
        src.Windows = new[] { new ProcessWindow("vlc", "_2.mp4 - VLC media player") };
        _now = _now.AddSeconds(5);
        w.PollOnce();
        Assert.AreEqual(1, first.Count, "an earlier snapshot must not mutate");
    }
}
