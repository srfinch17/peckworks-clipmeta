using System.Text;
using ClipMetaCore.Abstractions;

namespace ClipMetaCore.Mp4;

/// <summary>
/// Parses an MP4 file's box/atom hierarchy into an in-memory <see cref="BoxNode"/> tree.
/// Reads only box headers and known metadata payloads; never buffers media data (mdat).
/// Implements <see cref="IMediaParser"/> for use with the handler registry.
/// </summary>
public class Mp4Parser : IMediaParser
{
    // Box types that contain child boxes and should be recursed into.
    // Note: "meta" is also a FullBox — the parser consumes version+flags before recursing.
    private static readonly HashSet<string> ContainerTypes = new(StringComparer.Ordinal)
    {
        "moov", "trak", "mdia", "minf", "stbl", "udta", "edts", "dinf", "moof", "traf", "meta",
    };

    // Box types that carry an extra 4-byte version+flags header (FullBox in the spec).
    private static readonly HashSet<string> FullBoxTypes = new(StringComparer.Ordinal)
    {
        "meta", "mvhd", "tkhd", "mdhd", "hdlr", "stsd", "stts", "stsc", "stsz",
        "stco", "co64", "elst", "dref", "smhd", "vmhd", "nmhd",
    };

    // Type-indicator values stored in the data box's flags field.
    private static class DataType
    {
        public const int Utf8 = 1;
        public const int Jpeg = 13;
        public const int Png = 14;
        public const int SignedInt = 21;
        public const int UnsignedInt = 22;
    }

    // Minimum useful box size: 4 bytes size + 4 bytes type.
    private const int MinBoxSize = 8;

    // ── IMediaParser explicit interface implementation ────────────────────────

    /// <inheritdoc/>
    bool IMediaParser.CanParse(string filePath) =>
        Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    BoxNode IMediaParser.ParseFile(string filePath) => ParseFile(filePath);

    // ── Static public API ────────────────────────────────────────────────────

    /// <summary>
    /// Opens <paramref name="path"/> and parses its MP4 box hierarchy.
    /// </summary>
    /// <param name="path">Absolute or relative path to an MP4 file.</param>
    /// <returns>
    /// A synthetic root <see cref="BoxNode"/> whose <c>Children</c> are the file's top-level boxes.
    /// </returns>
    /// <exception cref="InvalidDataException">Thrown when the file cannot be parsed as valid MP4.</exception>
    public static BoxNode ParseFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Parse(fs);
    }

    /// <summary>
    /// Parses the MP4 box hierarchy from an already-open stream, without taking ownership of it.
    /// </summary>
    /// <remarks>
    /// Exists so the write engine can open the source file ONCE — with a share mode that denies
    /// other writers — and keep that same handle for both the parse and the subsequent
    /// byte-copy. If the parse and the copy used separate opens (as <see cref="ParseFile"/>
    /// followed by a second open would), another process could modify the file in between,
    /// and the chunk offsets baked into the output would describe bytes that no longer exist —
    /// e.g. tagging a clip a capture tool is still actively recording.
    /// The stream is left open; its position on return is unspecified.
    /// </remarks>
    /// <param name="fs">A readable, seekable stream positioned anywhere; read from offset 0.</param>
    public static BoxNode Parse(FileStream fs)
    {
        using var reader = new BinaryReader(fs, Encoding.Latin1, leaveOpen: true);

        long fileSize = fs.Length;

        var root = new BoxNode
        {
            Type = "root",
            Size = (ulong)fileSize,
            FileOffset = 0,
            HeaderSize = 0,
        };

        try
        {
            var children = ParseBoxes(reader, 0, fileSize, inIlst: false);
            root.Children.AddRange(children);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Unexpected end of file while parsing MP4 boxes.", ex);
        }

        PostProcessTree(root);
        return root;
    }

    /// <summary>
    /// Iterates through boxes in the byte range [<paramref name="start"/>, <paramref name="end"/>)
    /// and returns them as a list of <see cref="BoxNode"/> objects.
    /// </summary>
    /// <param name="reader">The file reader, positioned at <paramref name="start"/> on entry.</param>
    /// <param name="start">Inclusive start offset within the file.</param>
    /// <param name="end">Exclusive end offset within the file.</param>
    /// <param name="inIlst">
    /// When true the boxes being parsed are direct children of an <c>ilst</c> box (metadata items)
    /// and will be marked editable with their data value extracted.
    /// </param>
    public static List<BoxNode> ParseBoxes(BinaryReader reader, long start, long end, bool inIlst)
    {
        var nodes = new List<BoxNode>();

        reader.BaseStream.Position = start;

        while (reader.BaseStream.Position + MinBoxSize <= end)
        {
            long boxStart = reader.BaseStream.Position;

            BoxHeader header;
            try
            {
                header = BigEndianReader.ReadBoxHeader(reader);
            }
            catch (EndOfStreamException)
            {
                break;
            }

            // Corrupt box: size smaller than its own header. The parser deliberately STOPS here
            // instead of throwing — a damaged file should still be viewable up to the damage.
            // NOTE FOR THE WRITE PATH: stopping means everything after this point is missing
            // from the tree. Mp4Writer.VerifyParseAccountsForWholeFile detects exactly this
            // (parsed boxes won't cover the whole file) and refuses to rewrite, because writing
            // from an incomplete tree would silently drop the unparsed bytes.
            if (header.Size < (ulong)header.HeaderSize)
                break;

            // Guard against extended-size values with the high bit set that would overflow long.
            // Same lenient-stop semantics as above; the write path refuses such files.
            if (header.Size > (ulong)long.MaxValue) break;

            long boxEnd = boxStart + (long)header.Size;
            bool wasClamped = boxEnd > end;
            if (wasClamped) boxEnd = end;

            bool isFullBox = FullBoxTypes.Contains(header.Type);

            // Apple QuickTime sometimes encodes meta without the FullBox prefix.
            // A genuine FullBox meta has version=0, flags=0,0,0 — all four bytes zero.
            // If the first four bytes at the current position are non-zero, they are the
            // start of the first child box (hdlr), not a FullBox prefix.
            if (isFullBox && header.Type == "meta" && reader.BaseStream.Position + 4 <= boxEnd)
            {
                long peekPos = reader.BaseStream.Position;
                byte b0 = reader.ReadByte(), b1 = reader.ReadByte(),
                     b2 = reader.ReadByte(), b3 = reader.ReadByte();
                reader.BaseStream.Position = peekPos;
                if (!(b0 == 0 && b1 == 0 && b2 == 0 && b3 == 0))
                    isFullBox = false; // QT-style meta: no FullBox prefix
            }

            // FullBox requires 4 more bytes (version + flags) beyond the standard header.
            if (isFullBox && (boxEnd - boxStart) < (long)(header.HeaderSize + 4)) break;

            byte version = 0;
            uint flags = 0;

            if (isFullBox)
            {
                version = reader.ReadByte();
                byte f1 = reader.ReadByte(), f2 = reader.ReadByte(), f3 = reader.ReadByte();
                flags = (uint)((f1 << 16) | (f2 << 8) | f3);
            }

            long contentStart = reader.BaseStream.Position;

            var node = new BoxNode
            {
                Type = header.Type,
                // Store the clamped size so FileOffset+Size always points to the true end of box.
                Size = wasClamped ? (ulong)(boxEnd - boxStart) : header.Size,
                FileOffset = boxStart,
                HeaderSize = header.HeaderSize,
                IsFullBox = isFullBox,
                Version = version,
                Flags = flags,
                // Record that the on-disk size field overran its container so the write engine
                // can refuse the file (the viewer happily shows clamped boxes; rewriting them
                // would reproduce a header that lies about its own length).
                WasClamped = wasClamped,
            };

            if (inIlst)
            {
                // This is a metadata item (©nam, ©ART, trkn, ----, etc.) — mark editable and extract value.
                node.IsEditable = true;
                node.EditableKey = header.Type;

                if (header.Type == "----" && contentStart < boxEnd)
                {
                    // Freeform atom: parse mean, name, data children to build full key.
                    var freeformChildren = ParseBoxes(reader, contentStart, boxEnd, inIlst: false);
                    node.Children.AddRange(freeformChildren);

                    string domain = string.Empty, fieldName = string.Empty;
                    foreach (var child in freeformChildren)
                    {
                        if (child.Type == "mean" && child.DisplayValue != null)
                            domain = child.DisplayValue;
                        else if (child.Type == "name" && child.DisplayValue != null)
                            fieldName = child.DisplayValue;
                    }
                    if (domain.Length > 0 && fieldName.Length > 0)
                        node.EditableKey = $"{domain}:{fieldName}";

                    var dataChild = freeformChildren.Find(c => c.Type == "data");
                    if (dataChild != null)
                        ExtractValueFromDataNode(reader, dataChild, node);
                }
                else if (contentStart < boxEnd)
                {
                    var itemChildren = ParseBoxes(reader, contentStart, boxEnd, inIlst: false);
                    node.Children.AddRange(itemChildren);
                    var dataChild = itemChildren.Find(c => c.Type == "data");
                    if (dataChild != null)
                        ExtractValueFromDataNode(reader, dataChild, node);
                }
            }
            else if (header.Type == "ilst")
            {
                if (contentStart < boxEnd)
                {
                    var ilstChildren = ParseBoxes(reader, contentStart, boxEnd, inIlst: true);
                    node.Children.AddRange(ilstChildren);
                }
            }
            else if (header.Type == "stsd")
            {
                // stsd is a FullBox (version+flags already consumed) followed by a 4-byte entry_count,
                // then the codec sample entries (avc1, mp4a, …). Skip the entry_count and recurse.
                if (contentStart + 4 < boxEnd)
                {
                    var stsdChildren = ParseBoxes(reader, contentStart + 4, boxEnd, inIlst: false);
                    node.Children.AddRange(stsdChildren);
                }
            }
            else if (header.Type == "Xtra")
            {
                // Microsoft Xtra box: contains Windows Media (WM/) attributes written by
                // Windows File Explorer. Use a dedicated scanner rather than recursive ParseBoxes
                // because the internal record format is not standard MP4 box structure.
                var xtra = ParseXtraBox(reader, header, contentStart, boxEnd);
                node.Children.AddRange(xtra.Children);
            }
            else if (ContainerTypes.Contains(header.Type))
            {
                if (contentStart < boxEnd)
                {
                    var children = ParseBoxes(reader, contentStart, boxEnd, inIlst: false);
                    node.Children.AddRange(children);
                }
            }
            else
            {
                // Leaf box — try to extract a human-readable display value.
                ExtractLeafValue(reader, node, contentStart, boxEnd);
            }

            // Always seek to the end of this box before processing the next one.
            reader.BaseStream.Position = boxEnd;

            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// Reads the payload of a <c>data</c> box and sets the <c>DisplayValue</c> on its parent metadata item.
    /// </summary>
    /// <param name="reader">The file reader; will be seeked to the data box content.</param>
    /// <param name="dataNode">The parsed <c>data</c> box node whose file offset and size are known.</param>
    /// <param name="parent">The containing metadata item node that will receive <c>DisplayValue</c>.</param>
    private static void ExtractValueFromDataNode(BinaryReader reader, BoxNode dataNode, BoxNode parent)
    {
        // ContentOffset already accounts for FullBox version+flags overhead when present.
        long contentOffset = dataNode.ContentOffset;

        // data box payload: 1 byte version + 3 bytes type-indicator + 4 bytes locale = 8 bytes overhead.
        const int DataBoxOverhead = 8;
        long valueSize = (long)dataNode.Size - dataNode.HeaderSize - DataBoxOverhead;
        if (valueSize <= 0) return;

        reader.BaseStream.Position = contentOffset;

        // version (1 byte) — always 0; type-indicator (3 bytes) packed as 24-bit int.
        reader.ReadByte(); // version
        byte ti1 = reader.ReadByte(), ti2 = reader.ReadByte(), ti3 = reader.ReadByte();
        int typeIndicator = (ti1 << 16) | (ti2 << 8) | ti3;

        reader.ReadBytes(4); // locale — always 0, skip it.

        byte[] payload = reader.ReadBytes((int)Math.Min(valueSize, MaxMetadataPayload));

        parent.DisplayValue = typeIndicator switch
        {
            DataType.Utf8 => $"\"{Encoding.UTF8.GetString(payload)}\"",
            DataType.Jpeg => $"[JPEG image, {valueSize:N0} bytes]",
            DataType.Png  => $"[PNG image, {valueSize:N0} bytes]",
            DataType.SignedInt   => FormatIntegerPayload(parent.Type, payload, signed: true),
            DataType.UnsignedInt => FormatIntegerPayload(parent.Type, payload, signed: false),
            _ => $"[{valueSize:N0} bytes, type={typeIndicator}]",
        };
    }

    /// <summary>Interprets a raw byte payload as a numeric metadata value, with special handling for track numbers.</summary>
    private static string FormatIntegerPayload(string boxType, byte[] payload, bool signed = true)
    {
        if (boxType == "trkn" && payload.Length >= 4)
        {
            // Track number layout: 2 bytes padding, 2 bytes track, 2 bytes total (optional)
            int track = (payload[2] << 8) | payload[3];
            int total = payload.Length >= 6 ? (payload[4] << 8) | payload[5] : 0;
            return total > 0 ? $"{track}/{total}" : $"{track}";
        }
        if (boxType == "disk" && payload.Length >= 4)
        {
            int disc = (payload[2] << 8) | payload[3];
            int total = payload.Length >= 6 ? (payload[4] << 8) | payload[5] : 0;
            return total > 0 ? $"{disc}/{total}" : $"{disc}";
        }

        return payload.Length switch
        {
            1 => payload[0].ToString(),
            2 => ((payload[0] << 8) | payload[1]).ToString(),
            // Unsigned path uses uint arithmetic so high-bit values display correctly.
            >= 4 when !signed => (((uint)payload[0] << 24) | ((uint)payload[1] << 16) | ((uint)payload[2] << 8) | payload[3]).ToString(),
            >= 4 => ((payload[0] << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3]).ToString(),
            _ => BitConverter.ToString(payload),
        };
    }

    /// <summary>
    /// Attempts to extract a human-readable display value from a known leaf box type,
    /// setting <see cref="BoxNode.DisplayValue"/> when successful.
    /// </summary>
    private static void ExtractLeafValue(BinaryReader reader, BoxNode node, long contentStart, long boxEnd)
    {
        long contentSize = boxEnd - contentStart;
        if (contentSize <= 0) return;

        try
        {
            switch (node.Type)
            {
                case "ftyp":
                    // [4 major brand][4 minor version][4+ compatible brands]
                    if (contentSize >= 4)
                    {
                        reader.BaseStream.Position = contentStart;
                        string brand = Encoding.Latin1.GetString(reader.ReadBytes(4)).TrimEnd('\0');
                        node.DisplayValue = $"brand: {brand}";
                    }
                    break;

                case "mean":
                    // FullBox child of ---- freeform atom: 4-byte version+flags prefix, then domain string.
                    if (contentSize > 4)
                    {
                        reader.BaseStream.Position = contentStart + 4;
                        int meanLen = (int)Math.Min(contentSize - 4, 256);
                        node.DisplayValue = Encoding.UTF8.GetString(reader.ReadBytes(meanLen)).TrimEnd('\0');
                    }
                    break;

                case "name":
                    // Two contexts:
                    // 1. ---- freeform child: FullBox prefix (4 zero bytes) before a UTF-8 field name.
                    // 2. QuickTime udta/name: no FullBox prefix; may have a 2-byte language code.
                    if (contentSize > 0)
                    {
                        reader.BaseStream.Position = contentStart;
                        int maxRead = (int)Math.Min(contentSize, 256);
                        byte[] allBytes = reader.ReadBytes(maxRead);
                        // FullBox prefix = version(0) + flags(0,0,0). Detect by checking four zero bytes.
                        if (allBytes.Length >= 4 && allBytes[0] == 0 && allBytes[1] == 0
                            && allBytes[2] == 0 && allBytes[3] == 0)
                        {
                            if (allBytes.Length > 4)
                                node.DisplayValue = Encoding.UTF8.GetString(allBytes, 4, allBytes.Length - 4)
                                                        .TrimEnd('\0').Trim();
                        }
                        else
                        {
                            // QuickTime format — mark as user-editable track/handler name.
                            node.IsEditable = true;
                            node.EditableKey = "name";
                            string direct = Encoding.UTF8.GetString(allBytes).TrimEnd('\0').Trim();
                            string nameVal;
                            if (direct.Length > 0 && direct.All(c => !char.IsControl(c)))
                                nameVal = direct;
                            else if (allBytes.Length > 2)
                                nameVal = Encoding.UTF8.GetString(allBytes, 2, allBytes.Length - 2)
                                              .TrimEnd('\0').Trim();
                            else
                                nameVal = string.Empty;
                            if (nameVal.Length > 0)
                                node.DisplayValue = $"\"{nameVal}\"";
                        }
                    }
                    break;

                case "hdlr":
                    // FullBox; contentStart is already past version+flags.
                    // [4 pre_defined][4 handler_type][12 reserved][null-term name string]
                    if (contentSize >= 8)
                    {
                        reader.BaseStream.Position = contentStart + 4; // skip pre_defined
                        string handlerType = Encoding.Latin1.GetString(reader.ReadBytes(4)).TrimEnd('\0');
                        string typeName = handlerType switch
                        {
                            "vide" => "Video",
                            "soun" => "Sound",
                            "tmcd" => "Timecode",
                            "text" => "Text",
                            "sbtl" => "Subtitle",
                            "meta" => "Metadata",
                            "mdir" => "Metadata (iTunes)",
                            "data" => "Data",
                            _      => handlerType,
                        };
                        // Handler name string starts after 4+4+12 = 20 bytes from contentStart.
                        long nameOffset = contentStart + 20;
                        string suffix = string.Empty;
                        if (nameOffset < boxEnd)
                        {
                            reader.BaseStream.Position = nameOffset;
                            int nameLen = (int)Math.Min(boxEnd - nameOffset, 64);
                            string handlerName = Encoding.UTF8.GetString(reader.ReadBytes(nameLen))
                                                     .TrimEnd('\0').Trim();
                            if (handlerName.Length > 0 && handlerName.All(c => !char.IsControl(c)))
                                suffix = $" — {handlerName}";
                        }
                        node.DisplayValue = $"{typeName}{suffix}";
                    }
                    break;

                case "mvhd":
                    // FullBox; contentStart past version+flags.
                    // v0: [4 create][4 modify][4 timescale][4 duration]
                    // v1: [8 create][8 modify][4 timescale][8 duration]
                    if (node.Version == 0 && contentSize >= 16)
                    {
                        reader.BaseStream.Position = contentStart;
                        uint create0 = BigEndianReader.ReadUInt32(reader);
                        reader.ReadBytes(4); // modify_time
                        uint ts0 = BigEndianReader.ReadUInt32(reader);
                        uint dur0 = BigEndianReader.ReadUInt32(reader);
                        var mvhdParts0 = new List<string>();
                        if (ts0 > 0) mvhdParts0.Add($"duration: {FormatDuration((double)dur0 / ts0)}");
                        string createdStr0 = FormatMacTimestamp(create0);
                        if (createdStr0.Length > 0) mvhdParts0.Add($"created: {createdStr0}");
                        if (mvhdParts0.Count > 0) node.DisplayValue = string.Join(", ", mvhdParts0);
                    }
                    else if (node.Version == 1 && contentSize >= 28)
                    {
                        reader.BaseStream.Position = contentStart;
                        ulong create1 = BigEndianReader.ReadUInt64(reader);
                        reader.ReadBytes(8); // modify_time
                        uint ts1 = BigEndianReader.ReadUInt32(reader);
                        ulong dur1 = BigEndianReader.ReadUInt64(reader);
                        var mvhdParts1 = new List<string>();
                        if (ts1 > 0) mvhdParts1.Add($"duration: {FormatDuration((double)dur1 / ts1)}");
                        string createdStr1 = FormatMacTimestamp(create1);
                        if (createdStr1.Length > 0) mvhdParts1.Add($"created: {createdStr1}");
                        if (mvhdParts1.Count > 0) node.DisplayValue = string.Join(", ", mvhdParts1);
                    }
                    break;

                case "mdhd":
                    // FullBox; contentStart past version+flags.
                    // v0: [4 create][4 modify][4 timescale][4 duration][2 language][2 pre_defined]
                    // v1: [8 create][8 modify][4 timescale][8 duration][2 language][2 pre_defined]
                    if (node.Version == 0 && contentSize >= 18)
                    {
                        reader.BaseStream.Position = contentStart + 8;
                        uint mdTs0 = BigEndianReader.ReadUInt32(reader);
                        uint mdDur0 = BigEndianReader.ReadUInt32(reader);
                        node.RawTimescale = mdTs0;
                        ushort lang0 = BigEndianReader.ReadUInt16(reader);
                        string langStr0 = DecodeMdhdLanguage(lang0);
                        if (mdTs0 > 0)
                            node.DisplayValue = $"duration: {FormatDuration((double)mdDur0 / mdTs0)}{langStr0}";
                    }
                    else if (node.Version == 1 && contentSize >= 30)
                    {
                        reader.BaseStream.Position = contentStart + 16;
                        uint mdTs1 = BigEndianReader.ReadUInt32(reader);
                        ulong mdDur1 = BigEndianReader.ReadUInt64(reader);
                        node.RawTimescale = mdTs1;
                        ushort lang1 = BigEndianReader.ReadUInt16(reader);
                        string langStr1 = DecodeMdhdLanguage(lang1);
                        if (mdTs1 > 0)
                            node.DisplayValue = $"duration: {FormatDuration((double)mdDur1 / mdTs1)}{langStr1}";
                    }
                    break;

                case "tkhd":
                    // FullBox; contentStart past version+flags.
                    // v0: [4 create][4 modify][4 track_id][4 reserved][4 duration]
                    //     [8 reserved][2 layer][2 alt][2 vol][2 reserved][36 matrix]
                    //     [4 width (16.16)][4 height (16.16)]
                    if (node.Version == 0 && contentSize >= 80)
                    {
                        reader.BaseStream.Position = contentStart + 72;
                        uint widthFixed = BigEndianReader.ReadUInt32(reader);
                        uint heightFixed = BigEndianReader.ReadUInt32(reader);
                        int w = (int)(widthFixed >> 16);
                        int h = (int)(heightFixed >> 16);
                        if (w > 0 && h > 0)
                            node.DisplayValue = $"{w}×{h} px";
                    }
                    else if (node.Version == 1 && contentSize >= 92)
                    {
                        reader.BaseStream.Position = contentStart + 84;
                        uint widthFixed1 = BigEndianReader.ReadUInt32(reader);
                        uint heightFixed1 = BigEndianReader.ReadUInt32(reader);
                        int w1 = (int)(widthFixed1 >> 16);
                        int h1 = (int)(heightFixed1 >> 16);
                        if (w1 > 0 && h1 > 0)
                            node.DisplayValue = $"{w1}×{h1} px";
                    }
                    break;

                case "stts":
                    // FullBox; contentStart past version+flags.
                    // [4 entry_count][N × (4 sample_count + 4 sample_delta)]
                    // Compute weighted-average sample delta so VFR video shows correct average FPS.
                    if (contentSize >= 12)
                    {
                        reader.BaseStream.Position = contentStart;
                        uint sttsEntryCount = BigEndianReader.ReadUInt32(reader);
                        long sttsAvailable = (contentSize - 4) / 8;
                        uint sttsToRead = (uint)Math.Min(sttsEntryCount, Math.Min(sttsAvailable, 65536));
                        if (sttsToRead > 0)
                        {
                            ulong totalFrames = 0, totalDeltaSum = 0;
                            for (uint e = 0; e < sttsToRead; e++)
                            {
                                uint sc = BigEndianReader.ReadUInt32(reader);
                                uint sd = BigEndianReader.ReadUInt32(reader);
                                totalFrames += sc;
                                totalDeltaSum += (ulong)sc * sd;
                            }
                            if (totalFrames > 0)
                                node.RawSampleDelta = (uint)(totalDeltaSum / totalFrames);
                        }
                    }
                    break;

                case "avc1":
                case "avc3":
                case "hvc1":
                case "hev1":
                    // Visual sample entry (ISO 14496-12 §12.1.3).
                    // Not a FullBox. Layout from contentStart (= right after 8-byte box header):
                    //   +0  6 bytes reserved
                    //   +6  2 bytes data_reference_index
                    //   +8  2 bytes pre_defined
                    //   +10 2 bytes reserved
                    //   +12 12 bytes pre_defined (3×uint32)
                    //   +24 2 bytes width
                    //   +26 2 bytes height
                    if (contentSize >= 28)
                    {
                        reader.BaseStream.Position = contentStart + 24;
                        ushort vsWidth  = BigEndianReader.ReadUInt16(reader);
                        ushort vsHeight = BigEndianReader.ReadUInt16(reader);
                        if (vsWidth > 0 && vsHeight > 0)
                            node.DisplayValue = $"{vsWidth}×{vsHeight}";
                    }
                    break;

                case "mp4a":
                    // Audio sample entry (ISO 14496-12 §12.2.3).
                    // Not a FullBox. Layout from contentStart:
                    //   +0  6 bytes reserved
                    //   +6  2 bytes data_reference_index
                    //   +8  2 bytes version (QuickTime; 0 = standard ISO)
                    //   +10 6 bytes reserved
                    //   +16 2 bytes channelcount
                    //   +18 2 bytes samplesize
                    //   +20 2 bytes compression_id
                    //   +22 2 bytes packet_size
                    //   +24 4 bytes samplerate (16.16 fixed-point; integer part = Hz)
                    if (contentSize >= 28)
                    {
                        reader.BaseStream.Position = contentStart + 16;
                        ushort channels = BigEndianReader.ReadUInt16(reader);
                        reader.ReadBytes(6); // samplesize, compression_id, packet_size
                        uint srFixed = BigEndianReader.ReadUInt32(reader);
                        uint srHz = srFixed >> 16;
                        if (channels > 0 && srHz > 0)
                        {
                            string srStr = srHz >= 1000
                                ? $"{srHz / 1000.0:F1} kHz"
                                : $"{srHz} Hz";
                            node.DisplayValue = $"{channels} ch, {srStr}";
                        }
                    }
                    break;
            }
        }
        catch (EndOfStreamException) { /* best-effort: leave DisplayValue null */ }
        catch (IOException)          { /* best-effort: leave DisplayValue null */ }
    }

    // Mac/QuickTime epoch: January 1, 1904 00:00:00 UTC.
    private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Converts a Mac epoch timestamp (seconds since 1904-01-01) to a UTC date-time string.</summary>
    private static string FormatMacTimestamp(ulong seconds)
    {
        if (seconds == 0) return string.Empty;
        try
        {
            DateTime dt = MacEpoch.AddSeconds(seconds);
            return dt.ToString("yyyy-MM-dd HH:mm") + " UTC";
        }
        catch { return string.Empty; }
    }

    /// <summary>Walks the parsed tree and enriches nodes with derived values (e.g. frame rate).</summary>
    private static void PostProcessTree(BoxNode root)
    {
        var moov = root.Children.Find(c => c.Type == "moov");
        if (moov == null) return;

        foreach (var trak in moov.Children.Where(c => c.Type == "trak"))
            EnrichTrack(trak);
    }

    /// <summary>Computes frame rate for video tracks by combining mdhd timescale with the stts weighted average delta.</summary>
    private static void EnrichTrack(BoxNode trak)
    {
        var mdia = trak.Children.Find(c => c.Type == "mdia");
        if (mdia == null) return;

        var mdhd = mdia.Children.Find(c => c.Type == "mdhd");
        if (mdhd == null || mdhd.RawTimescale == 0) return;

        var minf = mdia.Children.Find(c => c.Type == "minf");
        if (minf == null) return;

        // Frame rate is only meaningful for video tracks; skip audio (smhd), timecode, etc.
        bool isVideo = minf.Children.Any(c => c.Type == "vmhd");
        if (!isVideo) return;

        var stbl = minf.Children.Find(c => c.Type == "stbl");
        if (stbl == null) return;

        var stts = stbl.Children.Find(c => c.Type == "stts");
        if (stts == null || stts.RawSampleDelta == 0) return;

        double fps = (double)mdhd.RawTimescale / stts.RawSampleDelta;
        string fpsStr = Math.Abs(fps - Math.Round(fps)) < 0.01
            ? $"{(int)Math.Round(fps)} FPS"
            : $"{fps:F3} FPS";
        stts.DisplayValue = fpsStr;
    }

    /// <summary>Formats a duration in seconds as M:SS.ff or H:MM:SS.</summary>
    private static string FormatDuration(double totalSeconds)
    {
        int h = (int)(totalSeconds / 3600);
        int m = (int)((totalSeconds % 3600) / 60);
        double s = totalSeconds % 60;
        return h > 0
            ? $"{h}:{m:D2}:{s:00.##}"
            : $"{m}:{s:00.##}";
    }

    /// <summary>Decodes the packed ISO 639-2/T language code from an mdhd box.</summary>
    private static string DecodeMdhdLanguage(ushort packed)
    {
        if (packed == 0 || packed == 0x7FFF) return string.Empty;
        char c1 = (char)(((packed >> 10) & 0x1F) + 0x60);
        char c2 = (char)(((packed >> 5)  & 0x1F) + 0x60);
        char c3 = (char)((packed         & 0x1F) + 0x60);
        string lang = $"{c1}{c2}{c3}";
        // "und" means undetermined — not worth displaying.
        return lang is "und" or "```" ? string.Empty : $", lang={lang}";
    }

    // WM/ keys written by Windows File Explorer that users can see and edit.
    private static readonly HashSet<string> XtraEditableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "WM/Category", "WM/SubTitle", "WM/Director", "WM/Publisher", "WM/EncodedBy",
        "WM/AlbumArtist", "WM/AlbumTitle", "WM/Composer", "WM/TrackNumber",
        "WM/Year", "WM/Genre", "WM/Description", "WM/Lyrics",
    };

    /// <summary>
    /// Parses a Microsoft <c>Xtra</c> box payload, extracting Windows Media (WM/) attributes
    /// as child <see cref="BoxNode"/> objects with <c>DisplayValue</c> set to the decoded string.
    /// The Xtra format stores names as null-terminated ASCII and values as UTF-16LE preceded
    /// by a two-byte class prefix (0x00 0x08).
    /// </summary>
    private static BoxNode ParseXtraBox(BinaryReader reader, BoxHeader header, long contentStart, long boxEnd)
    {
        var xtraNode = new BoxNode
        {
            Type      = header.Type,
            Size      = header.Size,
            FileOffset = contentStart - header.HeaderSize,
            HeaderSize = header.HeaderSize,
        };

        long payloadSize = boxEnd - contentStart;
        if (payloadSize <= 4) return xtraNode;

        reader.BaseStream.Position = contentStart;
        byte[] payload = reader.ReadBytes((int)Math.Min(payloadSize, MaxMetadataPayload));

        // Scan for WM/ attribute names (ASCII bytes: 0x57 0x4D 0x2F = "WM/").
        // We locate names by pattern rather than parsing the opaque record headers,
        // which use a mix of big-endian and little-endian fields.
        int i = 0;
        while (i < payload.Length - 6)
        {
            // Find next occurrence of "WM/"
            if (!(payload[i] == 0x57 && payload[i + 1] == 0x4D && payload[i + 2] == 0x2F))
            {
                i++;
                continue;
            }

            int nameStart = i;

            // Read name forward until null terminator.
            int nameEnd = nameStart;
            while (nameEnd < payload.Length && payload[nameEnd] != 0)
                nameEnd++;

            if (nameEnd >= payload.Length)
                break;

            string name = Encoding.ASCII.GetString(payload, nameStart, nameEnd - nameStart);

            // Scan up to 16 bytes past the null for the 0x00 0x08 class prefix.
            int scanLimit = Math.Min(nameEnd + 17, payload.Length - 1);
            int classPrefixAt = -1;
            for (int s = nameEnd + 1; s < scanLimit; s++)
            {
                if (payload[s] == 0x00 && payload[s + 1] == 0x08)
                {
                    classPrefixAt = s;
                    break;
                }
            }

            if (classPrefixAt < 0)
            {
                i = nameEnd + 1;
                continue;
            }

            // Value starts 2 bytes after the class prefix (skip 0x00 0x08).
            int valueStart = classPrefixAt + 2;

            // Read UTF-16LE until 0x00 0x00 null terminator (aligned to 2-byte boundary).
            int valueEnd = valueStart;
            while (valueEnd + 1 < payload.Length)
            {
                if (payload[valueEnd] == 0 && payload[valueEnd + 1] == 0)
                    break;
                valueEnd += 2;
            }

            string value = string.Empty;
            int charCount = valueEnd - valueStart;
            if (charCount > 0 && charCount <= MaxMetadataPayload)
                value = Encoding.Unicode.GetString(payload, valueStart, charCount);

            if (!string.IsNullOrWhiteSpace(value))
            {
                bool editable = XtraEditableKeys.Contains(name);
                // FileOffset points to the WM/ name, not to the start of the on-disk Xtra record
                // (which has an opaque length-prefix before the name). The write engine must not use
                // FileOffset/Size on these nodes for in-place overwrites; HasReliableOffsets signals this.
                xtraNode.Children.Add(new BoxNode
                {
                    Type        = name,
                    Size        = (ulong)(valueEnd + 2 - nameStart),
                    FileOffset  = contentStart + nameStart,
                    HeaderSize  = 0,
                    DisplayValue = $"\"{value}\"",
                    IsEditable  = editable,
                    EditableKey = editable ? name : null,
                    HasReliableOffsets = false,
                });
            }

            // Advance past this value's null terminator.
            i = valueEnd + 2;
        }

        return xtraNode;
    }

    // Guard against pathological files with absurdly large metadata strings.
    private const int MaxMetadataPayload = 1024 * 1024; // 1 MB
}
