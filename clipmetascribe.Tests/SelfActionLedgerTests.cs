using ClipMetaCore.Watching;

namespace ClipMetaScribe.Tests;

[TestClass]
public class SelfActionLedgerTests
{
    private static DateTimeOffset T0 => new(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void MarkWritten_IsWrittenWithinWindow()
    {
        var clock = T0;
        var ledger = new SelfActionLedger(() => clock);
        ledger.MarkWritten(@"C:\lib\a.mp4");
        Assert.IsTrue(ledger.WasWrittenWithin(@"C:\lib\a.mp4", TimeSpan.FromMinutes(5), T0));
        Assert.IsTrue(ledger.WasTouchedWithin(@"c:\LIB\A.MP4", TimeSpan.FromMinutes(5), T0)); // case-insensitive
    }

    [TestMethod]
    public void Read_DoesNotCountAsWritten()
    {
        var ledger = new SelfActionLedger(() => T0);
        ledger.MarkRead(@"C:\lib\a.mp4");
        Assert.IsFalse(ledger.WasWrittenWithin(@"C:\lib\a.mp4", TimeSpan.FromMinutes(5), T0));
        Assert.IsTrue(ledger.WasTouchedWithin(@"C:\lib\a.mp4", TimeSpan.FromMinutes(5), T0));
    }

    [TestMethod]
    public void WrittenThenRead_StaysWritten()
    {
        var ledger = new SelfActionLedger(() => T0);
        ledger.MarkWritten(@"C:\lib\a.mp4");
        ledger.MarkRead(@"C:\lib\a.mp4");
        Assert.IsTrue(ledger.WasWrittenWithin(@"C:\lib\a.mp4", TimeSpan.FromMinutes(5), T0));
    }

    [TestMethod]
    public void OutsideWindow_IsNotWithin()
    {
        var ledger = new SelfActionLedger(() => T0);
        ledger.MarkWritten(@"C:\lib\a.mp4");
        Assert.IsFalse(ledger.WasWrittenWithin(@"C:\lib\a.mp4", TimeSpan.FromMinutes(5), T0.AddMinutes(6)));
    }
}
