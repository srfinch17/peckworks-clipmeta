namespace ClipMetaCore;

/// <summary>Thrown when a file format is not supported for parsing or writing.</summary>
public sealed class UnsupportedFormatException : Exception
{
    /// <inheritdoc/>
    public UnsupportedFormatException(string message) : base(message) { }
    /// <inheritdoc/>
    public UnsupportedFormatException(string message, Exception inner) : base(message, inner) { }
}
