namespace ClipMetaCore.Schema;

/// <summary>Constants for the com.peckworkslab.clipmeta metadata schema.</summary>
public static class ClipMetaSchema
{
    /// <summary>Reverse-domain namespace written into every ---- freeform atom.</summary>
    public const string Domain = "com.peckworkslab.clipmeta";

    /// <summary>Schema version field. Written on every write to enable future migrations.</summary>
    public const string Schema = "schema";

    /// <summary>Current schema version value.</summary>
    public const string SchemaVersion = "1";

    /// <summary>Game title field.</summary>
    public const string Game = "game";

    /// <summary>Pipe-separated list of player names.</summary>
    public const string Players = "players";

    /// <summary>Pipe-separated list of tags (lowercase).</summary>
    public const string Tags = "tags";

    /// <summary>Pipe-separated list of timecodes in HH:MM:SS format.</summary>
    public const string Timecode = "timecode";

    /// <summary>Integer rating 1–5.</summary>
    public const string Rating = "rating";

    /// <summary>Freeform notes field.</summary>
    public const string Notes = "notes";

    /// <summary>Pipe-separated fields (values are lists of items).</summary>
    public static readonly IReadOnlySet<string> PipeFields =
        new HashSet<string> { Players, Tags, Timecode };

    /// <summary>Returns the full atom name for a field: "com.peckworkslab.clipmeta:fieldname".</summary>
    public static string AtomName(string field) => $"{Domain}:{field}";
}
