using ClipMetaCore.Watching;
using ClipMetaScribe.Tests.Helpers;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Unit tests for <see cref="RecentWriteSignal"/> — the gaming-mode "clip just saved" signal. Timing
/// is driven by an injected clock and explicit write times so the tests are deterministic.
/// </summary>
[TestClass]
public class RecentWriteSignalTests
{
    private string _dir = null!;
    private static readonly DateTime Now = new(2026, 6, 26, 18, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Done() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private string Touch(string name, DateTime writeTimeUtc)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        File.SetLastWriteTimeUtc(path, writeTimeUtc);
        return path;
    }

    private WatchContext Ctx() => WatchContext.Build(_dir, Array.Empty<ProcessWindow>());

    private RecentWriteSignal Signal(TimeSpan? window = null) =>
        new(clock: () => Now, window: window);

    [TestMethod]
    public void Detect_SingleFreshWrite_OneUnambiguousHit()
    {
        string clip = Touch("clip.mp4", Now.AddSeconds(-3));

        List<SignalHit> hits = Signal().Detect(Ctx()).ToList();

        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual(clip, hits[0].ClipPath);
        Assert.AreEqual(RecentWriteSignal.SourceName, hits[0].Source);
        Assert.IsFalse(hits[0].Ambiguous, "a single just-saved clip is the unambiguous gaming bind");
        Assert.IsNull(hits[0].Player);
    }

    [TestMethod]
    public void Detect_TwoFreshWrites_BothAmbiguous_NewestFirst()
    {
        string older = Touch("older.mp4", Now.AddSeconds(-60));
        string newer = Touch("newer.mp4", Now.AddSeconds(-2));

        List<SignalHit> hits = Signal().Detect(Ctx()).ToList();

        Assert.AreEqual(2, hits.Count);
        Assert.AreEqual(newer, hits[0].ClipPath, "newest write first");
        Assert.AreEqual(older, hits[1].ClipPath);
        Assert.IsTrue(hits.All(h => h.Ambiguous), "several saved at once → ambiguous");
    }

    [TestMethod]
    public void Detect_WriteOutsideWindow_NoHit()
    {
        Touch("stale.mp4", Now.AddMinutes(-30)); // older than the default 5-minute window

        Assert.AreEqual(0, Signal().Detect(Ctx()).Count(),
            "a clip written long ago is not a 'just saved' signal");
    }

    [TestMethod]
    public void Detect_FutureWriteTime_Ignored()
    {
        // Clock skew could stamp a write slightly in the future; it must not count as "elapsed".
        Touch("future.mp4", Now.AddMinutes(5));

        Assert.AreEqual(0, Signal().Detect(Ctx()).Count());
    }

    [TestMethod]
    public void Detect_CustomWindow_Respected()
    {
        Touch("clip.mp4", Now.AddSeconds(-90));

        Assert.AreEqual(0, Signal(window: TimeSpan.FromSeconds(30)).Detect(Ctx()).Count(),
            "outside a tight 30s window");
        Assert.AreEqual(1, Signal(window: TimeSpan.FromMinutes(2)).Detect(Ctx()).Count(),
            "inside a 2-minute window");
    }
}
