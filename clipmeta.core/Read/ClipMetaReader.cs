using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Read;

/// <summary>Reads ClipMeta metadata fields from a parsed MP4 box tree.</summary>
public static class ClipMetaReader
{
    private static readonly string DomainPrefix = ClipMetaSchema.Domain + ":";

    /// <summary>
    /// Walks <paramref name="root"/> and returns all <c>com.peckworkslab.clipmeta</c> freeform atoms
    /// found in any <c>ilst</c> box, in document order.
    /// </summary>
    /// <param name="root">The root node returned by <see cref="Mp4Parser.ParseFile"/>.</param>
    /// <returns>
    /// A list of (Field, Value) pairs where Field is the bare field name (e.g. "game")
    /// and Value is the atom's display string.
    /// </returns>
    public static IReadOnlyList<(string Field, string Value)> GetFields(BoxNode root)
    {
        var result = new List<(string, string)>();
        CollectFromNode(root, result);
        return result;
    }

    private static void CollectFromNode(BoxNode node, List<(string, string)> result)
    {
        if (node.Type == "ilst")
        {
            foreach (var child in node.Children)
            {
                if (child.Type == "----" &&
                    child.EditableKey?.StartsWith(DomainPrefix, StringComparison.Ordinal) == true &&
                    child.DisplayValue != null)
                {
                    string field = child.EditableKey[DomainPrefix.Length..];
                    string value = UnquoteDisplayValue(child.DisplayValue);
                    result.Add((field, value));
                }
            }
            return; // ilst never contains nested ilst boxes
        }
        foreach (var child in node.Children)
            CollectFromNode(child, result);
    }

    private static string UnquoteDisplayValue(string displayValue)
    {
        // The parser wraps UTF-8 string values in quotes: "My String"
        // Strip them if present, but leave other display values untouched (e.g. "[JPEG image, ...]").
        if (displayValue.Length >= 2 &&
            displayValue[0] == '"' &&
            displayValue[displayValue.Length - 1] == '"')
        {
            return displayValue[1..^1];
        }
        return displayValue;
    }
}
