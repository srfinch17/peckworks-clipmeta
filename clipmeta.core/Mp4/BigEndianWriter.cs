using System.Text;

namespace ClipMetaCore.Mp4;

/// <summary>
/// Static utility for writing big-endian integers and MP4 structural types to a <see cref="BinaryWriter"/>.
/// Mirrors <see cref="BigEndianReader"/> — every write is the exact inverse of a read.
/// </summary>
public static class BigEndianWriter
{
    /// <summary>Writes a 2-byte unsigned integer in big-endian order.</summary>
    public static void WriteUInt16(BinaryWriter writer, ushort value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        writer.Write(bytes);
    }

    /// <summary>Writes a 4-byte unsigned integer in big-endian order.</summary>
    public static void WriteUInt32(BinaryWriter writer, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        writer.Write(bytes);
    }

    /// <summary>Writes an 8-byte unsigned integer in big-endian order.</summary>
    public static void WriteUInt64(BinaryWriter writer, ulong value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        writer.Write(bytes);
    }

    /// <summary>
    /// Writes a 4-byte FourCC string using Latin-1 encoding so the © prefix (0xA9) round-trips.
    /// </summary>
    public static void WriteFourCC(BinaryWriter writer, string fourCC)
    {
        writer.Write(Encoding.Latin1.GetBytes(fourCC.PadRight(4)[..4]));
    }

    /// <summary>
    /// Writes an MP4 box header: 4-byte size (big-endian) + 4-byte FourCC.
    /// </summary>
    public static void WriteBoxHeader(BinaryWriter writer, uint size, string type)
    {
        WriteUInt32(writer, size);
        WriteFourCC(writer, type);
    }

    /// <summary>
    /// Writes a FullBox prefix: 1-byte version + 3-byte flags (big-endian).
    /// Always call this immediately after WriteBoxHeader for FullBox types.
    /// </summary>
    public static void WriteFullBoxPrefix(BinaryWriter writer, byte version, uint flags)
    {
        writer.Write(version);
        writer.Write((byte)(flags >> 16));
        writer.Write((byte)(flags >> 8));
        writer.Write((byte)flags);
    }
}
