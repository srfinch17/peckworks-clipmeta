namespace ClipMetaCore.Mp4;

/// <summary>A static, clip-independent description of one MP4 box type for structured consumers.</summary>
public sealed class BoxDefinition
{
    /// <summary>Human-readable name, or the raw FourCC when the type is unknown.</summary>
    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>Semantic category. Editable metadata field types report <see cref="BoxCategory.EditableMeta"/>.</summary>
    public BoxCategory Category { get; init; }

    /// <summary>One-line explanation of the box type, or null when undocumented.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Static reference data describing MP4 box types: friendly name, semantic category, and a
/// one-line description. Serves the CLI <c>--definitions</c> dictionary and any structured
/// consumer. This is the single source of the JSON <c>description</c> layer; the ASCII legend
/// keeps its own hand-tuned column strings and is intentionally NOT rendered from here.
/// </summary>
public static class BoxDefinitions
{
    // iTunes/editable field types: the © prefix plus the non-© editable fields the tagger writes.
    private static readonly HashSet<string> EditableFieldTypes = new(StringComparer.Ordinal)
    {
        "desc", "covr", "trkn", "disk", "aART", "tmpo", "cpil", "name",
        "gnre", "ldes", "purd", "cprt", "stik", "rtng", "pgap", "hdvd", "shwm",
    };

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["ftyp"] = "File type: MP4 brand and compatible variants (isom, mp42, M4V).",
        ["moov"] = "Root container for all structure and metadata.",
        ["mvhd"] = "Movie header: total duration, creation date, and playback rate.",
        ["trak"] = "One media stream: video, audio, timecode, or subtitle.",
        ["tkhd"] = "Track header: track ID, flags, duration, and pixel dimensions.",
        ["edts"] = "Edit list container for a track.",
        ["elst"] = "Edit list mapping the presentation timeline to the media timeline.",
        ["mdia"] = "Media-type container for a track.",
        ["mdhd"] = "Media header: per-track timescale, language, and duration.",
        ["hdlr"] = "Handler reference declaring media type: Video, Sound, Timecode, or Text.",
        ["minf"] = "Media information: links the sample table to the track's media type.",
        ["vmhd"] = "Video media header; marks the track as video.",
        ["smhd"] = "Sound media header; marks the track as audio.",
        ["dinf"] = "Data information: where the media data is located.",
        ["dref"] = "Data reference: URL or URN pointing to the media data.",
        ["stbl"] = "Sample table: master index mapping playback time to file offsets.",
        ["stsd"] = "Sample description: codec parameters (avc1=H.264, hvc1=H.265, mp4a=AAC).",
        ["stts"] = "Time-to-sample: duration of each sample in decoding order.",
        ["stss"] = "Sync sample table of keyframes; audio tracks omit this.",
        ["stsc"] = "Sample-to-chunk: groups samples into storage chunks.",
        ["stsz"] = "Sample size: byte size of every individual media sample.",
        ["stco"] = "Chunk offset (32-bit); co64 is the 64-bit form for large files.",
        ["co64"] = "Chunk offset (64-bit).",
        ["udta"] = "User data: optional container for custom or vendor metadata.",
        ["meta"] = "Metadata header (also a FullBox).",
        ["ilst"] = "Item list holding the editable iTunes-style tag fields.",
        ["data"] = "Value payload of a metadata item.",
        ["mean"] = "Namespace name of a freeform (----) metadata atom.",
        ["name"] = "Field name of a freeform (----) atom, or a track/handler label.",
        ["----"] = "Freeform metadata atom (holds custom/extended fields, including clipmeta).",
        ["mdat"] = "Media data: raw encoded audio and video samples, not expanded by this tool.",
        ["free"] = "Free space padding.",
        ["skip"] = "Skip padding.",
        ["Xtra"] = "Windows Media attributes written by Windows File Explorer.",
    };

    /// <summary>Returns the editable-aware category for a box type.</summary>
    /// <param name="type">The FourCC to classify.</param>
    public static BoxCategory CategoryFor(string type)
    {
        if (type.StartsWith("©", StringComparison.Ordinal) || EditableFieldTypes.Contains(type))
            return BoxCategory.EditableMeta;
        return MetadataKeys.GetCategory(type);
    }

    /// <summary>Returns the definition for a single box type; unknown types fall back to the raw FourCC and no description.</summary>
    /// <param name="type">The FourCC to describe.</param>
    public static BoxDefinition GetDefinition(string type) => new()
    {
        FriendlyName = MetadataKeys.GetName(type),
        Category = CategoryFor(type),
        Description = Descriptions.TryGetValue(type, out string? d) ? d : null,
    };

    /// <summary>Returns definitions for every box type with a registered friendly name, keyed by FourCC.</summary>
    public static IReadOnlyDictionary<string, BoxDefinition> AllDefinitions()
    {
        var result = new Dictionary<string, BoxDefinition>(StringComparer.Ordinal);
        foreach (string type in MetadataKeys.All.Keys)
            result[type] = GetDefinition(type);
        return result;
    }
}
