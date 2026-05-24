namespace ClipMetaCore.Mp4;

/// <summary>Represents the parsed header of an MP4 box/atom.</summary>
/// <param name="Size">Total box size in bytes, including this header. Adjusted for size==0 (to-EOF) and size==1 (extended) cases.</param>
/// <param name="Type">Four-character box type (FourCC), decoded with Latin-1 to preserve the 0xA9 © byte.</param>
/// <param name="HeaderSize">Bytes consumed by the header: 8 for normal boxes, 16 when the extended size field is present.</param>
public readonly record struct BoxHeader(
    ulong Size,
    string Type,
    int HeaderSize
);
