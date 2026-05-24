using ClipMetaCore.Mp4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClipMetaView.Tests;

[TestClass]
public class BigEndianReaderTests
{
    [TestMethod]
    public void ReadUInt16_BigEndianBytes_ReturnsCorrectValue()
    {
        byte[] bytes = { 0x01, 0x00 }; // big-endian 256
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        ushort result = BigEndianReader.ReadUInt16(reader);

        Assert.AreEqual((ushort)256, result);
    }

    [TestMethod]
    public void ReadUInt16_MaxValue_ReturnsCorrectValue()
    {
        byte[] bytes = { 0xFF, 0xFF };
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        Assert.AreEqual(ushort.MaxValue, BigEndianReader.ReadUInt16(reader));
    }

    [TestMethod]
    public void ReadUInt32_BigEndianBytes_ReturnsCorrectValue()
    {
        byte[] bytes = { 0x00, 0x00, 0x00, 0x20 }; // big-endian 32
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        uint result = BigEndianReader.ReadUInt32(reader);

        Assert.AreEqual(32u, result);
    }

    [TestMethod]
    public void ReadUInt32_BigEndianValue_ReturnsCorrectValue()
    {
        byte[] bytes = { 0x00, 0x01, 0x00, 0x00 }; // big-endian 65536
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        Assert.AreEqual(65536u, BigEndianReader.ReadUInt32(reader));
    }

    [TestMethod]
    public void ReadUInt64_BigEndianBytes_ReturnsCorrectValue()
    {
        byte[] bytes = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20 }; // big-endian 32
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        ulong result = BigEndianReader.ReadUInt64(reader);

        Assert.AreEqual(32UL, result);
    }

    [TestMethod]
    public void ReadFourCC_AsciiBytes_ReturnsCorrectString()
    {
        byte[] bytes = { 0x66, 0x74, 0x79, 0x70 }; // 'ftyp'
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        string result = BigEndianReader.ReadFourCC(reader);

        Assert.AreEqual("ftyp", result);
    }

    [TestMethod]
    public void ReadFourCC_WithCopyrightByte_PreservesByte0xA9()
    {
        // ©nam = 0xA9 'n' 'a' 'm'
        byte[] bytes = { 0xA9, 0x6E, 0x61, 0x6D };
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        string result = BigEndianReader.ReadFourCC(reader);

        Assert.AreEqual("©nam", result);
    }

    [TestMethod]
    public void ReadBoxHeader_NormalSize_ReturnsCorrectHeader()
    {
        // ftyp box, size 32
        byte[] bytes = { 0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70 };
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        var header = BigEndianReader.ReadBoxHeader(reader);

        Assert.AreEqual(32UL, header.Size);
        Assert.AreEqual("ftyp", header.Type);
        Assert.AreEqual(8, header.HeaderSize);
    }

    [TestMethod]
    public void ReadBoxHeader_ExtendedSize_ReturnsCorrectSizeAndHeaderSize()
    {
        // size == 1 (extended), type 'mdat', extended size = 32
        byte[] bytes = {
            0x00, 0x00, 0x00, 0x01,              // size = 1
            0x6D, 0x64, 0x61, 0x74,              // 'mdat'
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20 // extended size = 32
        };
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        var header = BigEndianReader.ReadBoxHeader(reader);

        Assert.AreEqual(32UL, header.Size);
        Assert.AreEqual("mdat", header.Type);
        Assert.AreEqual(16, header.HeaderSize);
    }

    [TestMethod]
    public void ReadBoxHeader_SizeZero_ComputesSizeFromStreamLength()
    {
        // size == 0 means "extends to EOF"; stream is 8 bytes
        byte[] bytes = { 0x00, 0x00, 0x00, 0x00, 0x66, 0x74, 0x79, 0x70 }; // size=0, 'ftyp'
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);

        var header = BigEndianReader.ReadBoxHeader(reader);

        Assert.AreEqual(8UL, header.Size); // 8 bytes total stream length - 0 box start
        Assert.AreEqual("ftyp", header.Type);
        Assert.AreEqual(8, header.HeaderSize);
    }
}
