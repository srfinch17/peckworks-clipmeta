namespace ClipMetaCore.Mp4;

/// <summary>Visual category used by the renderer to color-code box nodes.</summary>
public enum BoxCategory
{
    /// <summary>Container box that organizes child boxes (moov, trak, mdia…).</summary>
    Structural,
    /// <summary>Codec/sample-table internals not meaningful to end users (stbl, stts…).</summary>
    Technical,
    /// <summary>Fixed-format header with a parseable technical value (mvhd, hdlr, ftyp…).</summary>
    Header,
    /// <summary>User-editable metadata field (ilst children, name…).</summary>
    EditableMeta,
    /// <summary>Raw media data container (mdat).</summary>
    Media,
    /// <summary>Microsoft Windows Media / Xtra box attribute.</summary>
    WindowsMedia,
    /// <summary>Unrecognized or unclassified box.</summary>
    Unknown,
}

/// <summary>Maps MP4 FourCC codes to human-readable display names.</summary>
public static class MetadataKeys
{
    private static readonly Dictionary<string, string> Names = new()
    {
        // Top-level structural boxes
        ["ftyp"] = "File Type",
        ["mdat"] = "Media Data",
        ["free"] = "Free Space",
        ["skip"] = "Skip",
        ["wide"] = "Wide",
        ["pnot"] = "Preview",
        ["junk"] = "Junk",

        // Movie container
        ["moov"] = "Movie",
        ["mvhd"] = "Movie Header",
        ["iods"] = "Object Descriptor",

        // Track
        ["trak"] = "Track",
        ["tkhd"] = "Track Header",
        ["edts"] = "Edit List Container",
        ["elst"] = "Edit List",

        // Media
        ["mdia"] = "Media",
        ["mdhd"] = "Media Header",
        ["hdlr"] = "Handler",
        ["minf"] = "Media Info",

        // Media info
        ["vmhd"] = "Video Media Header",
        ["smhd"] = "Sound Media Header",
        ["nmhd"] = "Null Media Header",
        ["hmhd"] = "Hint Media Header",
        ["sthd"] = "Subtitle Media Header",
        ["dinf"] = "Data Info",
        ["dref"] = "Data Reference",
        ["url "] = "Data Entry URL",
        ["urn "] = "Data Entry URN",

        // Sample table
        ["stbl"] = "Sample Table",
        ["stsd"] = "Sample Description",
        ["stts"] = "Time to Sample",
        ["stss"] = "Sync Sample",
        ["stsc"] = "Sample to Chunk",
        ["stsz"] = "Sample Size",
        ["stco"] = "Chunk Offset",
        ["co64"] = "Chunk Offset 64-bit",
        ["ctts"] = "Composition Time Offset",
        ["stsh"] = "Shadow Sync",
        ["padb"] = "Padding Bits",
        ["sdtp"] = "Sample Dependency Flags",

        // Movie fragment
        ["moof"] = "Movie Fragment",
        ["mfhd"] = "Movie Fragment Header",
        ["traf"] = "Track Fragment",
        ["tfhd"] = "Track Fragment Header",
        ["tfdt"] = "Track Fragment Decode Time",
        ["trun"] = "Track Run",
        ["mfra"] = "Movie Fragment Random Access",
        ["tfra"] = "Track Fragment Random Access",
        ["mfro"] = "Movie Fragment Random Access Offset",
        ["sidx"] = "Segment Index",

        // User data / metadata
        ["udta"] = "User Data",
        ["meta"] = "Metadata",
        ["ilst"] = "Metadata Items",
        ["data"] = "Data",
        ["mean"] = "Mean",
        ["name"] = "Name",

        // iTunes metadata keys (© prefix is 0xA9, encoded as © in Latin-1 strings)
        ["©nam"] = "Title",
        ["©ART"] = "Artist",
        ["©alb"] = "Album",
        ["©day"] = "Year",
        ["©cmt"] = "Comment",
        ["©gen"] = "Genre",
        ["©lyr"] = "Lyrics",
        ["©too"] = "Encoder",
        ["©wrt"] = "Composer",
        ["©grp"] = "Grouping",
        ["©mvn"] = "Movement Name",
        ["©mvc"] = "Movement Count",
        ["©mvi"] = "Movement Number",

        // Numeric / boolean metadata
        ["trkn"] = "Track Number",
        ["disk"] = "Disc Number",
        ["tmpo"] = "BPM",
        ["cpil"] = "Compilation",
        ["pgap"] = "Gapless Playback",
        ["hdvd"] = "HD Video",
        ["stik"] = "Media Type",
        ["rtng"] = "Rating",
        ["shwm"] = "Show Movement",

        // Descriptive metadata
        ["desc"] = "Description",
        ["ldes"] = "Long Description",
        ["covr"] = "Cover Art",
        ["gnre"] = "Genre (ID3)",
        ["aART"] = "Album Artist",
        ["soal"] = "Sort Album",
        ["soar"] = "Sort Artist",
        ["sonm"] = "Sort Name",
        ["sosn"] = "Sort Show",
        ["soco"] = "Sort Composer",
        ["soaa"] = "Sort Album Artist",

        // Purchase / store
        ["purd"] = "Purchase Date",
        ["cprt"] = "Copyright",
        ["apID"] = "Apple ID",
        ["akID"] = "iTunes Account Type",
        ["atID"] = "Artist ID",
        ["plID"] = "Playlist ID",
        ["cnID"] = "Catalog ID",
        ["geID"] = "Genre ID",
        ["sfID"] = "Store Front ID",
        ["xid "] = "Extended ID",

        // TV / podcast
        ["tvsh"] = "TV Show",
        ["tvsn"] = "TV Season",
        ["tves"] = "TV Episode",
        ["tvnn"] = "TV Network",
        ["tven"] = "TV Episode ID",
        ["purl"] = "Podcast URL",
        ["egid"] = "Episode Global ID",
        ["catg"] = "Category",
        ["keyw"] = "Keywords",
        ["pcst"] = "Podcast",

        // Codec / technical
        ["avc1"] = "H.264 Video",
        ["hvc1"] = "H.265 Video",
        ["mp4a"] = "AAC Audio",
        ["ac-3"] = "AC-3 Audio",
        ["ec-3"] = "E-AC-3 Audio",
        ["flvr"] = "Codec Flavor",
        ["uuid"] = "UUID Extension",

        // Windows Media / Microsoft Xtra box
        ["Xtra"]                    = "Windows Media Attributes",
        ["WM/Category"]             = "Tags",
        ["WM/SubTitle"]             = "Subtitle",
        ["WM/Director"]             = "Director",
        ["WM/Publisher"]            = "Publisher",
        ["WM/EncodedBy"]            = "Encoded By",
        ["WM/UniqueFileIdentifier"] = "Unique ID",
        ["WM/AlbumArtist"]          = "Album Artist",
        ["WM/AlbumTitle"]           = "Album",
        ["WM/Composer"]             = "Composer",
        ["WM/TrackNumber"]          = "Track Number",
        ["WM/Year"]                 = "Year",
        ["WM/Genre"]                = "Genre",
        ["WM/Description"]          = "Description",
        ["WM/Lyrics"]               = "Lyrics",
        ["WM/ContentDistributor"]   = "Distributor",
        ["WM/Provider"]             = "Provider",
        ["WM/ProviderRating"]       = "Rating",
        ["WM/BeatsPerMinute"]       = "BPM",
        ["WM/InitialKey"]           = "Initial Key",
        ["WM/Mood"]                 = "Mood",
        ["WM/ContentGroupDescription"] = "Content Group",
        ["WM/SubTitleDescription"]  = "Subtitle Description",
    };

    /// <summary>Returns the friendly display name for a FourCC, or the raw FourCC string if the type is unknown.</summary>
    /// <param name="fourCC">The four-character code to look up.</param>
    /// <returns>A human-readable name, or <paramref name="fourCC"/> when not found.</returns>
    public static string GetName(string fourCC)
        => Names.TryGetValue(fourCC, out var name) ? name : fourCC;

    /// <summary>Returns true when <paramref name="fourCC"/> has a registered friendly name.</summary>
    public static bool IsKnown(string fourCC) => Names.ContainsKey(fourCC);

    /// <summary>The complete mapping of FourCC codes to friendly names.</summary>
    public static IReadOnlyDictionary<string, string> All => Names;

    private static readonly HashSet<string> StructuralSet = new(StringComparer.Ordinal)
    {
        "moov", "trak", "mdia", "minf", "udta", "edts", "dinf", "moof", "traf", "ilst", "meta",
    };

    private static readonly HashSet<string> TechnicalSet = new(StringComparer.Ordinal)
    {
        "stbl", "stts", "stss", "stsc", "stsz", "stco", "co64", "stsd", "dref",
        "ctts", "stsh", "padb", "sdtp", "mfhd", "tfhd", "tfdt", "trun", "elst",
        "mfra", "tfra", "mfro", "sidx",
        "avc1", "avc3", "hvc1", "hev1", "mp4a", "ac-3", "ec-3",
        "avcC", "hvcC", "esds", "colr", "pasp", "btrt",
    };

    private static readonly HashSet<string> HeaderSet = new(StringComparer.Ordinal)
    {
        "mvhd", "tkhd", "mdhd", "hdlr", "ftyp", "vmhd", "smhd", "nmhd", "hmhd",
        "name", "iods", "url ", "urn ", "mean",
    };

    private static readonly HashSet<string> WindowsMediaSet = new(StringComparer.Ordinal)
    {
        "Xtra",
        "WM/Category", "WM/SubTitle", "WM/Director", "WM/Publisher", "WM/EncodedBy",
        "WM/UniqueFileIdentifier", "WM/AlbumArtist", "WM/AlbumTitle", "WM/Composer",
        "WM/TrackNumber", "WM/Year", "WM/Genre", "WM/Description", "WM/Lyrics",
        "WM/ContentDistributor", "WM/Provider", "WM/ProviderRating", "WM/BeatsPerMinute",
        "WM/InitialKey", "WM/Mood", "WM/ContentGroupDescription", "WM/SubTitleDescription",
    };

    /// <summary>Returns true when the key is a Windows Media (WM/) or Xtra-box attribute.</summary>
    public static bool IsWindowsMedia(string fourCC) => WindowsMediaSet.Contains(fourCC) || fourCC.StartsWith("WM/", StringComparison.Ordinal);

    /// <summary>Returns the visual category for a FourCC, used by the renderer for color coding.</summary>
    public static BoxCategory GetCategory(string fourCC)
    {
        if (StructuralSet.Contains(fourCC)) return BoxCategory.Structural;
        if (TechnicalSet.Contains(fourCC)) return BoxCategory.Technical;
        if (HeaderSet.Contains(fourCC)) return BoxCategory.Header;
        if (fourCC == "mdat") return BoxCategory.Media;
        if (IsWindowsMedia(fourCC)) return BoxCategory.WindowsMedia;
        return BoxCategory.Unknown;
    }
}
