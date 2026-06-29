using ClipMetaCore.Schema;
using ClipMetaCore.Write;

namespace ClipMetaScribe.Tests;

/// <summary>
/// Unit tests for <see cref="MetadataPreview.Predict"/>, the dry-run preview's predicted-field
/// computation. Pure, clip-less. The end-to-end "preview == real write" guarantee is pinned by
/// WriteToolsTests; these cover the per-rule behavior (set/append/delete/clear-all/normalization).
/// </summary>
[TestClass]
public class MetadataPreviewTests
{
    private static (string, string)[] Predict(
        (string Field, string Value)[] current, Action<MetadataMutation> build)
    {
        var m = new MetadataMutation();
        build(m);
        return MetadataPreview.Predict(current, m).Select(f => (f.Field, f.Value)).ToArray();
    }

    private static string Atom(string field) => ClipMetaSchema.AtomName(field);

    [TestMethod]
    public void Set_ReplacesExistingValueInPlace()
    {
        var result = Predict(
            new[] { ("tags", "a|b") },
            m => m.SetFields[Atom("tags")] = "c");

        CollectionAssert.AreEqual(new[] { ("tags", "c") }, result);
    }

    [TestMethod]
    public void Set_NewField_AppendsAtEnd()
    {
        var result = Predict(
            new[] { ("game", "TF2") },
            m => m.SetFields[Atom("players")] = "chuck");

        CollectionAssert.AreEqual(new[] { ("game", "TF2"), ("players", "chuck") }, result);
    }

    [TestMethod]
    public void Set_EmptyValue_DeletesField()
    {
        var result = Predict(
            new[] { ("tags", "a"), ("game", "TF2") },
            m => m.SetFields[Atom("tags")] = "");

        CollectionAssert.AreEqual(new[] { ("game", "TF2") }, result);
    }

    [TestMethod]
    public void Delete_RemovesField()
    {
        var result = Predict(
            new[] { ("tags", "a"), ("game", "TF2") },
            m => m.DeleteFields.Add(Atom("tags")));

        CollectionAssert.AreEqual(new[] { ("game", "TF2") }, result);
    }

    [TestMethod]
    public void Append_Notes_JoinsAsProse_CasePreserved()
    {
        var result = Predict(
            new[] { ("notes", "Chuck wins") },
            m => m.AppendFields[Atom("notes")] = "raccoon ambush");

        CollectionAssert.AreEqual(new[] { ("notes", "Chuck wins. raccoon ambush") }, result);
    }

    [TestMethod]
    public void Append_Tags_PipeMergesAndDedups()
    {
        var result = Predict(
            new[] { ("tags", "a|b") },
            m => m.AppendFields[Atom("tags")] = "b|c");

        CollectionAssert.AreEqual(new[] { ("tags", "a|b|c") }, result);
    }

    [TestMethod]
    public void Append_AbsentField_BecomesTheValue()
    {
        var result = Predict(
            Array.Empty<(string, string)>(),
            m => m.AppendFields[Atom("players")] = "chuck");

        CollectionAssert.AreEqual(new[] { ("players", "chuck") }, result);
    }

    [TestMethod]
    public void ClearAll_RemovesEverything()
    {
        var result = Predict(
            new[] { ("tags", "a"), ("game", "TF2") },
            m => m.ClearAll = true);

        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void ClearAll_WithSet_KeepsOnlyTheSet()
    {
        var result = Predict(
            new[] { ("tags", "a"), ("game", "TF2") },
            m => { m.ClearAll = true; m.SetFields[Atom("game")] = "Teardown"; });

        CollectionAssert.AreEqual(new[] { ("game", "Teardown") }, result);
    }

    [TestMethod]
    public void Set_NormalizesValue_RatingClampedTagsLowercasedDeduped()
    {
        var result = Predict(
            Array.Empty<(string, string)>(),
            m =>
            {
                m.SetFields[Atom("rating")] = "9";
                m.SetFields[Atom("tags")] = "B|A|a";
            });

        CollectionAssert.AreEqual(new[] { ("rating", "5"), ("tags", "b|a") }, result);
    }
}
