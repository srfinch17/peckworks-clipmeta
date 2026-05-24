namespace ClipMetaCore.Abstractions;

/// <summary>Verbosity levels for clipmeta operations.</summary>
public enum LogLevel { Simple, Verbose }

/// <summary>Structured logger for clipmeta operations.</summary>
public interface IClipMetaLogger
{
    /// <summary>Current verbosity level.</summary>
    LogLevel Level { get; }

    /// <summary>Logs a message at Simple level (always written).</summary>
    void Log(string message);

    /// <summary>Logs a message at Verbose level (no-op unless Level == Verbose).</summary>
    void LogVerbose(string message);
}
