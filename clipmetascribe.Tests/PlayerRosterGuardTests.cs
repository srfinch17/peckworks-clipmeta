using ClipMetaCore.Schema;

namespace ClipMetaScribe.Tests;

[TestClass]
public class PlayerRosterGuardTests
{
    private static IReadOnlySet<string> Known(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    public void Unknown_FlagsTokensNotInKnownSet()
    {
        var unknown = PlayerRosterGuard.UnknownPlayers("chuck|miami element", Known("chuck", "chicken"));
        CollectionAssert.AreEqual(new[] { "miami element" }, unknown.ToArray());
    }

    [TestMethod]
    public void Unknown_IsCaseInsensitive_AndDeduped()
    {
        var unknown = PlayerRosterGuard.UnknownPlayers("Chuck|chuck|Bob|bob", Known("chuck"));
        CollectionAssert.AreEqual(new[] { "Bob" }, unknown.ToArray());
    }

    [TestMethod]
    public void Unknown_EmptyOrAllKnown_ReturnsEmpty()
    {
        Assert.AreEqual(0, PlayerRosterGuard.UnknownPlayers("", Known("chuck")).Count);
        Assert.AreEqual(0, PlayerRosterGuard.UnknownPlayers("chuck|chicken", Known("chuck", "chicken")).Count);
    }
}
