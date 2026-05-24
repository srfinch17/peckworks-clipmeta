using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;
using System.Text;

namespace ClipMetaScribe.Tests;

[TestClass]
public class FreeformAtomWriterTests
{
    private static byte[] WriteFreeform(string fieldName, string value)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        FreeformAtomWriter.Write(bw, ClipMetaSchema.Domain, fieldName, value);
        return ms.ToArray();
    }

    [TestMethod]
    public void Write_OuterBox_HasDashDashDashDashFourCC()
    {
        byte[] bytes = WriteFreeform("tags", "headshot");
        string fourCC = Encoding.Latin1.GetString(bytes, 4, 4);
        Assert.AreEqual("----", fourCC);
    }

    [TestMethod]
    public void Write_OuterSize_MatchesActualLength()
    {
        byte[] bytes = WriteFreeform("tags", "headshot");
        uint size = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        Assert.AreEqual((uint)bytes.Length, size, "Outer ---- box size must equal byte array length.");
    }

    [TestMethod]
    public void Write_MeanBox_HasFullBoxPrefix()
    {
        byte[] bytes = WriteFreeform("tags", "headshot");

        // After 8 bytes (---- header), mean box starts.
        // mean box: 4 size + 4 "mean" + 4 version+flags + domain bytes
        int meanStart = 8;
        uint meanSize = (uint)((bytes[meanStart] << 24) | (bytes[meanStart+1] << 16)
                               | (bytes[meanStart+2] << 8) | bytes[meanStart+3]);
        string meanFourCC = Encoding.Latin1.GetString(bytes, meanStart + 4, 4);
        byte meanVersion = bytes[meanStart + 8];
        byte meanFlag1 = bytes[meanStart + 9];
        byte meanFlag2 = bytes[meanStart + 10];
        byte meanFlag3 = bytes[meanStart + 11];

        Assert.AreEqual("mean", meanFourCC, "First child must be 'mean'");
        Assert.AreEqual(0, meanVersion, "mean version must be 0 (FullBox)");
        Assert.AreEqual(0, meanFlag1 | meanFlag2 | meanFlag3, "mean flags must be 0 (FullBox)");

        // Domain content starts at meanStart+12; verify it matches
        int domainLen = (int)meanSize - 12; // 4 size + 4 type + 4 version+flags = 12 header bytes
        string domain = Encoding.UTF8.GetString(bytes, meanStart + 12, domainLen);
        Assert.AreEqual(ClipMetaSchema.Domain, domain);
    }

    [TestMethod]
    public void Write_NameBox_HasFullBoxPrefix()
    {
        byte[] bytes = WriteFreeform("tags", "headshot");

        // Locate name box: starts after mean box
        int meanStart = 8;
        uint meanSize = (uint)((bytes[meanStart] << 24) | (bytes[meanStart+1] << 16)
                               | (bytes[meanStart+2] << 8) | bytes[meanStart+3]);
        int nameStart = meanStart + (int)meanSize;

        string nameFourCC = Encoding.Latin1.GetString(bytes, nameStart + 4, 4);
        byte nameVersion = bytes[nameStart + 8];

        Assert.AreEqual("name", nameFourCC, "Second child must be 'name'");
        Assert.AreEqual(0, nameVersion, "name version must be 0 (FullBox)");

        uint nameSize = (uint)((bytes[nameStart] << 24) | (bytes[nameStart+1] << 16)
                               | (bytes[nameStart+2] << 8) | bytes[nameStart+3]);
        int fieldLen = (int)nameSize - 12;
        string field = Encoding.UTF8.GetString(bytes, nameStart + 12, fieldLen);
        Assert.AreEqual("tags", field);
    }

    [TestMethod]
    public void Write_DataBox_HasCorrectTypeIndicatorAndValue()
    {
        byte[] bytes = WriteFreeform("tags", "headshot");

        // Parse to data box position
        int meanStart = 8;
        uint meanSize = (uint)((bytes[meanStart] << 24) | (bytes[meanStart+1] << 16)
                               | (bytes[meanStart+2] << 8) | bytes[meanStart+3]);
        int nameStart = meanStart + (int)meanSize;
        uint nameSize = (uint)((bytes[nameStart] << 24) | (bytes[nameStart+1] << 16)
                               | (bytes[nameStart+2] << 8) | bytes[nameStart+3]);
        int dataStart = nameStart + (int)nameSize;

        string dataFourCC = Encoding.Latin1.GetString(bytes, dataStart + 4, 4);
        // data payload: 1 version + 3 type_indicator + 4 locale + value
        byte version = bytes[dataStart + 8];
        int typeIndicator = (bytes[dataStart + 9] << 16) | (bytes[dataStart + 10] << 8) | bytes[dataStart + 11];

        Assert.AreEqual("data", dataFourCC);
        Assert.AreEqual(0, version);
        Assert.AreEqual(1, typeIndicator, "Type indicator 1 = UTF-8 text");

        uint dataSize = (uint)((bytes[dataStart] << 24) | (bytes[dataStart+1] << 16)
                               | (bytes[dataStart+2] << 8) | bytes[dataStart+3]);
        // value starts at dataStart + 8 (header) + 4 (version+type) + 4 (locale) = dataStart + 16
        int valueLen = (int)dataSize - 16;
        string value = Encoding.UTF8.GetString(bytes, dataStart + 16, valueLen);
        Assert.AreEqual("headshot", value);
    }

    [TestMethod]
    public void Write_ParseBack_AtomReadableByMp4Parser()
    {
        // Write only the ---- atom bytes (no ilst wrapper) and call ParseBoxes with inIlst:true.
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        FreeformAtomWriter.Write(bw, ClipMetaSchema.Domain, "tags", "headshot");
        ms.Position = 0;

        using var reader = new BinaryReader(ms, System.Text.Encoding.Latin1, leaveOpen: true);
        var nodes = ClipMetaCore.Mp4.Mp4Parser.ParseBoxes(reader, 0, ms.Length, inIlst: true);

        Assert.AreEqual(1, nodes.Count, "Expected exactly one item");
        var node = nodes[0];
        Assert.AreEqual("----", node.Type);
        Assert.AreEqual($"{ClipMetaSchema.Domain}:tags", node.EditableKey);
        Assert.IsTrue(node.IsEditable);
        Assert.IsNotNull(node.DisplayValue);
        Assert.IsTrue(node.DisplayValue!.Contains("headshot"), $"DisplayValue was: {node.DisplayValue}");
    }
}
