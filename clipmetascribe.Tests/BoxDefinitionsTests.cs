using ClipMetaCore.Mp4;

namespace ClipMetaScribe.Tests;

[TestClass]
public class BoxDefinitionsTests
{
    [TestMethod]
    public void GetDefinition_KnownStructuralType_HasNameCategoryAndDescription()
    {
        BoxDefinition d = BoxDefinitions.GetDefinition("moov");
        Assert.AreEqual("Movie", d.FriendlyName);
        Assert.AreEqual(BoxCategory.Structural, d.Category);
        Assert.IsFalse(string.IsNullOrEmpty(d.Description), "known types should carry a description");
    }

    [TestMethod]
    public void GetDefinition_ItunesField_IsEditableMeta()
    {
        BoxDefinition d = BoxDefinitions.GetDefinition("©nam");
        Assert.AreEqual("Title", d.FriendlyName);
        Assert.AreEqual(BoxCategory.EditableMeta, d.Category);
    }

    [TestMethod]
    public void GetDefinition_UnknownType_FallsBackToTypeNameAndNoDescription()
    {
        BoxDefinition d = BoxDefinitions.GetDefinition("zzzz");
        Assert.AreEqual("zzzz", d.FriendlyName);
        Assert.IsNull(d.Description);
    }

    [TestMethod]
    public void AllDefinitions_CoversEveryKnownMetadataKey()
    {
        var all = BoxDefinitions.AllDefinitions();
        foreach (string type in MetadataKeys.All.Keys)
            Assert.IsTrue(all.ContainsKey(type), $"missing definition for {type}");
    }
}
