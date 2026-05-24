using ClipMetaCore.Abstractions;
using ClipMetaCore.Logging;

namespace ClipMetaScribe.Tests;

[TestClass]
public class FileLoggerTests
{
    private string _logDir = string.Empty;

    [TestInitialize]
    public void Setup() => _logDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true);
    }

    [TestMethod]
    public void Log_SimpleMessage_WrittenToFile()
    {
        string logPath = Path.Combine(_logDir, "clipmeta.log");
        Directory.CreateDirectory(_logDir);
        var logger = new FileLogger(logPath, LogLevel.Simple);

        logger.Log("WRITE clip001.mp4 OK");

        string content = File.ReadAllText(logPath);
        Assert.IsTrue(content.Contains("WRITE clip001.mp4 OK"));
    }

    [TestMethod]
    public void LogVerbose_WhenSimpleLevel_NotWritten()
    {
        string logPath = Path.Combine(_logDir, "clipmeta.log");
        Directory.CreateDirectory(_logDir);
        var logger = new FileLogger(logPath, LogLevel.Simple);

        logger.LogVerbose("[V] stco adjusted");

        string content = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
        Assert.IsFalse(content.Contains("[V] stco adjusted"));
    }

    [TestMethod]
    public void LogVerbose_WhenVerboseLevel_WrittenWithPrefix()
    {
        string logPath = Path.Combine(_logDir, "clipmeta.log");
        Directory.CreateDirectory(_logDir);
        var logger = new FileLogger(logPath, LogLevel.Verbose);

        logger.LogVerbose("stco adjusted");

        string content = File.ReadAllText(logPath);
        Assert.IsTrue(content.Contains("[V]"));
        Assert.IsTrue(content.Contains("stco adjusted"));
    }

    [TestMethod]
    public void Log_EntryIncludesTimestamp()
    {
        string logPath = Path.Combine(_logDir, "clipmeta.log");
        Directory.CreateDirectory(_logDir);
        var logger = new FileLogger(logPath, LogLevel.Simple);

        logger.Log("test entry");

        string content = File.ReadAllText(logPath);
        // Timestamp format: [2026-05-21 14:32:01]
        Assert.IsTrue(content.Contains("[202"), $"Expected timestamp in log, got: {content}");
    }

    [TestMethod]
    public void Rotation_WhenFileTooLarge_RotatesFile()
    {
        string logPath = Path.Combine(_logDir, "clipmeta.log");
        Directory.CreateDirectory(_logDir);
        // Pre-create an oversized log file (just over 10 MB)
        File.WriteAllBytes(logPath, new byte[10 * 1024 * 1024 + 1]);

        var logger = new FileLogger(logPath, LogLevel.Simple);
        logger.Log("trigger rotation");

        Assert.IsTrue(File.Exists(logPath + ".1"), "Previous log should be rotated to .1");
        Assert.IsTrue(new FileInfo(logPath).Length < 10 * 1024 * 1024,
            "Active log file should be smaller than 10 MB after rotation");
    }
}
