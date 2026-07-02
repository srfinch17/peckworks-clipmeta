using ClipMetaCore.Mp4;

namespace ClipMetaCore.Schema;

/// <summary>Constants for the com.peckworkslab.clipmeta metadata schema.</summary>
public static class ClipMetaSchema
{
    /// <summary>Reverse-domain namespace written into every ---- freeform atom.</summary>
    public const string Domain = "com.peckworkslab.clipmeta";

    /// <summary>The domain namespace followed by the field separator: "com.peckworkslab.clipmeta:".</summary>
    public const string DomainFieldPrefix = Domain + ":";

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

    /// <summary>Provenance field stamped by the write engine: "who tagged this".</summary>
    public const string TaggedBy = "tagged_by";

    /// <summary>The value written into <see cref="TaggedBy"/> by ClipMeta's own writes.</summary>
    public const string ProvenanceValue = "Peckworks ClipMeta";

    /// <summary>Pipe-separated fields (values are lists of items).</summary>
    public static readonly IReadOnlySet<string> PipeFields =
        new HashSet<string> { Players, Tags, Timecode };

    /// <summary>
    /// Free-text fields appended as prose, joined with a space, case preserved, no dedup, rather
    /// than as deduplicated pipe lists. Drives the append join in <c>Normalizer.AppendValue</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> ProseFields = new HashSet<string> { Notes };

    /// <summary>
    /// Fields the deferred-tag queue ACCUMULATES on a re-tag instead of overwriting, so two spoken
    /// narrations of the same clip both survive (notes append as prose; tags/players pipe-merge).
    /// Every other field (game, rating, timecode, custom) is last-wins / replace.
    /// </summary>
    public static readonly IReadOnlySet<string> QueueAppendFields = new HashSet<string> { Notes, Tags, Players };

    /// <summary>All well-known user-facing fields, in canonical display order.</summary>
    public static readonly IReadOnlyList<string> KnownFields =
        [Game, Players, Tags, Timecode, Rating, Notes];

    /// <summary>
    /// True for write-engine bookkeeping fields that live in the file but are not user metadata
    /// (<see cref="Schema"/> and the <see cref="TaggedBy"/> provenance stamp). User-facing read
    /// paths (stats, export, index, MCP tools) exclude these; the raw <c>--list</c> / tree view
    /// deliberately does not, so provenance is still discoverable on inspection.
    /// </summary>
    public static bool IsInternal(string field) =>
        Schema.Equals(field, StringComparison.Ordinal) ||
        TaggedBy.Equals(field, StringComparison.Ordinal);

    /// <summary>Returns the full atom name for a field: "com.peckworkslab.clipmeta:fieldname".</summary>
    public static string AtomName(string field) => $"{Domain}:{field}";

    /// <summary>
    /// True when <paramref name="node"/> is a freeform ("----") atom whose key is in the
    /// clipmeta domain namespace. This is the INTRINSIC clipmeta-atom test only: it carries no
    /// location scoping and no display-value requirement, so it is safe to share with the box-tree
    /// mapper without altering the reader's or the write gate's own (deliberately different) checks.
    /// </summary>
    /// <param name="node">The parsed box node to test.</param>
    public static bool IsClipmetaFreeformAtom(BoxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Type == "----"
            && node.EditableKey is not null
            && node.EditableKey.StartsWith(DomainFieldPrefix, StringComparison.Ordinal);
    }
}
