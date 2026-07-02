using ClipMetaCore.Mp4;
using ClipMetaCore.Read;
using ClipMetaCore.Schema;

namespace ClipMetaScribe.Tests;

[TestClass]
public class BoxTreeJsonTests
{
    private static BoxTree SampleTree()
    {
        var ftyp = new BoxNode { Type = "ftyp", Size = 32, FileOffset = 0, HeaderSize = 8, DisplayValue = "\"isom\"" };
        var atom = new BoxNode { Type = "----", EditableKey = ClipMetaSchema.Domain + ":game", DisplayValue = "\"TF2\"", IsEditable = true };
        var root = new BoxNode { Type = "ROOT", Children = { ftyp, atom } };
        return BoxTreeMapper.Map(root, @"C:\clips\a.mp4", 32);
    }

    [TestMethod]
    public void ToJson_UsesCamelCaseKeys()
    {
        string json = BoxTreeJson.ToJson(SampleTree());
        StringAssert.Contains(json, "\"fileSize\":");
        StringAssert.Contains(json, "\"isFullBox\":");
        StringAssert.Contains(json, "\"isClipmetaContainer\":");
        Assert.IsFalse(json.Contains("\"FileSize\""), "keys must be camelCase, not PascalCase");
    }

    [TestMethod]
    public void ToJson_SerializesCategoryAsStringName()
    {
        string json = BoxTreeJson.ToJson(SampleTree());
        StringAssert.Contains(json, "\"category\":\"Header\"");
        StringAssert.Contains(json, "\"category\":\"EditableMeta\"");
    }

    [TestMethod]
    public void ToJson_OmitsNullDisplayValueAndUnquotesPresentOne()
    {
        var leaf = new BoxNode { Type = "moov" }; // no DisplayValue
        var tree = BoxTreeMapper.Map(new BoxNode { Type = "ROOT", Children = { leaf } }, "p", 0);
        string json = BoxTreeJson.ToJson(tree);
        Assert.IsFalse(json.Contains("displayValue"), "null displayValue must be omitted");
        // and a present one is unquoted:
        StringAssert.Contains(BoxTreeJson.ToJson(SampleTree()), "\"displayValue\":\"isom\"");
    }

    [TestMethod]
    public void ToJson_DoesNotEmitContentOffset()
    {
        Assert.IsFalse(BoxTreeJson.ToJson(SampleTree()).Contains("contentOffset"));
    }

    [TestMethod]
    public void ToJsonObject_ToJsonString_EqualsToJson_ByteIdentical()
    {
        BoxTree tree = SampleTree();
        Assert.AreEqual(BoxTreeJson.ToJson(tree), BoxTreeJson.ToJsonObject(tree).ToJsonString());
    }
}
