using ClipMetaCore.Abstractions;

namespace ClipMetaCore.Logging;

/// <summary>
/// Writes structured log entries to a file.
/// Rotates at 10 MB; keeps at most 3 log files (oldest deleted when limit is reached).
/// </summary>
public sealed class FileLogger : IClipMetaLogger
{
    private readonly string _logPath;
    private readonly object _lock = new();

    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const int MaxLogFiles = 3;

    /// <inheritdoc/>
    public LogLevel Level { get; }

    /// <summary>Creates a FileLogger that writes to the given path.</summary>
    public FileLogger(string logPath, LogLevel level = LogLevel.Simple)
    {
        _logPath = logPath;
        Level = level;
        // Path.GetDirectoryName("clipmeta.log") returns "" not null, guard before CreateDirectory.
        string? dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <inheritdoc/>
    public void Log(string message) => Write(message);

    /// <inheritdoc/>
    public void LogVerbose(string message)
    {
        if (Level == LogLevel.Verbose)
            Write($"[V] {message}");
    }

    private void Write(string message)
    {
        string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        lock (_lock)
        {
            RotateIfNeeded();
            File.AppendAllText(_logPath, entry);
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logPath)) return;
        var fi = new FileInfo(_logPath);
        if (fi.Length < MaxFileSizeBytes) return;

        // Shift .log → .log.1, .log.1 → .log.2, delete .log.(MaxLogFiles-1)
        for (int i = MaxLogFiles - 1; i >= 1; i--)
        {
            string old = $"{_logPath}.{i}";
            string newer = i == 1 ? _logPath : $"{_logPath}.{i - 1}";
            if (File.Exists(old)) File.Delete(old);
            if (File.Exists(newer)) File.Move(newer, old);
        }
    }
}
