using ClipMetaCore.Mp4;

namespace ClipMetaCore.Read;

/// <summary>One node in the structured box tree returned by <see cref="BoxTreeMapper"/>.</summary>
public sealed class BoxTreeNode
{
    /// <summary>Four-character box type (FourCC).</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Absolute byte offset of the box's first byte in the file.</summary>
    public long Offset { get; init; }

    /// <summary>Total box size in bytes, including the header.</summary>
    public ulong Size { get; init; }

    /// <summary>Header byte count: 8 for a standard box, 16 with an extended-size field.</summary>
    public int HeaderSize { get; init; }

    /// <summary>True when the box carries an ISO FullBox version byte and 24-bit flags after the header.</summary>
    public bool IsFullBox { get; init; }

    /// <summary>FullBox version, or 0.</summary>
    public byte Version { get; init; }

    /// <summary>FullBox flags, or 0.</summary>
    public uint Flags { get; init; }

    /// <summary>Friendly name for the type, or the raw FourCC when unknown.</summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>Semantic category; editable metadata fields report <see cref="BoxCategory.EditableMeta"/>.</summary>
    public BoxCategory Category { get; init; }

    /// <summary>Decoded human-readable value for leaf metadata boxes (unquoted), or null.</summary>
    public string? DisplayValue { get; init; }

    /// <summary>True for metadata items the tagger can add, update, or delete.</summary>
    public bool IsEditable { get; init; }

    /// <summary>Raw editable key for editable items, or null.</summary>
    public string? EditableKey { get; init; }

    /// <summary>True when the box's on-disk size claimed more bytes than its container held and was clamped.</summary>
    public bool WasClamped { get; init; }

    /// <summary>False when Offset/Size are approximate (e.g. Xtra child items) and must not be byte-trusted.</summary>
    public bool HasReliableOffsets { get; init; } = true;

    /// <summary>True when this is a clipmeta-namespaced freeform ("----") atom.</summary>
    public bool IsClipmetaContainer { get; init; }

    /// <summary>Child boxes; empty for leaves (including mdat, which is never expanded).</summary>
    public IReadOnlyList<BoxTreeNode> Children { get; init; } = [];
}

/// <summary>Structured box tree for one MP4 file.</summary>
public sealed class BoxTree
{
    /// <summary>Resolved absolute path of the parsed file.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>Top-level boxes, in file order.</summary>
    public IReadOnlyList<BoxTreeNode> Boxes { get; init; } = [];
}
