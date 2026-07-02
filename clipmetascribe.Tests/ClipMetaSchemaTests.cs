using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaScribe.Tests;

[TestClass]
public class ClipMetaSchemaTests
{
    [TestMethod]
    public void IsClipmetaFreeformAtom_TrueForDomainPrefixedFreeformAtom()
    {
        var node = new BoxNode { Type = "----", EditableKey = ClipMetaSchema.Domain + ":game" };
        Assert.IsTrue(ClipMetaSchema.IsClipmetaFreeformAtom(node));
    }

    [TestMethod]
    public void IsClipmetaFreeformAtom_FalseForForeignFreeformAtom()
    {
        var node = new BoxNode { Type = "----", EditableKey = "com.apple.iTunes:CDDB1" };
        Assert.IsFalse(ClipMetaSchema.IsClipmetaFreeformAtom(node));
    }

    [TestMethod]
    public void IsClipmetaFreeformAtom_FalseForNonFreeformNode()
    {
        var node = new BoxNode { Type = "©nam", EditableKey = ClipMetaSchema.Domain + ":game" };
        Assert.IsFalse(ClipMetaSchema.IsClipmetaFreeformAtom(node));
    }

    [TestMethod]
    public void IsClipmetaFreeformAtom_FalseWhenEditableKeyNull()
    {
        var node = new BoxNode { Type = "----", EditableKey = null };
        Assert.IsFalse(ClipMetaSchema.IsClipmetaFreeformAtom(node));
    }
}
