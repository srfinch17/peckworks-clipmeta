using ClipMetaCore.Mp4;
using System.Text;

namespace ClipMetaScribe.Tests;

[TestClass]
public class BigEndianWriterTests
{
    [TestMethod]
    public void WriteUInt16_ThenReadBack_Matches()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteUInt16(bw, 0x1234);
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        Assert.AreEqual((ushort)0x1234, BigEndianReader.ReadUInt16(br));
    }

    [TestMethod]
    public void WriteUInt32_ThenReadBack_Matches()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteUInt32(bw, 0x00B4AF20);
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        Assert.AreEqual(0x00B4AF20u, BigEndianReader.ReadUInt32(br));
    }

    [TestMethod]
    public void WriteUInt64_ThenReadBack_Matches()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteUInt64(bw, 0x0000000100000020UL);
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        Assert.AreEqual(0x0000000100000020UL, BigEndianReader.ReadUInt64(br));
    }

    [TestMethod]
    public void WriteFourCC_ThenReadBack_Matches()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteFourCC(bw, "moov");
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        Assert.AreEqual("moov", BigEndianReader.ReadFourCC(br));
    }

    [TestMethod]
    public void WriteFourCC_CopyrightPrefix_RoundTrips()
    {
        // © = 0xA9. FourCC must use Latin-1 so the byte round-trips.
        string fourCC = "©nam";
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteFourCC(bw, fourCC);
        ms.Position = 0;
        using var br = new BinaryReader(ms);
        Assert.AreEqual(fourCC, BigEndianReader.ReadFourCC(br));
    }

    [TestMethod]
    public void WriteUInt32_BigEndianByteOrder_CorrectBytes()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        BigEndianWriter.WriteUInt32(bw, 0x00000020);
        byte[] bytes = ms.ToArray();
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x00, 0x00, 0x20 }, bytes);
    }
}
