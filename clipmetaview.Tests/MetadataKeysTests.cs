using ClipMetaCore.Mp4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClipMetaView.Tests;

[TestClass]
public class MetadataKeysTests
{
    [TestMethod]
    public void GetName_KnownFourCC_ReturnsCorrectFriendlyName()
    {
        Assert.AreEqual("Title",       MetadataKeys.GetName("©nam"));
        Assert.AreEqual("Artist",      MetadataKeys.GetName("©ART"));
        Assert.AreEqual("Album",       MetadataKeys.GetName("©alb"));
        Assert.AreEqual("Year",        MetadataKeys.GetName("©day"));
        Assert.AreEqual("Comment",     MetadataKeys.GetName("©cmt"));
        Assert.AreEqual("Genre",       MetadataKeys.GetName("©gen"));
        Assert.AreEqual("Track Number",MetadataKeys.GetName("trkn"));
        Assert.AreEqual("Description", MetadataKeys.GetName("desc"));
        Assert.AreEqual("Cover Art",   MetadataKeys.GetName("covr"));
    }

    [TestMethod]
    public void GetName_StructuralBoxes_ReturnFriendlyNames()
    {
        Assert.AreEqual("Movie",          MetadataKeys.GetName("moov"));
        Assert.AreEqual("Track",          MetadataKeys.GetName("trak"));
        Assert.AreEqual("Media Data",     MetadataKeys.GetName("mdat"));
        Assert.AreEqual("File Type",      MetadataKeys.GetName("ftyp"));
        Assert.AreEqual("User Data",      MetadataKeys.GetName("udta"));
        Assert.AreEqual("Metadata",       MetadataKeys.GetName("meta"));
        Assert.AreEqual("Metadata Items", MetadataKeys.GetName("ilst"));
    }

    [TestMethod]
    public void GetName_UnknownFourCC_ReturnsRawFourCC()
    {
        Assert.AreEqual("unkn", MetadataKeys.GetName("unkn"));
        Assert.AreEqual("xxxx", MetadataKeys.GetName("xxxx"));
    }

    [TestMethod]
    public void IsKnown_KnownFourCC_ReturnsTrue()
    {
        Assert.IsTrue(MetadataKeys.IsKnown("moov"));
        Assert.IsTrue(MetadataKeys.IsKnown("©nam"));
        Assert.IsTrue(MetadataKeys.IsKnown("ftyp"));
    }

    [TestMethod]
    public void IsKnown_UnknownFourCC_ReturnsFalse()
    {
        Assert.IsFalse(MetadataKeys.IsKnown("zzzz"));
        Assert.IsFalse(MetadataKeys.IsKnown("unkn"));
    }

    [TestMethod]
    public void All_ContainsExpectedMinimumEntries()
    {
        Assert.IsTrue(MetadataKeys.All.Count >= 10, "Expected at least 10 known FourCC entries");
    }
}
