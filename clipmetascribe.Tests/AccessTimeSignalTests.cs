using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class AccessTimeSignalTests
{
    private static WatchContext Ctx(IReadOnlyList<LibraryClip> clips, SelfActionLedger? ledger) => new()
    {
        LibraryClips = clips,
        ByFileName = clips.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LibraryClip>)g.ToList(), StringComparer.OrdinalIgnoreCase),
        ByFullPath = clips.ToDictionary(c => c.FullPath, c => c, StringComparer.OrdinalIgnoreCase),
        PlayerWindows = Array.Empty<ProcessWindow>(),
        Ledger = ledger,
    };

    [TestMethod]
    public void SelfReadClip_IsExcluded()
    {
        DateTime now = new(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc);
        var ledger = new SelfActionLedger(() => new DateTimeOffset(now));
        ledger.MarkRead(@"C:\lib\read.mp4");
        var clips = new[]
        {
            new LibraryClip(@"C:\lib\read.mp4", "read.mp4", now, now, now),
            new LibraryClip(@"C:\lib\other.mp4", "other.mp4", now.AddMinutes(-1), now, now),
        };

        var hits = new AccessTimeSignal(() => new DateTimeOffset(now)).Detect(Ctx(clips, ledger)).Select(h => h.ClipPath).ToList();

        CollectionAssert.DoesNotContain(hits, @"C:\lib\read.mp4");
        CollectionAssert.Contains(hits, @"C:\lib\other.mp4");
    }

    [TestMethod]
    public void NoLedger_EmitsAll()
    {
        DateTime now = DateTime.UtcNow;
        var clips = new[] { new LibraryClip(@"C:\lib\a.mp4", "a.mp4", now, now, now) };
        Assert.AreEqual(1, new AccessTimeSignal().Detect(Ctx(clips, ledger: null)).Count());
    }
}
