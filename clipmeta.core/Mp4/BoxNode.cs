namespace ClipMetaCore.Mp4;

/// <summary>
/// In-memory representation of a parsed MP4 box/atom. Forms a tree whose
/// leaves and containers mirror the file's box hierarchy.
/// </summary>
public class BoxNode
{
    /// <summary>Four-character box type (FourCC), Latin-1 decoded so the © prefix (0xA9) round-trips correctly.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Total size of the box in bytes, including the header. Never zero after parsing.</summary>
    public ulong Size { get; init; }

    /// <summary>Absolute byte offset of the first byte of this box within the source file.</summary>
    public long FileOffset { get; init; }

    /// <summary>Number of header bytes consumed: 8 for standard boxes, 16 when an extended-size field is present.</summary>
    public int HeaderSize { get; init; }

    /// <summary>Byte offset of the first content byte, accounting for both the standard box header and any FullBox version+flags overhead.</summary>
    public long ContentOffset => FileOffset + HeaderSize + (IsFullBox ? 4 : 0);

    /// <summary>False for nodes whose <see cref="FileOffset"/> and <see cref="Size"/> are approximate (e.g. Xtra child items); the write engine must not use these for in-place overwrites.</summary>
    public bool HasReliableOffsets { get; init; } = true;

    /// <summary>
    /// True when the box's on-disk size field claimed more bytes than its container (or the file)
    /// actually holds, and the parser clamped <see cref="Size"/> to the available range.
    /// Typical cause: a truncated download. The viewer tolerates this; the write engine refuses
    /// to rewrite such files because the box header on disk is lying about its own length.
    /// </summary>
    public bool WasClamped { get; init; }

    /// <summary>True when this box carries a version byte and 24-bit flags field after the standard box header.</summary>
    public bool IsFullBox { get; init; }

    /// <summary>Version field present in FullBox types; 0 for non-FullBox nodes.</summary>
    public byte Version { get; init; }

    /// <summary>Flags field present in FullBox types; 0 for non-FullBox nodes.</summary>
    public uint Flags { get; init; }

    /// <summary>Child boxes parsed from this box's payload. Always non-null; empty for leaf boxes.</summary>
    public List<BoxNode> Children { get; init; } = new();

    /// <summary>Human-readable value extracted from metadata leaf nodes, or null when not applicable.</summary>
    public string? DisplayValue { get; set; }

    /// <summary>True for metadata items inside the ilst box that clipmetascribe can add, update, or delete.</summary>
    public bool IsEditable { get; set; }

    /// <summary>The raw FourCC key for editable items; used by clipmetascribe to locate the target field.</summary>
    public string? EditableKey { get; set; }

    /// <summary>Raw media timescale (ticks/second) from mdhd; used internally for frame-rate enrichment after parsing.</summary>
    internal uint RawTimescale { get; set; }

    /// <summary>Raw sample delta from the first stts entry; combined with <see cref="RawTimescale"/> to derive frame rate.</summary>
    internal uint RawSampleDelta { get; set; }
}
