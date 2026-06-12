using ClipMetaCore.Abstractions;
using ClipMetaMcp;

namespace ClipMetaMcp.Tests;

/// <summary>
/// SafeLogger exists so that logging failures (cross-process contention on the shared
/// mcp.log) can never escape into the protocol loop and kill the session.
/// </summary>
[TestClass]
public class SafeLoggerTests
{
    private sealed class ThrowingLogger : IClipMetaLogger
    {
        public LogLevel Level => LogLevel.Verbose;
        public void Log(string message) => throw new IOException("log file is locked by another process");
        public void LogVerbose(string message) => throw new IOException("log file is locked by another process");
    }

    [TestMethod]
    public void Log_InnerThrows_DoesNotPropagate()
    {
        var logger = new SafeLogger(new ThrowingLogger());

        logger.Log("hello"); // must not throw
    }

    [TestMethod]
    public void LogVerbose_InnerThrows_DoesNotPropagate()
    {
        var logger = new SafeLogger(new ThrowingLogger());

        logger.LogVerbose("hello"); // must not throw
    }

    [TestMethod]
    public void Level_PassesThroughToInner()
    {
        var logger = new SafeLogger(new ThrowingLogger());

        Assert.AreEqual(LogLevel.Verbose, logger.Level);
    }
}
