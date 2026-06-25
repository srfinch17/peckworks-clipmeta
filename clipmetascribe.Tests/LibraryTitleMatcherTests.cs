using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Library-aware title matching: instead of extracting a token from arbitrary player-title text
/// and hoping it equals a library key, find which KNOWN library basename appears in the title.
/// This is immune to title-format quirks (timecode prefixes, OSD text, paused state) that broke
/// the old extract-then-exact-match path for MPC-HC.
/// </summary>
[TestClass]
public class LibraryTitleMatcherTests
{
    private static readonly string[] SonsLibrary =
    {
        "Sons of the Forest 2025.03.17 - 23.27.30.27.DVR.mp4",
        "Sons of the Forest 2025.03.18 - 00.15.57.28.DVR.mp4",
    };

    [TestMethod]
    public void FindBestMatch_MpcHcTitleWithTimecodePrefix_ResolvesExactClip()
    {
        // THE headline dogfooding bug: MPC-HC shows a playback-position prefix whose colons made
        // the old bare-name regex capture "23 - Sons...mp4", which no library key equalled, so the
        // player-title path silently went quiet (intermittent ✓✗✗✓✓). Containment must resolve it.
        string title = "00:01:23 - Sons of the Forest 2025.03.17 - 23.27.30.27.DVR.mp4";

        string? match = LibraryTitleMatcher.FindBestMatch(title, SonsLibrary);

        Assert.AreEqual("Sons of the Forest 2025.03.17 - 23.27.30.27.DVR.mp4", match);
    }

    [TestMethod]
    public void FindBestMatch_VlcCleanTitle_Resolves()
    {
        string title = "Sons of the Forest 2025.03.18 - 00.15.57.28.DVR.mp4 - VLC media player";

        string? match = LibraryTitleMatcher.FindBestMatch(title, SonsLibrary);

        Assert.AreEqual("Sons of the Forest 2025.03.18 - 00.15.57.28.DVR.mp4", match);
    }

    [TestMethod]
    public void FindBestMatch_BoundarySafety_DoesNotMatchOnAFilenameSuffix()
    {
        // Library has clip.mp4; the player is on a DIFFERENT file myclip.mp4. A naive Contains
        // would match clip.mp4 inside "myclip.mp4". The preceding char 'y' is a valid filename
        // char, so the boundary check must reject it.
        string? match = LibraryTitleMatcher.FindBestMatch(
            "myclip.mp4 - VLC media player", new[] { "clip.mp4" });

        Assert.IsNull(match);
    }

    [TestMethod]
    public void FindBestMatch_PrefersLongestMatch()
    {
        // Both are known and both are substrings (at a boundary) of the title; the more specific
        // (longest) one wins so prefix-overlap resolves deterministically.
        string[] library = { "clip.mp4", "my clip.mp4" };

        string? match = LibraryTitleMatcher.FindBestMatch(
            "my clip.mp4 - VLC media player", library);

        Assert.AreEqual("my clip.mp4", match);
    }

    [TestMethod]
    public void FindBestMatch_TitleNamingNoKnownClip_ReturnsNull()
    {
        string? match = LibraryTitleMatcher.FindBestMatch(
            "absent.mp4 - VLC media player", SonsLibrary);

        Assert.IsNull(match);
    }

    [TestMethod]
    public void FindBestMatch_NullOrEmptyTitle_ReturnsNull()
    {
        Assert.IsNull(LibraryTitleMatcher.FindBestMatch(null, SonsLibrary));
        Assert.IsNull(LibraryTitleMatcher.FindBestMatch("   ", SonsLibrary));
    }

    [TestMethod]
    public void FindBestMatch_CaseInsensitive()
    {
        // Windows filesystem semantics: a title that lowercases the name still resolves.
        string? match = LibraryTitleMatcher.FindBestMatch(
            "sons of the forest 2025.03.17 - 23.27.30.27.dvr.mp4 - mpc-hc", SonsLibrary);

        Assert.AreEqual("Sons of the Forest 2025.03.17 - 23.27.30.27.DVR.mp4", match);
    }
}
