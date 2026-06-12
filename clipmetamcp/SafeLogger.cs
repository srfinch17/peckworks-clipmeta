using ClipMetaCore.Abstractions;

namespace ClipMetaMcp;

/// <summary>
/// Crash-proof decorator over any <see cref="IClipMetaLogger"/>: logging failures are swallowed.
///
/// Why this must exist: <c>FileLogger</c> appends with no sharing for writers, and this server
/// hard-codes one shared log path — so a second clipmetamcp process (Claude Desktop's instance
/// plus a <c>--selftest</c> child, or two hosts) can collide and throw <c>IOException</c> from
/// <c>Log()</c>. Logger calls sit inside the session's catch blocks; an unguarded throw there
/// would escape <c>Run()</c> and kill the live MCP session mid-conversation (2026-06-11 review).
/// Diagnostics are best-effort; the protocol loop is not.
/// </summary>
public sealed class SafeLogger : IClipMetaLogger
{
    private readonly IClipMetaLogger _inner;

    /// <summary>Wraps <paramref name="inner"/> so its failures can never propagate.</summary>
    public SafeLogger(IClipMetaLogger inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc/>
    public LogLevel Level => _inner.Level;

    /// <inheritdoc/>
    public void Log(string message)
    {
        try { _inner.Log(message); }
        catch { /* a lost log line is strictly better than a dead server */ }
    }

    /// <inheritdoc/>
    public void LogVerbose(string message)
    {
        try { _inner.LogVerbose(message); }
        catch { /* see Log */ }
    }
}
