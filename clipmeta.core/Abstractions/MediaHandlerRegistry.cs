namespace ClipMetaCore.Abstractions;

using ClipMetaCore;

/// <summary>Selects the correct parser or writer for a given file by extension.</summary>
public sealed class MediaHandlerRegistry
{
    private readonly List<IMediaParser> _parsers = new();
    private readonly List<IMediaWriter> _writers = new();

    /// <summary>Registers a parser. Parsers are evaluated in registration order.</summary>
    public void RegisterParser(IMediaParser parser) => _parsers.Add(parser);

    /// <summary>Registers a writer. Writers are evaluated in registration order.</summary>
    public void RegisterWriter(IMediaWriter writer) => _writers.Add(writer);

    /// <summary>Returns the first parser that can handle the given file.</summary>
    /// <exception cref="UnsupportedFormatException">When no registered parser matches.</exception>
    public IMediaParser GetParser(string filePath)
    {
        return _parsers.FirstOrDefault(p => p.CanParse(filePath))
            ?? throw new UnsupportedFormatException(
                $"No parser registered for '{Path.GetExtension(filePath)}' files.");
    }

    /// <summary>Returns the first writer that can handle the given file.</summary>
    /// <exception cref="UnsupportedFormatException">When no registered writer matches.</exception>
    public IMediaWriter GetWriter(string filePath)
    {
        return _writers.FirstOrDefault(w => w.CanWrite(filePath))
            ?? throw new UnsupportedFormatException(
                $"No writer registered for '{Path.GetExtension(filePath)}' files.");
    }
}
