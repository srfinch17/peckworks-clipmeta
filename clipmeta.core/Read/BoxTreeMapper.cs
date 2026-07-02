using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;

namespace ClipMetaCore.Read;

/// <summary>Maps a parsed <see cref="BoxNode"/> tree into the serializable <see cref="BoxTree"/> DTO. Pure; no IO.</summary>
public static class BoxTreeMapper
{
    /// <summary>Builds a <see cref="BoxTree"/> from a parsed root, a resolved path, and the file size.</summary>
    /// <param name="root">The root node from <see cref="Mp4Parser.ParseFile"/>.</param>
    /// <param name="resolvedPath">Absolute path to report in the output.</param>
    /// <param name="fileSize">File size in bytes.</param>
    public static BoxTree Map(BoxNode root, string resolvedPath, long fileSize)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new BoxTree
        {
            Path = resolvedPath,
            FileSize = fileSize,
            Boxes = root.Children.Select(MapNode).ToList(),
        };
    }

    private static BoxTreeNode MapNode(BoxNode node) => new()
    {
        Type = node.Type,
        Offset = node.FileOffset,
        Size = node.Size,
        HeaderSize = node.HeaderSize,
        IsFullBox = node.IsFullBox,
        Version = node.Version,
        Flags = node.Flags,
        FriendlyName = MetadataKeys.GetName(node.Type),
        Category = node.IsEditable ? BoxCategory.EditableMeta : MetadataKeys.GetCategory(node.Type),
        DisplayValue = node.DisplayValue is null ? null : ClipMetaReader.UnquoteDisplayValue(node.DisplayValue),
        IsEditable = node.IsEditable,
        EditableKey = node.EditableKey,
        WasClamped = node.WasClamped,
        HasReliableOffsets = node.HasReliableOffsets,
        IsClipmetaContainer = ClipMetaSchema.IsClipmetaFreeformAtom(node),
        Children = node.Children.Select(MapNode).ToList(),
    };
}
