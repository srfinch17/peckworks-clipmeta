using System.Text;

namespace ClipMetaCore.Mp4;

/// <summary>
/// Static utility for reading big-endian integers and MP4 structural types from a <see cref="BinaryReader"/>.
/// All multi-byte reads reverse byte order on little-endian hosts so the caller always gets host-native values.
/// </summary>
public static class BigEndianReader
{
    /// <summary>Reads a 2-byte unsigned integer from the stream in big-endian order.</summary>
    public static ushort ReadUInt16(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(2);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt16(bytes, 0);
    }

    /// <summary>Reads a 4-byte unsigned integer from the stream in big-endian order.</summary>
    public static uint ReadUInt32(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    /// <summary>Reads an 8-byte unsigned integer from the stream in big-endian order.</summary>
    public static ulong ReadUInt64(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(8);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt64(bytes, 0);
    }

    /// <summary>
    /// Reads 4 bytes and returns them as a Latin-1 string (ISO-8859-1).
    /// Latin-1 is required so that the 0xA9 © prefix byte in iTunes metadata FourCCs
    /// round-trips without corruption.
    /// </summary>
    public static string ReadFourCC(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>
    /// Reads an MP4 box header from the current stream position, handling the three size cases:
    /// <list type="bullet">
    ///   <item>Normal: 32-bit size field contains the box's total byte count.</item>
    ///   <item>Extended (size == 1): a subsequent 8-byte field holds the real 64-bit size.</item>
    ///   <item>To-EOF (size == 0): the box extends from its start to the end of the stream.</item>
    /// </list>
    /// </summary>
    /// <param name="reader">Positioned at the first byte of the box header.</param>
    /// <returns>A <see cref="BoxHeader"/> with <c>Size</c> always reflecting the true total byte count.</returns>
    public static BoxHeader ReadBoxHeader(BinaryReader reader)
    {
        long boxStart = reader.BaseStream.Position;
        uint size32 = ReadUInt32(reader);
        string type = ReadFourCC(reader);

        ulong size;
        int headerSize;

        if (size32 == SizeExtended)
        {
            size = ReadUInt64(reader);
            headerSize = ExtendedHeaderSize;
        }
        else if (size32 == SizeToEof)
        {
            size = (ulong)(reader.BaseStream.Length - boxStart);
            headerSize = NormalHeaderSize;
        }
        else
        {
            size = size32;
            headerSize = NormalHeaderSize;
        }

        return new BoxHeader(size, type, headerSize);
    }

    /// <summary>
    /// Reads an MP4 FullBox header: a standard box header followed by a version byte and a 24-bit flags field.
    /// </summary>
    /// <param name="reader">Positioned at the first byte of the box header.</param>
    /// <returns>A <see cref="FullBoxHeader"/> carrying both the base header and the version/flags.</returns>
    public static FullBoxHeader ReadFullBoxHeader(BinaryReader reader)
    {
        var box = ReadBoxHeader(reader);
        byte version = reader.ReadByte();
        byte f1 = reader.ReadByte(), f2 = reader.ReadByte(), f3 = reader.ReadByte();
        uint flags = (uint)((f1 << 16) | (f2 << 8) | f3);
        return new FullBoxHeader(box, version, flags);
    }

    private const uint SizeExtended = 1;
    private const uint SizeToEof = 0;
    private const int NormalHeaderSize = 8;
    private const int ExtendedHeaderSize = 16;
}
