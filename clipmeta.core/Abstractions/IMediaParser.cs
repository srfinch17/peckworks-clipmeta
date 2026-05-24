using ClipMetaCore.Mp4;

namespace ClipMetaCore.Abstractions;

/// <summary>Reads a media file and returns its box/atom tree.</summary>
public interface IMediaParser
{
    /// <summary>Returns true if this parser can handle the given file extension.</summary>
    bool CanParse(string filePath);

    /// <summary>Parses the file and returns the root node of its structure tree.</summary>
    BoxNode ParseFile(string filePath);
}
