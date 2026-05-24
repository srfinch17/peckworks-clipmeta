using ClipMetaCore.Write;

namespace ClipMetaCore.Abstractions;

/// <summary>Writes metadata mutations into a media file safely.</summary>
public interface IMediaWriter
{
    /// <summary>Returns true if this writer can handle the given file extension.</summary>
    bool CanWrite(string filePath);

    /// <summary>
    /// Applies the mutation to the file using a temp-file strategy.
    /// The original is never opened for writing; on any failure it is untouched.
    /// </summary>
    void WriteMetadata(string filePath, MetadataMutation mutation, IClipMetaLogger logger);
}
