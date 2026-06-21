using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ProcessWindowSourceTests
{
    [TestMethod]
    public void Empty_ReturnsNoWindows()
    {
        IReadOnlyList<ProcessWindow> windows =
            EmptyProcessWindowSource.Instance.GetPlayerWindows(MediaPlayers.KnownProcessNames);
        Assert.AreEqual(0, windows.Count);
    }

    [TestMethod]
    public void ForCurrentPlatform_ReturnsUsableSource_ThatDoesNotThrow()
    {
        IProcessWindowSource source = ProcessWindowSource.ForCurrentPlatform();
        // On Linux CI this is the empty source; on Windows it enumerates real processes.
        // Either way it must return a list without throwing.
        IReadOnlyList<ProcessWindow> windows = source.GetPlayerWindows(MediaPlayers.KnownProcessNames);
        Assert.IsNotNull(windows);
    }

    [TestMethod]
    public void KnownProcessNames_IncludeSeededPlayers()
    {
        CollectionAssert.Contains((System.Collections.ICollection)MediaPlayers.KnownProcessNames, "vlc");
        CollectionAssert.Contains((System.Collections.ICollection)MediaPlayers.KnownProcessNames, "mpc-hc64");
    }
}
