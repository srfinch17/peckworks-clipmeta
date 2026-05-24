using ClipMetaCore.Abstractions;

namespace ClipMetaCore.Logging;

/// <summary>No-op logger for use in tests and dry-run scenarios.</summary>
public sealed class NullLogger : IClipMetaLogger
{
    /// <summary>Singleton instance.</summary>
    public static readonly NullLogger Instance = new();

    /// <inheritdoc/>
    public LogLevel Level => LogLevel.Simple;

    /// <inheritdoc/>
    public void Log(string message) { }

    /// <inheritdoc/>
    public void LogVerbose(string message) { }
}
