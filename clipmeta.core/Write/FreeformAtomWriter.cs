using System.Text;
using ClipMetaCore.Mp4;

namespace ClipMetaCore.Write;

/// <summary>
/// Writes a single <c>----</c> (freeform) MP4 atom to a stream.
/// Structure: <c>----</c> contains <c>mean</c> (domain), <c>name</c> (field), <c>data</c> (value).
/// Both <c>mean</c> and <c>name</c> are FullBoxes and carry a mandatory 4-byte version+flags prefix.
/// </summary>
public static class FreeformAtomWriter
{
    private const int DataOverhead = 16; // 8 box header + 4 (version+type) + 4 locale

    /// <summary>
    /// Writes a complete <c>----</c> atom to <paramref name="writer"/>.
    /// </summary>
    /// <param name="writer">Destination stream positioned at the write location.</param>
    /// <param name="domain">The reverse-domain namespace (e.g. "com.peckworkslab.clipmeta").</param>
    /// <param name="fieldName">The field name (e.g. "tags").</param>
    /// <param name="value">The UTF-8 value to store.</param>
    public static void Write(BinaryWriter writer, string domain, string fieldName, string value)
    {
        byte[] domainBytes = Encoding.UTF8.GetBytes(domain);
        byte[] nameBytes = Encoding.UTF8.GetBytes(fieldName);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);

        uint meanSize = (uint)(12 + domainBytes.Length);  // 8 box header + 4 FullBox prefix + domain
        uint nameSize = (uint)(12 + nameBytes.Length);
        uint dataSize = (uint)(DataOverhead + valueBytes.Length);
        uint totalSize = (uint)(8 + meanSize + nameSize + dataSize); // 8 = ---- box header

        // ---- outer box
        BigEndianWriter.WriteBoxHeader(writer, totalSize, "----");

        // mean (FullBox: version=0, flags=0, then domain string)
        BigEndianWriter.WriteBoxHeader(writer, meanSize, "mean");
        BigEndianWriter.WriteFullBoxPrefix(writer, 0, 0);
        writer.Write(domainBytes);

        // name (FullBox: version=0, flags=0, then field name string)
        BigEndianWriter.WriteBoxHeader(writer, nameSize, "name");
        BigEndianWriter.WriteFullBoxPrefix(writer, 0, 0);
        writer.Write(nameBytes);

        // data box: version=0, type indicator=1 (UTF-8), locale=0
        BigEndianWriter.WriteBoxHeader(writer, dataSize, "data");
        writer.Write((byte)0);           // version
        writer.Write((byte)0);           // type indicator high byte
        writer.Write((byte)0);           // type indicator mid byte
        writer.Write((byte)1);           // type indicator low byte = 1 (UTF-8)
        writer.Write((byte)0);           // locale bytes (4 x 0)
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(valueBytes);
    }

    /// <summary>
    /// Calculates the byte size a <c>----</c> atom will occupy for the given inputs.
    /// Useful for pre-calculating size deltas before writing.
    /// </summary>
    /// <param name="domain">The reverse-domain namespace string.</param>
    /// <param name="fieldName">The field name string.</param>
    /// <param name="value">The UTF-8 value string.</param>
    /// <returns>Total byte count of the complete <c>----</c> atom.</returns>
    public static uint CalculateSize(string domain, string fieldName, string value)
    {
        uint meanSize = (uint)(12 + Encoding.UTF8.GetByteCount(domain));
        uint nameSize = (uint)(12 + Encoding.UTF8.GetByteCount(fieldName));
        uint dataSize = (uint)(DataOverhead + Encoding.UTF8.GetByteCount(value));
        return 8 + meanSize + nameSize + dataSize;
    }
}
